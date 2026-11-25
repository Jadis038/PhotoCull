// S1：单张 RAW 的 open / 内嵌 JPEG 抽取 / 完整 demosaic 耗时对比。
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LibRawProbe;

public static class S1
{
    public static void Run(string file, int repeat = 5)
    {
        Console.WriteLine($"LibRaw {LibRaw.Version()}  S1 on {Path.GetFileName(file)}  ({new FileInfo(file).Length / 1048576.0:F1} MB, x{repeat})");
        Console.WriteLine("run | open(ms) | thumb(ms) | thumbW x thumbH | full(ms) | fullW x fullH | model");
        double oSum = 0, tSum = 0, fSum = 0;
        for (int i = 0; i < repeat; i++)
        {
            var sw = Stopwatch.StartNew();
            IntPtr lr = LibRaw.libraw_init(0);
            int rc = LibRaw.libraw_open_wfile(lr, file);
            sw.Stop(); double openMs = sw.Elapsed.TotalMilliseconds;
            if (rc != 0) { Console.WriteLine($"open failed: {LibRaw.Err(rc)}"); LibRaw.libraw_close(lr); return; }
            var ip = LibRaw.GetIParams(lr);
            int iw = LibRaw.libraw_get_iwidth(lr), ih = LibRaw.libraw_get_iheight(lr);

            sw.Restart();
            rc = LibRaw.libraw_unpack_thumb(lr);
            int tw = 0, th = 0;
            if (rc == 0)
            {
                IntPtr mem = LibRaw.libraw_dcraw_make_mem_thumb(lr, out int err);
                if (mem != IntPtr.Zero)
                {
                    var img = LibRaw.GetImage(mem);
                    tw = img.width; th = img.height;
                    LibRaw.libraw_dcraw_clear_mem(mem);
                }
                else rc = err;
            }
            sw.Stop(); double thumbMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            int rc2 = LibRaw.libraw_unpack(lr);
            int fw = 0, fh = 0;
            if (rc2 == 0 && LibRaw.libraw_dcraw_process(lr) == 0)
            {
                IntPtr mem2 = LibRaw.libraw_dcraw_make_mem_image(lr, out int e2);
                if (mem2 != IntPtr.Zero) { var im2 = LibRaw.GetImage(mem2); fw = im2.width; fh = im2.height; LibRaw.libraw_dcraw_clear_mem(mem2); }
                else rc2 = e2;
            }
            sw.Stop(); double fullMs = sw.Elapsed.TotalMilliseconds;
            LibRaw.libraw_close(lr);

            oSum += openMs; tSum += thumbMs; fSum += fullMs;
            Console.WriteLine($"{i + 1,3} | {openMs,8:F1} | {thumbMs,8:F1} | {tw,5} x {th,-5} | {fullMs,8:F1} | {fw,5} x {fh,-5} | {LibRaw.Cstr(ip.model)}  [{iw}x{ih}]");
        }
        Console.WriteLine($"avg | {oSum / repeat,8:F1} | {tSum / repeat,8:F1} |                 | {fSum / repeat,8:F1} |");
        Console.WriteLine($"目标（open+thumb < 40ms/张）=> {((tSum / repeat) < 40 ? "PASS" : "FAIL")}   (open+thumb = {(oSum + tSum) / repeat:F1}ms)");
    }
}
