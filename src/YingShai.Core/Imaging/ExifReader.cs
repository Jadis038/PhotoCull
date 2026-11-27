using System.Windows.Media.Imaging;

namespace YingShai.Core.Imaging;

/// <summary>
/// 从 WIC 元数据里读 EXIF 字段。RAW 的「一次打开」策略：LibRaw 拿主体字段，
/// 这里只从同一份内嵌 JPEG 的 EXIF 里补镜头/色彩空间/方向等 LibRaw 不直接给的值。
/// </summary>
public static class ExifReader
{
    private static object? Query(BitmapMetadata? md, string query)
    {
        if (md == null) return null;
        try { return md.GetQuery(query); } catch { return null; }
    }

    private static string? Str(BitmapMetadata? md, string q)
    {
        var v = Query(md, q);
        if (v == null) return null;
        // WIC 对某些标签（如 Model/Artist）会返回 string[]（每个字符一项），拼回字符串
        if (v is string[] arr) return string.Concat(arr).Trim();
        if (v is string[] { Length: 0 }) return null;
        var s = v.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(s) || s == "0" ? null : s;
    }

    private static double? Num(BitmapMetadata? md, string q)
    {
        var v = Query(md, q);
        if (v == null) return null;
        try
        {
            // WIC 会返回 ulong 分数编码（分子/分母各 32 位）或 int
            return v switch
            {
                ulong u => (u >> 32) == 0 ? u : (double)(u & 0xFFFFFFFF) / (u >> 32),
                uint u32 => u32,
                int i => i,
                double d => d,
                float f => f,
                _ => double.TryParse(v.ToString(), out var p) ? p : null
            };
        }
        catch { return null; }
    }

    /// <summary>EXIF 方向（1..8），读不到按 1 处理。</summary>
    public static int ReadOrientation(BitmapMetadata? md)
    {
        var v = Num(md, "/app1/ifd/{ushort=274}") ?? Num(md, "/ifd/{ushort=274}");
        if (v is >= 1 and <= 8) return (int)v.Value;
        return 1;
    }

    /// <summary>读一个 EXIF 字段集合（RAW 内嵌 JPEG / 普通 JPG 都可用）。</summary>
    public static ExifFields Read(BitmapMetadata? md)
    {
        var f = new ExifFields
        {
            Make = Str(md, "/app1/ifd/{ushort=271}"),
            Model = Str(md, "/app1/ifd/{ushort=272}"),
            LensModel = Str(md, "/app1/ifd/exif/{ushort=42036}"),
            LensMake = Str(md, "/app1/ifd/exif/{ushort=42035}"),
            DateTimeOriginal = Str(md, "/app1/ifd/exif/{ushort=36867}") ?? Str(md, "/app1/ifd/{ushort=306}"),
            Orientation = ReadOrientation(md),
            ExposureTime = Num(md, "/app1/ifd/exif/{ushort=33434}"),
            FNumber = Num(md, "/app1/ifd/exif/{ushort=33437}"),
            Iso = Num(md, "/app1/ifd/exif/{ushort=34855}"),
            FocalLength = Num(md, "/app1/ifd/exif/{ushort=37386}"),
            FocalLength35 = Num(md, "/app1/ifd/exif/{ushort=41989}"),
            // 色彩空间：正常在 Exif IFD；个别写入方放在 IFD0，兜底查询
            ColorSpace = Num(md, "/app1/ifd/exif/{ushort=40961}") ?? Num(md, "/app1/ifd/{ushort=40961}"),
            PixelX = Num(md, "/app1/ifd/exif/{ushort=40962}"),
            PixelY = Num(md, "/app1/ifd/exif/{ushort=40963}"),
        };
        return f;
    }

    /// <summary>EXIF 色彩空间数值转显示名。</summary>
    public static string? ColorSpaceName(double? code) => code switch
    {
        1 => "sRGB",
        2 => "Adobe RGB",
        0xFFFF => "未校准",
        null => null,
        _ => $"色彩空间 {code:0}"
    };

    /// <summary>解析 EXIF 日期字符串（"2024:06:01 12:34:56"）。</summary>
    public static DateTime? ParseExifDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(':', '-').Replace(' ', 'T');
        // yyyy:MM:dd HH:mm:ss -> yyyy-MM-ddTHH:mm:ss
        if (s.Length >= 19 && DateTime.TryParse(s.Substring(0, 19), out var dt)) return dt;
        return null;
    }
}

public sealed class ExifFields
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? LensModel { get; init; }
    public string? LensMake { get; init; }
    public string? DateTimeOriginal { get; init; }
    public int Orientation { get; init; } = 1;
    public double? ExposureTime { get; init; }
    public double? FNumber { get; init; }
    public double? Iso { get; init; }
    public double? FocalLength { get; init; }
    public double? FocalLength35 { get; init; }
    public double? ColorSpace { get; init; }
    public double? PixelX { get; init; }
    public double? PixelY { get; init; }
}
