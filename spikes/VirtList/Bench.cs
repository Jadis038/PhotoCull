using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VirtList;

/// <summary>
/// 测量主体。一个进程只跑一个场景（工作集峰值才干净可比），由 run.ps1 依次启动。
/// <para>
/// 测量方法（真窗口 / 离屏同一套）：
/// 1. 逐帧用 ScrollToHorizontalOffset 推进水平偏移，再同步 UpdateLayout()
///    —— 这正是 WPF 滚动时做的事：改偏移 → 测量/排列 → 虚拟化面板增删容器 → 失效重绘；
/// 2. 每帧用 Stopwatch 分两段计时：
///    a) 布局段：改偏移 + UpdateLayout（测量/排列/容器生成）；
///    b) 队列段：把 Dispatcher 里 &gt;=Background 的条目处理掉
///       （ItemContainerGenerator 的容器回收动作排在 Background，渲染失效排在 Render，
///        真实帧循环里每帧都会被处理）。两者相加 = 一帧内 UI 线程的总工作耗时。
/// 3. 另用 CompositionTarget.Rendering 跑一小段真实合成帧回调，记录真实帧间隔，并记录
///    本机空闲窗口的帧间隔作为环境基线（本机无桌面会话，帧间隔受合成器节流，不能当掉帧判据）；
/// 4. 每帧采样进程 WorkingSet64，以及 ItemsHost 的直接子元素数（= 当前实例化的容器数）。
/// </para>
/// </summary>
public sealed class Bench
{
    private readonly Options _opt;
    private readonly List<ThumbItem> _items;
    private readonly Process _process = Process.GetCurrentProcess();
    private long _wsStartBytes;
    private long _wsPeakBytes;

    public Bench(Options opt)
    {
        _opt = opt;
        _items = ItemFactory.Create(opt.Items);
    }

    public void Run()
    {
        Console.WriteLine("═══ 影·筛 S4 spike · 横向虚拟化缩略图列表 ═══");
        Console.WriteLine($"场景      : {_opt.ScenarioLabel}");
        Console.WriteLine($"数据项    : {_opt.Items} 项   单元尺寸: {ThumbCell.CellWidth}×{ThumbCell.CellHeight} dip（图 {ThumbCell.ImageSize}×{ThumbCell.ImageSize} + 文字区）");
        Console.WriteLine($"运行环境  : {RuntimeInformation.OSDescription} / .NET {Environment.Version} / x64={Environment.Is64BitProcess} / CPU 核数 {Environment.ProcessorCount} / 服务端 GC={GCSettings.IsServerGC}");
        Console.WriteLine($"WPF 渲染  : RenderCapability.Tier={RenderCapability.Tier >> 16}");
        Console.WriteLine();

        // ── 1. 搭树 + 首次布局（包含模板解析、容器首次生成）──
        var buildSw = Stopwatch.StartNew();
        using var host = ListHost.Create(_opt, _items);
        buildSw.Stop();
        var firstLayoutMs = buildSw.Elapsed.TotalMilliseconds;

        if (host.FallbackNote is not null)
        {
            Console.WriteLine($"注意: {host.FallbackNote}");
        }

        _wsStartBytes = _process.WorkingSet64;
        _wsPeakBytes = _wsStartBytes;

        var dpi = VisualTreeHelper.GetDpi(host.Root);
        Console.WriteLine($"宿主管线  : {host.HostMode}，窗口 {_opt.Width:F0}×{_opt.Height:F0}，ScrollViewer 视口 {host.Scroller.ViewportWidth:F0}×{host.Scroller.ViewportHeight:F0} dip，DPI 缩放 {dpi.DpiScaleX:F2}");
        Console.WriteLine($"滚动范围  : ExtentWidth={host.Scroller.ExtentWidth:F0} dip，ScrollableWidth={host.Scroller.ScrollableWidth:F0} dip");
        Console.WriteLine($"建树+首次布局: {firstLayoutMs:F1} ms（含 {host.RealizedContainerCount} 个容器）");
        if (host.RealizedContainerCount == 0)
        {
            throw new InvalidOperationException("首次布局后没有任何容器，ItemsHost 未生成子元素");
        }

        var maxOffset = host.Scroller.ScrollableWidth;
        if (maxOffset <= 0)
        {
            throw new InvalidOperationException($"ScrollableWidth={maxOffset}，无法滚动（内容没有超出视口？）");
        }

        var step = _opt.EffectiveStep;

        // ── 2. 预热：让 JIT、模板、字体缓存稳定下来，不计入统计 ──
        for (var i = 0; i < 20; i++)
        {
            Step(host, Math.Min(i * step, maxOffset));
        }

        Step(host, 0);

        // ── 3. 队列排空的空转基准：排除测量代码自身的开销 ──
        var pumpBaselineSamples = new List<double>(64);
        for (var i = 0; i < 64; i++)
        {
            var t0 = Clock.Now;
            PumpDispatcher();
            pumpBaselineSamples.Add(Clock.Ms(t0, Clock.Now));
        }

        var pumpBaseline = Stats.From(pumpBaselineSamples);

        // ── 4. 虚拟化抽查：不同偏移下的已实现容器数 ──
        Console.WriteLine();
        Console.WriteLine("── 虚拟化抽查：不同滚动偏移下，实际实例化的容器数量 ──");
        foreach (var fraction in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var offset = maxOffset * fraction;
            Step(host, offset);
            var count = host.RealizedContainerCount;
            var (first, last) = host.RealizedItemRange();
            Console.WriteLine($"  偏移 {offset,9:F0} dip ({fraction * 100,3:F0}%) → 容器 {count,5} 个，可见项序号 {first,5}..{last,5}");
        }

        Step(host, 0);

        // ── 5. 主测量：来回滚动 ──
        Console.WriteLine();
        Console.WriteLine($"── 滚动测量：每帧 {step:F0} dip，来回 {_opt.Trips} 趟（{_opt.Trips * 2} 次平移），每次平移约 {maxOffset / step:F0} 帧 ──");

        var frameMs = new List<double>(8192);
        var layoutMs = new List<double>(8192);
        var pumpMs = new List<double>(8192);
        var containerCounts = new List<int>(8192);
        var frameCount = 0;
        var totalDistance = 0.0;
        var passes = _opt.Trips * 2;

        for (var pass = 0; pass < passes; pass++)
        {
            var rightward = pass % 2 == 0;
            var pos = rightward ? 0.0 : maxOffset;
            Step(host, pos);

            while (true)
            {
                pos += rightward ? step : -step;
                if (rightward && pos > maxOffset)
                {
                    pos = maxOffset;
                }

                if (!rightward && pos < 0)
                {
                    pos = 0;
                }

                var (layout, pump) = Step(host, pos);
                layoutMs.Add(layout);
                pumpMs.Add(pump);
                frameMs.Add(layout + pump);
                containerCounts.Add(host.RealizedContainerCount);
                frameCount++;
                totalDistance += step;

                if (frameCount % 8 == 0)
                {
                    SampleMemory();
                }

                if ((rightward && pos >= maxOffset) || (!rightward && pos <= 0))
                {
                    break;
                }
            }
        }

        SampleMemory();

        // ── 6. 真实帧回调（只有真窗口才有 CompositionTarget.Rendering）──
        List<double>? idleIntervals = null;
        List<double>? scrollIntervals = null;
        List<double>? scrollWork = null;
        if (host.HasRealFrames)
        {
            var (idle, _) = PumpFrames(30, null);
            idleIntervals = idle.Count > 1 ? idle.GetRange(1, idle.Count - 1) : idle;

            var realPos = 0.0;
            var (intervals, work) = PumpFrames(_opt.RealFrames, () =>
            {
                realPos += step;
                if (realPos > maxOffset)
                {
                    realPos = 0;
                }

                host.ApplyScrollAndLayout(realPos);
            });
            scrollIntervals = intervals.Count > 1 ? intervals.GetRange(1, intervals.Count - 1) : intervals;
            scrollWork = work.Count > 1 ? work.GetRange(1, work.Count - 1) : work;
        }

        // ── 7. 单帧视口光栅化耗时（RenderTargetBitmap 软件光栅化，参考值）──
        host.ApplyScrollAndLayout(maxOffset * 0.5);
        var rasterMs = MeasureRasterization(host);

        // ── 8. 统计 ──
        var frameStats = Stats.From(frameMs);
        var layoutStats = Stats.From(layoutMs);
        var pumpStats = Stats.From(pumpMs);
        var (over1, over2, total) = DropCounter.Count(frameMs);
        var containersMin = containerCounts.Min();
        var containersMax = containerCounts.Max();
        var containersAvg = containerCounts.Average();
        var created = ThumbCell.CreatedCount;
        var rendered = ThumbCell.RenderedCount;

        // ── 9. 输出帧图 PNG（在帧上叠一行指标标注，方便文档直接引用）──
        var pngPath = _opt.PngPath ?? Path.Combine(AppContext.BaseDirectory, "frame-" + _opt.ScenarioKey + ".png");
        var label = $"影·筛 S4 spike | {_opt.ScenarioKey} | {_opt.Items} 项 | 容器 {containersMin}~{containersMax} 个 | 每帧 {frameStats.Avg:F2} ms | {host.HostMode}";
        SavePng(host, maxOffset * 0.5, pngPath, label);

        // ── 10. 汇总 ──
        _process.Refresh();
        var wsPeakProc = _process.PeakWorkingSet64;

        var managedBeforeGc = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedAfterGc = GC.GetTotalMemory(true);

        Console.WriteLine();
        Console.WriteLine($"── 主测量结果（{total} 帧，总位移 {totalDistance / 1000.0:F0} 千 dip）──");
        Console.WriteLine($"每帧 UI 工作耗时合计  : 平均 {frameStats.Avg:F2} ms | P50 {frameStats.P50:F2} | P95 {frameStats.P95:F2} | P99 {frameStats.P99:F2} | 最大 {frameStats.Max:F2} | 最小 {frameStats.Min:F3}");
        Console.WriteLine($"  其中 布局段(改偏移+UpdateLayout) : 平均 {layoutStats.Avg:F2} ms | P95 {layoutStats.P95:F2} | 最大 {layoutStats.Max:F2}");
        Console.WriteLine($"  其中 队列段(容器回收/渲染失效)   : 平均 {pumpStats.Avg:F2} ms | P95 {pumpStats.P95:F2} | 最大 {pumpStats.Max:F2}（测量代码空转基准 {pumpBaseline.Avg:F3} ms，已包含在队列段里）");
        Console.WriteLine($"掉帧（单帧预算 16.67 ms @60Hz）   : {over1} / {total} = {DropCounter.Percent(over1, total)}   （>33.3ms: {over2} 帧 = {DropCounter.Percent(over2, total)}）");
        Console.WriteLine($"已实例化容器数（每帧采样 ItemsHost.Children.Count）: 最小 {containersMin} / 平均 {containersAvg:F1} / 最大 {containersMax}");
        Console.WriteLine($"ThumbCell 实例化总数（全生命周期）: {created} 个（数据项 {_opt.Items} 个，OnRender 累计 {rendered} 次）");
        Console.WriteLine($"进程工作集: 起始 {Mb(_wsStartBytes):F1} MB | 采样峰值 {Mb(_wsPeakBytes):F1} MB | 进程峰值 {Mb(wsPeakProc):F1} MB");
        Console.WriteLine($"托管堆: 场景结束 {Mb(managedBeforeGc):F1} MB → GC 后 {Mb(managedAfterGc):F1} MB；GC 次数 Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}");
        Console.WriteLine($"单帧视口光栅化（RTB 软件路径，20 次中位）: {rasterMs:F2} ms（参考值；真窗口最终呈现走 GPU 合成）");

        if (scrollIntervals is not null && idleIntervals is not null && scrollIntervals.Count > 0 && idleIntervals.Count > 0)
        {
            var s = Stats.From(scrollIntervals);
            var i = Stats.From(idleIntervals);
            var w = scrollWork is { Count: > 0 } ? Stats.From(scrollWork) : null;
            Console.WriteLine($"真实帧回调（CompositionTarget.Rendering）: 空闲窗口 {i.Avg:F1} ms/帧（P95 {i.P95:F1}，最大 {i.Max:F1}）；持续滚动 {s.Avg:F2} ms/帧（P95 {s.P95:F2}，最大 {s.Max:F2}），回调内布局平均 {(w is null ? 0 : w.Avg):F2} ms");
            Console.WriteLine($"  说明: 本机无桌面会话、没有 vsync/真实显示同步——窗口没有脏区时约 {i.Avg:F0} ms 才合成一次（按需节流），");
            Console.WriteLine($"        持续滚动时反而 {s.Avg:F2} ms 就能走完一次合成。所以帧间隔既不代表 60Hz，也不代表性能上限，");
            Console.WriteLine("        掉帧率一律按每帧 UI 线程工作耗时（布局段 + 队列段）判定。");
        }
        else
        {
            Console.WriteLine("真实帧回调: 离屏模式没有 CompositionTarget.Rendering，未采样帧间隔（布局/容器生成走的是同一套管线）。");
        }

        Console.WriteLine($"帧图      : {pngPath}");

        // ── 10. 结论提示 ──
        Console.WriteLine();
        if (_opt.Scenario == ScenarioKind.NonVirtualized)
        {
            Console.WriteLine($"结论提示: 常驻容器 {containersMax} 个 = 全部 {_opt.Items} 项，虚拟化关闭 → 所有单元都实例化并常驻可视化树。");
        }
        else if (_opt.Scenario == ScenarioKind.VirtualizedStandard)
        {
            Console.WriteLine($"结论提示: 常驻容器 {containersMin}~{containersMax} 个（虚拟化生效），但全生命周期 new 了 {created} 个 ThumbCell（≈ 数据项数）");
            Console.WriteLine("          → Standard 模式容器用完即弃，滚动期间持续创建/销毁；换 Recycling 才能复用。");
        }
        else
        {
            Console.WriteLine($"结论提示: 常驻容器 {containersMin}~{containersMax} 个（{_opt.Items} 项中），滚动只影响窗口内 ~{Math.Ceiling(host.Scroller.ViewportWidth / ThumbCell.CellWidth):F0} 个单元；");
            Console.WriteLine($"          全生命周期只 new 了 {created} 个 ThumbCell → 虚拟化 + 容器回收生效。");
        }

        Console.WriteLine();
        Console.WriteLine(
            "RESULT|" + string.Join("|",
                _opt.ScenarioKey,
                host.HostMode,
                _opt.Items,
                total,
                frameStats.Avg.ToString("F3"),
                frameStats.P50.ToString("F3"),
                frameStats.P95.ToString("F3"),
                frameStats.P99.ToString("F3"),
                frameStats.Max.ToString("F3"),
                over1,
                (over1 * 100.0 / total).ToString("F1"),
                over2,
                Mb(_wsStartBytes).ToString("F1"),
                Mb(_wsPeakBytes).ToString("F1"),
                Mb(wsPeakProc).ToString("F1"),
                containersMin,
                containersAvg.ToString("F1"),
                containersMax,
                created,
                firstLayoutMs.ToString("F1"),
                rasterMs.ToString("F2"),
                scrollIntervals is { Count: > 0 } ? Stats.From(scrollIntervals).Avg.ToString("F2") : "-",
                idleIntervals is { Count: > 0 } ? Stats.From(idleIntervals).Avg.ToString("F1") : "-",
                pngPath,
                layoutStats.Avg.ToString("F3"),
                pumpStats.Avg.ToString("F3")));
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    private void SampleMemory()
    {
        _process.Refresh();
        var ws = _process.WorkingSet64;
        if (ws > _wsPeakBytes)
        {
            _wsPeakBytes = ws;
        }
    }

    /// <summary>
    /// 驱动一帧：改偏移 → 同步布局（测量/排列）→ 排空 Dispatcher 队列（容器回收/生成）→ 计时。
    /// 返回（布局段耗时, 队列段耗时），单位 ms。
    /// </summary>
    private static (double Layout, double Pump) Step(ListHost host, double offset)
    {
        var t0 = Clock.Now;
        host.ApplyScrollAndLayout(offset);
        var t1 = Clock.Now;
        PumpDispatcher();
        var t2 = Clock.Now;
        return (Clock.Ms(t0, t1), Clock.Ms(t1, t2));
    }

    private static void PumpDispatcher()
    {
        // 以 Background 优先级推一个空操作：把队列里 >=Background 的条目全部处理掉
        // （容器回收/生成动作排在 Background，渲染失效排在 Render）。
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, static () => { });
    }

    /// <summary>
    /// 用真实合成帧回调驱动若干帧。返回（帧间隔序列, 回调内工作耗时序列）。
    /// 若本机没有合成器（离屏模式），回调永远不会来，由 5 秒超时兜底。
    /// </summary>
    private static (List<double> Intervals, List<double> Work) PumpFrames(int count, Action? onFrame)
    {
        var intervals = new List<double>(count);
        var work = new List<double>(count);
        long last = 0;
        var n = 0;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) =>
        {
            frame.Continue = false;
            timeout.Stop();
        };

        EventHandler handler = (_, _) =>
        {
            var now = Clock.Now;
            if (last != 0)
            {
                intervals.Add(Clock.Ms(last, now));
            }

            last = now;
            if (onFrame is not null)
            {
                var w0 = Clock.Now;
                onFrame();
                work.Add(Clock.Ms(w0, Clock.Now));
            }

            if (++n >= count)
            {
                frame.Continue = false;
            }
        };

        CompositionTarget.Rendering += handler;
        timeout.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
            timeout.Stop();
        }

        return (intervals, work);
    }

    private static double MeasureRasterization(ListHost host)
    {
        var w = (int)Math.Round(host.Root.ActualWidth);
        var h = (int)Math.Round(host.Root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var samples = new List<double>(20);
        for (var i = 0; i < 20; i++)
        {
            var t0 = Clock.Now;
            rtb.Render(host.Root);
            samples.Add(Clock.Ms(t0, Clock.Now));
        }

        return Stats.From(samples).P50;
    }

    private static void SavePng(ListHost host, double offset, string path, string label)
    {
        host.ApplyScrollAndLayout(offset);
        var w = (int)Math.Round(host.Root.ActualWidth);
        var h = (int)Math.Round(host.Root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

        // 第一趟：真实渲染管线把可视树光栅化（证明 WPF 渲染跑得通）
        rtb.Render(host.Root);

        // 第二趟：在缩略图条下方的空白区叠一行半透明标注，方便文档/人工辨认是哪个场景
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bar = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x10));
            bar.Freeze();
            var barTop = Math.Max(ThumbCell.CellHeight + 6, h - 56);
            dc.DrawRectangle(bar, null, new Rect(0, barTop, w, 24));
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                13,
                Brushes.White,
                1.0);
            dc.DrawText(text, new Point(8, barTop + 3));
        }

        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
