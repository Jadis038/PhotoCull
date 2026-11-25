// LibRaw 0.21.1 C API 最小 P/Invoke 封装（spike 用）。
// 结构体布局对照 libraw_types.h：libraw_iparams_t / libraw_imgother_t / libraw_lensinfo_t / libraw_processed_image_t。
using System.Runtime.InteropServices;
using System.Text;

namespace LibRawProbe;

[StructLayout(LayoutKind.Sequential)]
public struct LibRawIParams
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
public struct LibRawGpsInfo
{
    public float lat0, lat1, lat2, lon0, lon1, lon2, ts0, ts1, ts2, altitude;
    public byte altref, latref, longref, gpsstatus, gpsparsed;
}

[StructLayout(LayoutKind.Sequential)]
public struct LibRawImgOther
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
public struct LibRawLensInfo
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
    public int type;      // LibRaw_image_formats: 1=JPEG, 2=bitmap RGB
    public ushort height, width, colors, bits;
    public uint data_size;
    public IntPtr data;
}

public static class LibRaw
{
    private const string Dll = "raw_r.dll";
    private const CallingConvention CC = CallingConvention.Cdecl;

    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr libraw_init(uint flags);
    [DllImport(Dll, CallingConvention = CC, CharSet = CharSet.Unicode)] public static extern int libraw_open_wfile(IntPtr lr, string path);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_open_file(IntPtr lr, string path);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_unpack(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_unpack_thumb(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_unpack_thumb_ex(IntPtr lr, int idx);
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr lr, out int err);
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr libraw_dcraw_make_mem_image(IntPtr lr, out int err);
    [DllImport(Dll, CallingConvention = CC)] public static extern void libraw_dcraw_clear_mem(IntPtr img);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_dcraw_process(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern void libraw_close(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern void libraw_recycle(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_strerror(int code);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_version();
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_get_iparams(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_get_imgother(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] private static extern IntPtr libraw_get_lensinfo(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_get_iwidth(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern int libraw_get_iheight(IntPtr lr);
    [DllImport(Dll, CallingConvention = CC)] public static extern void libraw_set_demosaic(IntPtr lr, int value);
    [DllImport(Dll, CallingConvention = CC)] public static extern void libraw_set_output_bps(IntPtr lr, int value);
    [DllImport(Dll, CallingConvention = CC)] public static extern void libraw_set_no_auto_bright(IntPtr lr, int value);

    public static string Version() => Ptr(libraw_version());
    public static string Err(int code) => code == 0 ? "OK" : Ptr(libraw_strerror(code));
    public static LibRawIParams GetIParams(IntPtr lr) => Marshal.PtrToStructure<LibRawIParams>(libraw_get_iparams(lr));
    public static LibRawImgOther GetImgOther(IntPtr lr) => Marshal.PtrToStructure<LibRawImgOther>(libraw_get_imgother(lr));
    public static LibRawLensInfo GetLensInfo(IntPtr lr) => Marshal.PtrToStructure<LibRawLensInfo>(libraw_get_lensinfo(lr));
    public static LibRawProcessedImage GetImage(IntPtr p) => Marshal.PtrToStructure<LibRawProcessedImage>(p);

    public static string Cstr(byte[] raw)
    {
        if (raw == null) return "";
        int n = Array.IndexOf(raw, (byte)0);
        if (n < 0) n = raw.Length;
        return Encoding.UTF8.GetString(raw, 0, n).Trim();
    }

    private static string Ptr(IntPtr p) => p == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(p) ?? "");
}
