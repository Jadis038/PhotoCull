// S2：全格式兼容性探测：能否 open / 有无内嵌预览 / 元数据是否齐全。
using System.Diagnostics;

namespace LibRawProbe;

public static class S2
{
    public static void Run(string dir)
    {
        Console.WriteLine($"LibRaw {LibRaw.Version()}  S2 formats in {dir}");
        Console.WriteLine("file | ext | model | open | thumb(tformat WxH) | meta(make/model/iso/f/shutter/mm/lens/time/size)");
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            IntPtr lr = LibRaw.libraw_init(0);
            var rc = LibRaw.libraw_open_wfile(lr, f);
            if (rc != 0)
            {
                Console.WriteLine($"{Path.GetFileName(f)} | {ext} | - | FAIL({LibRaw.Err(rc)}) | - | -");
                LibRaw.libraw_close(lr);
                continue;
            }
            var ip = LibRaw.GetIParams(lr);
            var ot = LibRaw.GetImgOther(lr);
            var lens = LibRaw.GetLensInfo(lr);
            var sw = Stopwatch.StartNew();
            var trc = LibRaw.libraw_unpack_thumb(lr);
            long thumbMs = sw.ElapsedMilliseconds;
            string thumb = "none";
            if (trc == 0)
            {
                var mem = LibRaw.libraw_dcraw_make_mem_thumb(lr, out int err);
                if (mem != IntPtr.Zero) { var im = LibRaw.GetImage(mem); thumb = $"type{im.type} {im.width}x{im.height} {thumbMs}ms {im.data_size / 1024}KB"; LibRaw.libraw_dcraw_clear_mem(mem); }
                else thumb = $"make_mem FAIL({LibRaw.Err(err)})";
            }
            else thumb = $"unpack_thumb FAIL({LibRaw.Err(trc)})";

            string meta = string.Join("/", new[] {
                LibRaw.Cstr(ip.make), LibRaw.Cstr(ip.model),
                ot.iso_speed.ToString("F0"), ot.aperture.ToString("F1"),
                ot.shutter.ToString("F4"), ot.focal_len.ToString("F0"),
                LibRaw.Cstr(lens.Lens), ot.timestamp.ToString(),
                $"{LibRaw.libraw_get_iwidth(lr)}x{LibRaw.libraw_get_iheight(lr)}"
            });
            Console.WriteLine($"{Path.GetFileName(f)} | {ext} | {LibRaw.Cstr(ip.model)} | ok | {thumb} | {meta}");
            LibRaw.libraw_close(lr);
        }
    }
}
