using System.Runtime.InteropServices;
using System.Text;

namespace YingShai.Core.Imaging;

/// <summary>
/// LibRaw 0.21.1 的 C API 最小封装。只绑定我们真正用到的函数。
/// 结构体布局严格对照 libraw_types.h（0.21.1），字段顺序/类型不可随意改动。
/// 原生库名：raw_r.dll（随程序分发，LGPL-2.1 / CDDL-1.0）。
/// </summary>
internal static class LibRawInterop
{
    private const string Dll = "raw_r.dll";
    private const CallingConvention CC = CallingConvention.Cdecl;

    [DllImport(Dll, CallingConvention = CC)] internal static extern IntPtr libraw_init(uint flags);
    [DllImport(Dll, CallingConvention = CC, CharSet = CharSet.Unicode)]
    internal static extern int libraw_open_wfile(IntPtr lr, [MarshalAs(UnmanagedType.LPWStr)] string path);
    [DllImport(Dll, CallingConvention = CC)] internal static extern int libraw_unpack(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern int libraw_unpack_thumb(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr lr, out int err);
    [DllImport(Dll, CallingConvention = CC)] internal static extern IntPtr libraw_dcraw_make_mem_image(IntPtr lr, out int err);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_dcraw_clear_mem(IntPtr img);
    [DllImport(Dll, CallingConvention = CC)] internal static extern int libraw_dcraw_process(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_close(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_recycle(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_free_image(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_strerror(int code);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_version();
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_get_iparams(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_get_imgother(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_get_lensinfo(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern int libraw_get_iwidth(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern int libraw_get_iheight(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_set_demosaic(IntPtr lr, int value);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_set_output_bps(IntPtr lr, int value);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_set_output_color(IntPtr lr, int value);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_set_no_auto_bright(IntPtr lr, int value);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_set_gamma(IntPtr lr, int index, float value);
    [DllImport(Dll, CallingConvention = CC)] internal static extern void libraw_set_user_mul(IntPtr lr, int index, float value);
    [DllImport(Dll, CallingConvention = CC)] internal static extern int libraw_get_color_maximum(IntPtr lr);

    /// <summary>LibRaw 版本串，例如 "0.21.1-Release"。</summary>
    public static string Version
    {
        get
        {
            var p = libraw_version();
            return p == IntPtr.Zero ? "?" : (Marshal.PtrToStringAnsi(p) ?? "?");
        }
    }

    public static string ErrorText(int code)
    {
        if (code == 0) return "OK";
        var p = libraw_strerror(code);
        return p == IntPtr.Zero ? $"错误码 {code}" : (Marshal.PtrToStringAnsi(p) ?? $"错误码 {code}");
    }

    public static LibRawIParams GetIParams(IntPtr lr) => Marshal.PtrToStructure<LibRawIParams>(libraw_get_iparams(lr));
    public static LibRawImgOther GetImgOther(IntPtr lr) => Marshal.PtrToStructure<LibRawImgOther>(libraw_get_imgother(lr));
    public static LibRawLensInfo GetLensInfo(IntPtr lr) => Marshal.PtrToStructure<LibRawLensInfo>(libraw_get_lensinfo(lr));
    public static LibRawProcessedImage GetProcessedImage(IntPtr p) => Marshal.PtrToStructure<LibRawProcessedImage>(p);

    /// <summary>读 C 字符串（char[]，UTF-8 编码，0 结尾）。</summary>
    public static string CStr(byte[]? raw)
    {
        if (raw == null || raw.Length == 0) return string.Empty;
        int n = Array.IndexOf(raw, (byte)0);
        if (n < 0) n = raw.Length;
        return Encoding.UTF8.GetString(raw, 0, n).Trim();
    }
}

// ——— 以下结构体布局对照 libraw_types.h（0.21.1）———

[StructLayout(LayoutKind.Sequential)]
internal struct LibRawIParams
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] guard;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] make;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] model;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] software;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] normalized_make;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] normalized_model;
    public uint maker_index;
    public uint raw_count;
    public uint dng_version;
    public uint is_foveon;
    public int colors;
    public uint filters;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] xtrans;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] xtrans_abs;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public byte[] cdesc;
    public uint xmplen;
    public IntPtr xmpdata;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibRawGpsInfo
{
    public float lat0, lat1, lat2, lon0, lon1, lon2, ts0, ts1, ts2, altitude;
    public byte altref, latref, longref, gpsstatus, gpsparsed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibRawImgOther
{
    public float iso_speed;
    public float shutter;
    public float aperture;
    public float focal_len;
    public long timestamp;
    public uint shot_order;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public uint[] gpsdata;
    public LibRawGpsInfo parsed_gps;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] desc;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] artist;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] analogbalance;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibRawLensInfo
{
    public float MinFocal, MaxFocal, MaxAp4MinFocal, MaxAp4MaxFocal, EXIF_MaxAp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] LensMake;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Lens;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] LensSerial;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] InternalLensSerial;
    public ushort FocalLengthIn35mmFormat;
}

[StructLayout(LayoutKind.Sequential)]
public struct LibRawProcessedImage
{
    public int type;   // 1 = LIBRAW_IMAGE_JPEG, 2 = LIBRAW_IMAGE_BITMAP
    public ushort height, width, colors, bits;
    public uint data_size;
    // 注意：C 结构体末尾是 unsigned char data[1]（内联柔性数组，不是指针），
    // 像素数据的起始地址 = 结构体指针 + 16 字节，见 LibRawImage.DataOffset。
}

public static class LibRawProcessedImageLayout
{
    /// <summary>libraw_processed_image_t.data 的字节偏移（sizeof 前 5 个字段 = 4+2*4+4 = 16）。</summary>
    public const int DataOffset = 16;
}

public enum LibRawImageKind { Jpeg = 1, Bitmap = 2 }
