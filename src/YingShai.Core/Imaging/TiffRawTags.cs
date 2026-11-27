using System.Buffers.Binary;

namespace YingShai.Core.Imaging;

/// <summary>
/// 轻量 TIFF/DNG 标签读取（只读 IFD0 与 ExifIFD，不碰像素数据）。
/// 用于补齐 LibRaw 不直接提供的字段，例如色彩空间（ColorSpace 40961）
/// 与 DNG 的 AsShotProfileName（50728）。任何解析失败都静默返回空。
/// </summary>
public static class TiffRawTags
{
    public static (int? ColorSpace, string? ProfileName) ReadColorSpaceTags(string path)
    {
        try
        {
            using var fs = new FileStream(AppPaths.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
            using var br = new BinaryReader(fs);

            ushort bom = ReadU16(br, le: true);
            bool le = bom == 0x4949;                      // "II" 小端 / "MM" 大端
            if (!le && bom != 0x4D4D) return (null, null);
            if (ReadU16(br, le) != 42) return (null, null);
            long ifd0Offset = ReadU32(br, le);

            var ifd0Tags = ReadIfd(br, le, ifd0Offset);

            int? cs = null;
            if (ifd0Tags.TryGetValue(40961, out var v) && v.Type == 3 && v.Count >= 1)
                cs = (int)(le ? (v.Value & 0xFFFF) : (v.Value >> 16));

            string? profile = null;
            if (ifd0Tags.TryGetValue(50728, out var p) && p.Type == 2 && p.Count > 0)
                profile = ReadAscii(br, le, p.Value, (int)p.Count);

            if (ifd0Tags.TryGetValue(34665, out var exifOff) && exifOff.Type == 4 && exifOff.Count >= 1)
            {
                var exif = ReadIfd(br, le, exifOff.Value);
                if (cs == null && exif.TryGetValue(40961, out var e) && e.Type == 3 && e.Count >= 1)
                    cs = (int)(le ? (e.Value & 0xFFFF) : (e.Value >> 16));
            }

            return (cs, string.IsNullOrWhiteSpace(profile) ? null : profile.Trim());
        }
        catch
        {
            return (null, null);
        }
    }

    private readonly record struct Entry(ushort Type, uint Count, uint Value);

    private static Dictionary<int, Entry> ReadIfd(BinaryReader br, bool le, long offset)
    {
        br.BaseStream.Seek(offset, SeekOrigin.Begin);
        ushort n = ReadU16(br, le);
        var d = new Dictionary<int, Entry>();
        for (int i = 0; i < n; i++)
        {
            ushort tag = ReadU16(br, le);
            ushort type = ReadU16(br, le);
            uint count = ReadU32(br, le);
            uint value = ReadU32(br, le);
            d[tag] = new Entry(type, count, value);
        }
        return d;
    }

    private static string ReadAscii(BinaryReader br, bool le, uint offset, int count)
    {
        if (count <= 0 || count > 4096) return "";
        br.BaseStream.Seek(offset, SeekOrigin.Begin);
        var bytes = br.ReadBytes(count);
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static ushort ReadU16(BinaryReader br, bool le)
    {
        var b = br.ReadBytes(2);
        return le ? (ushort)(b[0] | (b[1] << 8)) : (ushort)((b[0] << 8) | b[1]);
    }

    private static uint ReadU32(BinaryReader br, bool le)
    {
        var b = br.ReadBytes(4);
        return le
            ? (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24))
            : (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }
}
