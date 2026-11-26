using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YingShai.Core.Imaging;

/// <summary>一次解码的结果（已应用 EXIF 方向，已冻结，可跨线程）。</summary>
public sealed class DecodedImage
{
    public required BitmapSource Bitmap { get; init; }
    /// <summary>原始像素宽（未旋转）。</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public int Orientation { get; init; } = 1;
    public ExifFields Exif { get; init; } = new();
}

/// <summary>
/// WIC（Windows Imaging Component）相关操作：按目标尺寸解码（走 DecodePixelWidth，避免把 45MP 整张载入内存）、
/// 应用 EXIF 方向、编码 JPEG、读 EXIF 字段。全部返回冻结的 BitmapSource，可跨线程使用。
/// </summary>
public static class WicImage
{
    /// <summary>
    /// 按最大边 maxSide 解码（等比缩放）。
    /// 关键点：先只读文件头取尺寸，再让 WIC 用 DecodePixelWidth/Height 在解码阶段缩放，绝不整张载入再缩。
    /// </summary>
    public static DecodedImage DecodeScaled(byte[] data, int maxSide)
    {
        int peekW = 0, peekH = 0, orientation = 1;
        ExifFields exif = new();
        try
        {
            using var peek = new MemoryStream(data, false);
            var dec = BitmapDecoder.Create(peek, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frame0 = dec.Frames[0];
            peekW = frame0.PixelWidth;
            peekH = frame0.PixelHeight;
            var md = frame0.Metadata as BitmapMetadata;
            orientation = ExifReader.ReadOrientation(md);
            exif = ExifReader.Read(md);
        }
        catch
        {
            // 头部读不出来时退化为原尺寸硬解
        }

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(data, false);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (peekW > 0 && peekH > 0 && Math.Max(peekW, peekH) > maxSide)
        {
            if (peekW >= peekH) bi.DecodePixelWidth = maxSide;
            else bi.DecodePixelHeight = maxSide;
        }
        bi.EndInit();
        bi.Freeze();

        // 重要：BitmapImage 会一直引用 StreamSource 指向的 MemoryStream（大 NEF 的内嵌 JPEG 有 1~8MB，
        // 2000 张就是几个 GB）。复制像素到独立的 WriteableBitmap 后再丢弃 BitmapImage，内存才真正释放。
        var pixels = CopyPixels(bi);

        var oriented = ApplyOrientation(pixels, orientation);
        return new DecodedImage
        {
            Bitmap = oriented,
            SourceWidth = peekW > 0 ? peekW : bi.PixelWidth,
            SourceHeight = peekH > 0 ? peekH : bi.PixelHeight,
            Orientation = orientation,
            Exif = exif
        };
    }

    /// <summary>把位图的像素复制成独立对象，切断它对解码流/源的引用。</summary>
    public static BitmapSource CopyPixels(BitmapSource src)
    {
        int bpp = (src.Format.BitsPerPixel + 7) / 8;
        int stride = src.PixelWidth * bpp;
        var buffer = new byte[stride * src.PixelHeight];
        src.CopyPixels(buffer, stride, 0);
        // 统一成 96 DPI：RAW 内嵌 JPEG 常带 300dpi 的 JFIF 密度，
        // 若照搬，WPF 会按物理尺寸显示（1024px 变成 328 DIP），大图会莫名其妙变小。
        var bs = BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96,
            src.Format, src.Palette, buffer, stride);
        bs.Freeze();
        return bs;
    }

    /// <summary>从文件解码（非 RAW：JPG/PNG/TIFF/WebP/BMP...）。</summary>
    public static DecodedImage DecodeFileScaled(string path, int maxSide)
        => DecodeScaled(File.ReadAllBytes(AppPaths.ToLongPath(path)), maxSide);

    /// <summary>只读文件头拿尺寸与方向（零解码，用于信息栏与 100% 视图规划）。</summary>
    public static bool TryReadSizeAndOrientation(string path, out int w, out int h, out int orientation)
    {
        w = h = 0; orientation = 1;
        try
        {
            using var fs = new FileStream(AppPaths.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var f = dec.Frames[0];
            w = f.PixelWidth; h = f.PixelHeight;
            orientation = ExifReader.ReadOrientation(f.Metadata as BitmapMetadata);
            return true;
        }
        catch { return false; }
    }

    /// <summary>把任意尺寸的位图等比缩到最大边 maxSide（用于 LibRaw 内存位图缩略）。</summary>
    public static BitmapSource ScaleToMaxSide(BitmapSource src, int maxSide)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
        if (scale >= 1.0) return src;
        var tb = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        tb.Freeze();
        return tb;
    }

    /// <summary>按 EXIF 方向（1..8）旋转/镜像。</summary>
    public static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        if (orientation <= 1 || orientation > 8) return src;
        var g = new TransformGroup();
        switch (orientation)
        {
            case 2: g.Children.Add(new ScaleTransform(-1, 1)); break;                 // 水平镜像
            case 3: g.Children.Add(new RotateTransform(180)); break;
            case 4: g.Children.Add(new ScaleTransform(1, -1)); break;                 // 垂直镜像
            case 5: g.Children.Add(new ScaleTransform(-1, 1)); g.Children.Add(new RotateTransform(90)); break;
            case 6: g.Children.Add(new RotateTransform(90)); break;
            case 7: g.Children.Add(new ScaleTransform(-1, 1)); g.Children.Add(new RotateTransform(270)); break;
            case 8: g.Children.Add(new RotateTransform(270)); break;
        }
        g.Freeze();
        var tb = new TransformedBitmap(src, g);
        tb.Freeze();
        return tb;
    }

    /// <summary>JPEG 编码（用于磁盘缩略图缓存）。</summary>
    public static byte[] EncodeJpeg(BitmapSource src, int quality = 88)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 30, 100) };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>降饱和（已标记项在缩略图条里显示为灰调）。</summary>
    public static BitmapSource Desaturate(BitmapSource src)
    {
        var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        return gray;
    }

    /// <summary>
    /// 把 24 位 RGB 数据（LibRaw 内存位图）转成 BitmapSource。
    /// 注意：这里必须用 WriteableBitmap + WritePixels，不能用 BitmapSource.Create——
    /// 后者对几十 MB 的大位图会在原生层抛 E_INVALIDARG（RAF 48MB 实测），
    /// 而 WriteableBitmap 路径对 73MB（NEF 全解）实测可用。统一按 96 DPI 创建，
    /// DPI 归一化逻辑与 CopyPixels 一致。
    /// </summary>
    public static BitmapSource FromRgb24(IntPtr data, int width, int height, int stride)
    {
        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Rgb24, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), data, stride * height, stride);
        wb.Freeze();
        return wb;
    }

    public static BitmapSource FromRgb24(byte[] data, int width, int height, int stride)
    {
        var bs = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, data, height * stride);
        bs.Freeze();
        return bs;
    }

    /// <summary>估算 BitmapSource 占用的字节数（用于内存 LRU 预算）。</summary>
    public static long EstimateBytes(BitmapSource? bmp)
    {
        if (bmp == null) return 0;
        try { return Math.Max(1, (long)bmp.PixelWidth * bmp.PixelHeight * ((bmp.Format.BitsPerPixel + 7) / 8)); }
        catch { return 0; }
    }
}
