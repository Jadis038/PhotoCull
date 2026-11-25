using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YingShai.Core.Model;

namespace YingShai.Core.Imaging;

/// <summary>LibRaw 解出来的内嵌预览（可能是 JPEG 字节，也可能是未压缩 RGB 位图）。</summary>
public sealed class EmbeddedPreview
{
    public required LibRawImageKind Kind { get; init; }
    /// <summary>JPEG 字节（Kind=Jpeg 时有效）。</summary>
    public byte[]? JpegData { get; init; }
    /// <summary>RGB24 像素（Kind=Bitmap 时有效，stride = Width*3）。</summary>
    public byte[]? RgbData { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>LibRaw 报告的原始字节数（用于诊断）。</summary>
    public int RawSize { get; init; }
    /// <summary>从 open 到拿到预览的耗时（毫秒）。</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>
/// 一次 libraw_open_file 的会话：元数据 + 内嵌预览 + 可选的全尺寸 demosaic。
/// 严格遵守「每个文件只打开一次」：元数据和预览都从这一次打开里取。
/// </summary>
public sealed class LibRawImage : IDisposable
{
    private IntPtr _lr;
    private readonly string _path;
    private bool _previewTaken;

    private LibRawImage(IntPtr lr, string path)
    {
        _lr = lr;
        _path = path;
    }

    /// <summary>LibRaw 支持的 RAW 扩展名（不含 X3F：LibRaw 0.21 不认这两个样本，见测试矩阵）。</summary>
    public static readonly string[] RawExtensions =
    {
        ".nef", ".nrw", ".cr2", ".cr3", ".crw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2",
        ".pef", ".dng", ".srw", ".raw", ".rwl", ".3fr", ".fff", ".dcr", ".kdc", ".mef", ".mos",
        ".mrw", ".erf", ".x3f", ".iiq", ".cap", ".pxn", ".r3d", ".qtk", ".ari", ".srf", ".braw"
    };

    public static bool IsRawExtension(string ext) =>
        RawExtensions.Contains(ext.ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>打开文件（不抛异常，失败返回 false 并给出中文错误）。</summary>
    public static bool TryOpen(string path, out LibRawImage? image, out string error)
    {
        image = null;
        error = string.Empty;
        IntPtr lr = IntPtr.Zero;
        try
        {
            lr = LibRawInterop.libraw_init(0);
            if (lr == IntPtr.Zero) { error = "LibRaw 初始化失败"; return false; }
            // 长路径 / 中文路径都用宽字符 API，Win32 天然支持 \\?\ 前缀
            var p = AppPaths.ToLongPath(path);
            int rc = LibRawInterop.libraw_open_wfile(lr, p);
            if (rc != 0)
            {
                error = $"无法解析该 RAW：{LibRawInterop.ErrorText(rc)}";
                LibRawInterop.libraw_close(lr);
                return false;
            }
            image = new LibRawImage(lr, path);
            return true;
        }
        catch (Exception ex)
        {
            if (lr != IntPtr.Zero) LibRawInterop.libraw_close(lr);
            error = "LibRaw 原生调用异常：" + ex.Message;
            return false;
        }
    }

    /// <summary>从已打开的会话里读元数据（不需要 unpack，几乎零成本）。</summary>
    public PhotoMetadata ReadMetadata()
    {
        var ip = LibRawInterop.GetIParams(_lr);
        var ot = LibRawInterop.GetImgOther(_lr);
        var lens = LibRawInterop.GetLensInfo(_lr);

        DateTime? taken = null;
        if (ot.timestamp > 0)
        {
            try { taken = DateTimeOffset.FromUnixTimeSeconds(ot.timestamp).LocalDateTime; } catch { }
        }

        int w = 0, h = 0;
        try { w = LibRawInterop.libraw_get_iwidth(_lr); h = LibRawInterop.libraw_get_iheight(_lr); } catch { }

        string? lensName = LibRawInterop.CStr(lens.Lens);
        if (string.IsNullOrWhiteSpace(lensName)) lensName = null;

        return new PhotoMetadata
        {
            Make = NullIfEmpty(LibRawInterop.CStr(ip.make)),
            Model = NullIfEmpty(LibRawInterop.CStr(ip.model)),
            Lens = lensName,
            FocalLengthMm = ot.focal_len > 0 ? ot.focal_len : null,
            ApertureF = ot.aperture > 0 ? ot.aperture : null,
            ShutterSeconds = ot.shutter > 0 ? ot.shutter : null,
            Iso = ot.iso_speed > 0 ? (int)Math.Round(ot.iso_speed) : null,
            TakenAt = taken,
            PixelWidth = w > 0 ? w : null,
            PixelHeight = h > 0 ? h : null,
        };
    }

    /// <summary>
    /// 抽取内嵌预览。这是缩略图的唯一来源：一次 unpack_thumb + make_mem_thumb，通常 5~40ms。
    /// </summary>
    public EmbeddedPreview? TryReadPreview(out string error)
    {
        error = string.Empty;
        if (_previewTaken) { error = "预览已经读取过"; return null; }
        _previewTaken = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int rc = LibRawInterop.libraw_unpack_thumb(_lr);
        if (rc != 0)
        {
            error = LibRawInterop.ErrorText(rc);
            return null;
        }
        IntPtr mem = LibRawInterop.libraw_dcraw_make_mem_thumb(_lr, out int err);
        if (mem == IntPtr.Zero)
        {
            error = LibRawInterop.ErrorText(err);
            return null;
        }
        try
        {
            var img = LibRawInterop.GetProcessedImage(mem);
            // 像素/JPEG 数据是内联在结构体末尾的（不是指针），偏移 16 字节
            IntPtr dataPtr = IntPtr.Add(mem, LibRawProcessedImageLayout.DataOffset);
            sw.Stop();
            if (img.data_size == 0 || img.data_size > 400u * 1024 * 1024)
            {
                error = $"内嵌预览大小异常（{img.data_size} 字节）";
                return null;
            }
            if (img.type == (int)LibRawImageKind.Jpeg)
            {
                var bytes = new byte[img.data_size];
                Marshal.Copy(dataPtr, bytes, 0, bytes.Length);
                return new EmbeddedPreview
                {
                    Kind = LibRawImageKind.Jpeg,
                    JpegData = bytes,
                    Width = img.width,
                    Height = img.height,
                    RawSize = (int)img.data_size,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
            if (img.type == (int)LibRawImageKind.Bitmap)
            {
                int stride = img.width * 3;
                var bytes = new byte[stride * img.height];
                Marshal.Copy(dataPtr, bytes, 0, bytes.Length);
                return new EmbeddedPreview
                {
                    Kind = LibRawImageKind.Bitmap,
                    RgbData = bytes,
                    Width = img.width,
                    Height = img.height,
                    RawSize = (int)img.data_size,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
            error = $"未知的内嵌预览类型 {img.type}";
            return null;
        }
        finally
        {
            LibRawInterop.libraw_dcraw_clear_mem(mem);
        }
    }

    /// <summary>
    /// 完整解码（demosaic）。只在用户放大到 100% 或需要像素级细节时调用，必须放后台线程。
    /// 返回按相机方向摆正的 BitmapSource（LibRaw 自己处理 flip）。
    /// </summary>
    public BitmapSource? TryDecodeFull(out string error, int demosaicQuality = 3)
    {
        error = string.Empty;
        int rc = LibRawInterop.libraw_unpack(_lr);
        if (rc != 0) { error = LibRawInterop.ErrorText(rc); return null; }

        LibRawInterop.libraw_set_output_bps(_lr, 8);
        LibRawInterop.libraw_set_output_color(_lr, 1);   // sRGB
        LibRawInterop.libraw_set_demosaic(_lr, demosaicQuality);

        rc = LibRawInterop.libraw_dcraw_process(_lr);
        if (rc != 0) { error = LibRawInterop.ErrorText(rc); return null; }

        IntPtr mem = LibRawInterop.libraw_dcraw_make_mem_image(_lr, out int err);
        if (mem == IntPtr.Zero) { error = LibRawInterop.ErrorText(err); return null; }
        try
        {
            var img = LibRawInterop.GetProcessedImage(mem);
            if (img.type != (int)LibRawImageKind.Bitmap)
            {
                error = "全尺寸解码没有返回位图";
                return null;
            }
            int stride = img.width * 3;
            IntPtr dataPtr = IntPtr.Add(mem, LibRawProcessedImageLayout.DataOffset);
            var bs = WicImage.FromRgb24(dataPtr, img.width, img.height, stride);
            return bs;
        }
        finally
        {
            LibRawInterop.libraw_dcraw_clear_mem(mem);
            LibRawInterop.libraw_free_image(_lr);
            LibRawInterop.libraw_recycle(_lr);
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose()
    {
        if (_lr != IntPtr.Zero)
        {
            try { LibRawInterop.libraw_close(_lr); } catch { }
            _lr = IntPtr.Zero;
        }
    }
}
