using System.Diagnostics;
using System.IO;
using System.Text;
using YingShai.Core.Imaging;
using YingShai.Core.Index;
using YingShai.Core.Jobs;
using YingShai.Core.Model;
using YingShai.Core.Session;
using YingShai.Core.Settings;

namespace YingShai.Core.Cli;

/// <summary>
/// 命令行基准测试（影·筛.exe --bench &lt;文件夹&gt;）：输出各阶段耗时表 + CSV，可重复运行。
/// 覆盖：枚举、首屏 20 张、单张切换（缓存命中/首次）、100% 全解、全量缩略图预热、缓存命中率、内存峰值。
/// </summary>
public sealed class Bench
{
    private readonly List<string[]> _rows = new();
    private readonly List<string> _lines = new();

    public int Run(string folder, string? csvPath = null, bool cold = false, bool warmAll = true)
    {
        var total = Stopwatch.StartNew();
        // 用真实索引库：磁盘缩略图缓存的索引必须持久，否则跨进程复用不到缓存（也是真实行为）
        string dbPath = AppPaths.IndexDbPath;
        var store = new StateStore(dbPath);
        var settings = new AppSettings { MemoryCacheMb = 512, DecodeConcurrency = 4 };
        var session = new ViewerSession(settings, store)
        {
            UiContext = null
        };

        if (cold) { session.Cache.ClearAll(); Console.WriteLine("已清空磁盘缓存（冷启动测量）"); }

        var proc = Process.GetCurrentProcess();
        try
        {
            // ——— 1. 枚举 ———
            var sw = Stopwatch.StartNew();
            bool ok = session.OpenFolderAsync(folder).GetAwaiter().GetResult();
            sw.Stop();
            if (!ok) { Console.Error.WriteLine($"无法打开文件夹：{folder}"); return 2; }

            Add("enumerate", "打开文件夹→文件名列表可见", session.LastScanMs, "ms", $"{session.TotalCount} 个文件");
            _lines.Add($"枚举：{session.LastScanMs:F0} ms（{session.TotalCount} 个文件）");

            if (session.TotalCount == 0) { Console.Error.WriteLine("文件夹里没有图片"); return 2; }

            // ——— 2. 首屏 20 张 ———
            var first = session.Items.Take(20).ToList();
            sw.Restart();
            Task.WhenAll(first.Select(i => session.GetThumbAsync(i, DecodePriority.VisibleStrip))).GetAwaiter().GetResult();
            sw.Stop();
            Add("first_screen", "首屏 20 张缩略图", sw.Elapsed.TotalMilliseconds, "ms", $"{first.Count} 张");
            _lines.Add($"首屏 {first.Count} 张缩略图：{sw.Elapsed.TotalMilliseconds:F0} ms");

            // ——— 3. 单击切图：首次 vs 缓存命中 ———
            var uncached = session.Items.Skip(20).Take(10).ToList();
            var missTimes = new List<double>();
            foreach (var item in uncached)
            {
                var t = Stopwatch.StartNew();
                session.GetPreviewAsync(item, CancellationToken.None).GetAwaiter().GetResult();
                t.Stop();
                missTimes.Add(t.Elapsed.TotalMilliseconds);
            }
            var hitTimes = new List<double>();
            foreach (var item in uncached)
            {
                var t = Stopwatch.StartNew();
                session.GetPreviewAsync(item, CancellationToken.None).GetAwaiter().GetResult();
                t.Stop();
                hitTimes.Add(t.Elapsed.TotalMilliseconds);
            }
            Add("switch_first", "单击切图（首次）P50", Pct(missTimes, 50), "ms", $"样本 {missTimes.Count}");
            Add("switch_first", "单击切图（首次）P95", Pct(missTimes, 95), "ms", "");
            Add("switch_cached", "单击切图（缓存命中）P50", Pct(hitTimes, 50), "ms", $"样本 {hitTimes.Count}");
            Add("switch_cached", "单击切图（缓存命中）P95", Pct(hitTimes, 95), "ms", "");
            _lines.Add($"单击切图：首次 P50 {Pct(missTimes, 50):F0} ms / P95 {Pct(missTimes, 95):F0} ms，" +
                       $"缓存命中 P50 {Pct(hitTimes, 50):F1} ms");

            // ——— 4. 100% 全解 ———
            var rawItem = session.Items.FirstOrDefault(i => LibRawImage.IsRawExtension(i.Extension)) ?? session.Items[0];
            var fullSw = Stopwatch.StartNew();
            var full = session.GetFullResAsync(rawItem, CancellationToken.None).GetAwaiter().GetResult();
            fullSw.Stop();
            Add("full_decode", $"100% 全解（{rawItem.Name}）", fullSw.Elapsed.TotalMilliseconds, "ms",
                full == null ? "失败" : $"{full.PixelWidth}×{full.PixelHeight}");
            _lines.Add($"100% 全解：{fullSw.Elapsed.TotalMilliseconds:F0} ms（{(full == null ? "失败" : $"{full.PixelWidth}×{full.PixelHeight}")}）");

            // ——— 5. 全量缩略图预热（分批，模拟真实使用里"滚到哪生成到哪"） ———
            if (warmAll)
            {
                sw.Restart();
                const int chunk = 200;
                var items = session.Items.ToList();
                double wsPeak = 0;
                for (int i = 0; i < items.Count; i += chunk)
                {
                    var batch = items.Skip(i).Take(chunk).Select(it => session.GetThumbAsync(it, DecodePriority.Warmup)).ToArray();
                    Task.WhenAll(batch).GetAwaiter().GetResult();
                    proc.Refresh();
                    wsPeak = Math.Max(wsPeak, proc.WorkingSet64 / 1048576.0);
                }
                sw.Stop();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                proc.Refresh();
                double wsAfterGc = proc.WorkingSet64 / 1048576.0;
                double secPerItem = sw.Elapsed.TotalSeconds / Math.Max(1, session.TotalCount);
                Add("warm_all", "全量缩略图预热总耗时", sw.Elapsed.TotalSeconds, "s", $"{session.TotalCount} 张（{chunk} 张一批）");
                Add("warm_all", "折算 2000 张耗时", secPerItem * 2000, "s", $"按 {session.TotalCount} 张外推");
                Add("warm_all", "预热期间工作集峰值", wsPeak, "MB", $"GC 后 {wsAfterGc:F0} MB");
                _lines.Add($"全量预热：{session.TotalCount} 张 {sw.Elapsed.TotalSeconds:F1} s" +
                           $"（折算 2000 张 {secPerItem * 2000:F0} s），工作集峰值 {wsPeak:F0} MB，GC 后 {wsAfterGc:F0} MB");
            }

            // ——— 6. 缓存命中率 / 解码统计 ———
            var decodeTimes = session.Scheduler.RecentDecodeMs;
            long hits = session.Scheduler.CacheHitCount;
            long decodes = session.Scheduler.DecodeCount;
            double hitRate = (hits + decodes) == 0 ? 0 : 100.0 * hits / (hits + decodes);
            Add("cache", "缓存命中率", hitRate, "%", $"命中 {hits}，解码 {decodes}");
            Add("decode", "单张解码 P50", Pct(decodeTimes.ToList(), 50), "ms", $"样本 {decodeTimes.Length}");
            Add("decode", "单张解码 P95", Pct(decodeTimes.ToList(), 95), "ms", "");
            _lines.Add($"缓存命中率：{hitRate:F1}%（命中 {hits} / 解码 {decodes}），单张解码 P50 {Pct(decodeTimes.ToList(), 50):F0} ms、P95 {Pct(decodeTimes.ToList(), 95):F0} ms");

            // ——— 7. 内存 ———
            proc.Refresh();
            double peakMb = proc.PeakWorkingSet64 / 1048576.0;
            double nowMb = proc.WorkingSet64 / 1048576.0;
            Add("memory", "进程内存峰值", peakMb, "MB", $"当前 {nowMb:F0} MB");
            _lines.Add($"内存：峰值 {peakMb:F0} MB（当前 {nowMb:F0} MB），内存缓存 {session.Cache.MemoryBytes / 1048576.0:F0} MB / {session.Cache.MemoryCount} 张");

            // ——— 8. 输出 ———
            Console.WriteLine();
            Console.WriteLine("影·筛 基准测试（--bench）");
            Console.WriteLine($"文件夹：{folder}");
            Console.WriteLine($"文件数：{session.TotalCount}    总耗时：{total.Elapsed.TotalSeconds:F1} s");
            Console.WriteLine(new string('-', 78));
            foreach (var line in _lines) Console.WriteLine("  " + line);
            Console.WriteLine(new string('-', 78));
            Console.WriteLine("阶段".PadRight(16) + "指标".PadRight(28) + "数值".PadLeft(12) + "  单位/说明");
            foreach (var r in _rows)
                Console.WriteLine(r[0].PadRight(16) + r[1].PadRight(28) + r[2].PadLeft(12) + "  " + r[4] + (r[3] == "ms" || r[3] == "s" ? " " + r[3] : ""));

            csvPath ??= Path.Combine(Path.GetTempPath(), $"影筛bench_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            WriteCsv(csvPath, folder);
            Console.WriteLine();
            Console.WriteLine($"CSV 已写入：{csvPath}");
            return 0;
        }
        finally
        {
            session.Dispose();
            store.Dispose();
            // 索引库是持久的（磁盘缩略图缓存索引在里面），不删
        }
    }

    private void Add(string stage, string metric, double value, string unit, string note)
        => _rows.Add(new[] { stage, metric, value.ToString("F1"), unit, note });

    private static double Pct(List<double> values, int pct)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        int idx = (int)Math.Ceiling(pct / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private void WriteCsv(string path, string folder)
    {
        var sb = new StringBuilder();
        sb.AppendLine("stage,metric,value,unit,note");
        foreach (var r in _rows)
        {
            sb.Append(Escape(r[0])).Append(',').Append(Escape(r[1])).Append(',')
              .Append(r[2]).Append(',').Append(Escape(r[3])).Append(',').Append(Escape(r[4])).AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
