using System.Security.Cryptography;
using System.Windows;
using YingShai.Core;
using YingShai.Core.Index;
using YingShai.Core.Ops;
using YingShai.Core.Session;
using YingShai.Core.Settings;

namespace YingShai.App.Ui;

/// <summary>应用启动流程：单实例、命令行参数、恢复上次会话、窗口创建。</summary>
public static class AppController
{
    private static Mutex? _mutex;
    private static MainWindow? _window;
    private static ViewerSession? _session;
    private static MainViewModel? _vm;

    public static void Start(string[] args)
    {
        AppPaths.EnsureDirectories();

        // 全局异常登记：任何未处理异常都写进 logs\app.log，方便定位（也用于无桌面验收）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            YingShai.Core.Util.AppLog.Error("未处理异常（AppDomain）", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            YingShai.Core.Util.AppLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
        Application.Current.DispatcherUnhandledException += (_, e) =>
            YingShai.Core.Util.AppLog.Error("UI 线程未处理异常", e.Exception);

        bool uiSelfTest = args.Any(a => a == "--ui-selftest");
        if (!uiSelfTest)
        {
            bool primary = SingleInstance.TryBecomePrimary(args, out _mutex);
            if (!primary)
            {
                Application.Current.Shutdown(0);
                return;
            }
        }

        var settings = AppSettings.Load();
        var store = new StateStore();
        var session = new ViewerSession(settings, store) { UiContext = SynchronizationContext.Current };
        var vm = new MainViewModel(session);
        var win = new MainWindow(vm)
        {
            Width = Math.Max(960, settings.WindowWidth),
            Height = Math.Max(620, settings.WindowHeight)
        };
        _window = win;
        _session = session;
        _vm = vm;

        if (!uiSelfTest)
        {
            SingleInstance.StartServer(a => Application.Current.Dispatcher.BeginInvoke(new Action(() => HandleNewArgs(a))));
        }

        win.Loaded += async (_, _) =>
        {
            if (uiSelfTest)
            {
                await RunUiSelfTestAsync(win, vm, args);
                return;
            }
            await InitialOpenAsync(args, settings);
        };

        win.Show();
    }

    private static async Task InitialOpenAsync(string[] args, AppSettings settings)
    {
        if (_window == null || _session == null) return;
        string? path = args.FirstOrDefault(a => !a.StartsWith("-") && (File.Exists(a) || Directory.Exists(a)));
        if (path != null)
        {
            if (File.Exists(path)) await _window.OpenFolderAsync(Path.GetDirectoryName(Path.GetFullPath(path))!, path);
            else await _window.OpenFolderAsync(path);
            return;
        }
        if (settings.RestoreLastSession && !string.IsNullOrEmpty(settings.LastFolder) && Directory.Exists(settings.LastFolder))
            await _window.OpenFolderAsync(settings.LastFolder);
    }

    private static async void HandleNewArgs(string[] args)
    {
        if (_window == null) return;
        YingShai.Core.Util.AppLog.Info($"收到新实例请求：{string.Join(" ", args)}");
        string? path = args.FirstOrDefault(a => !a.StartsWith("-") && (File.Exists(a) || Directory.Exists(a)));
        if (path != null)
        {
            if (File.Exists(path)) await _window.OpenFolderAsync(Path.GetDirectoryName(Path.GetFullPath(path))!, path);
            else await _window.OpenFolderAsync(path);
        }
        _window.Activate();
    }

    /// <summary>
    /// 无交互桌面下的 UI 验收：打开文件夹 → 等待大图就绪 → 渲染一帧 PNG → 退出。
    /// 用法：影·筛.exe --ui-selftest &lt;文件夹&gt; [输出.png]
    /// </summary>
    private static async Task RunUiSelfTestAsync(MainWindow win, MainViewModel vm, string[] args)
    {
        string? folder = args.SkipWhile(a => a != "--ui-selftest").Skip(1).FirstOrDefault(a => Directory.Exists(a));
        string outPng = args.SkipWhile(a => a != "--ui-selftest").Skip(1)
            .FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(Path.GetTempPath(), "影筛_ui_selftest.png");
        string logPath = Path.ChangeExtension(outPng, ".log");
        var lines = new List<string>();
        int exitCode = 0;
        try
        {
            if (string.IsNullOrEmpty(folder))
            {
                lines.Add("FAIL: 没有指定文件夹参数");
                exitCode = 2;
            }
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = await win.OpenFolderAsync(folder);
                lines.Add($"打开文件夹：{ok}，{_session?.TotalCount} 张，枚举 {_session?.LastScanMs:F0} ms");
                // 等大图就绪（最多 10 秒）
                for (int i = 0; i < 100 && vm.LargeImage == null; i++)
                    await Task.Delay(100);
                lines.Add($"大图就绪：{vm.LargeImage != null}（大图 {(vm.LargeImage?.PixelWidth ?? 0)}×{vm.LargeImage?.PixelHeight ?? 0}），首帧 {sw.ElapsedMilliseconds} ms");
                await Task.Delay(600); // 让缩略图加载一会儿
                int withThumb = _session?.Items.Count(x => x.Thumb != null) ?? 0;
                lines.Add($"缩略图就绪：{withThumb}/{_session?.TotalCount}");
                lines.Add($"窗口大小：{win.ActualWidth:F0}×{win.ActualHeight:F0}");

                // 先出"标记前"的截图
                win.RenderToPng(outPng);
                lines.Add($"已渲染（标记前）：{outPng}");
                lines.Add($"布局：{win.LayoutDebug}");

                if (args.Contains("--click-test") && _session != null && _vm != null)
                {
                    // 单击选择测试：模拟鼠标单击缩略图（同一条选中路径），验证上方立即查看这张图
                    var target = _session.Items.Count > 5 ? _session.Items[5] : _session.Items[^1];
                    var before = _vm.Current;
                    var click = await win.SimulateThumbClickAsync(target);
                    lines.Add($"单击缩略图测试：从 {before?.Name} 单击 {target.Name} → 当前项一致={click.CurrentOk}，大图已切换={click.ImageOk}，耗时 {click.Ms:F0} ms");
                    if (!click.CurrentOk || !click.ImageOk) exitCode = 1;

                    // 标记后停留测试：双击/标记当前图后，预览不再跳到下一张
                    var marked = _vm.Current!;
                    var mr = await _session.ToggleMarkAsync(marked);
                    marked.ApplyMarkVisual();
                    _vm.RaiseCounts();
                    await Task.Delay(150);
                    bool stayed = _vm.Current == marked;
                    lines.Add($"标记后停留测试：标记 {marked.Name} {(mr.Ok ? "成功" : "失败 " + mr.Error)}，当前项未移动={stayed}，已筛 {_session.MarkedCount}/{_session.TotalCount}");
                    if (!stayed || !mr.Ok) exitCode = 1;

                    _vm.StatusText = $"已筛 {_session.MarkedCount} 张";
                    await Task.Delay(700);
                    string markedPng = Path.Combine(Path.GetDirectoryName(outPng)!, Path.GetFileNameWithoutExtension(outPng) + "_marked.png");
                    win.RenderToPng(markedPng);
                    lines.Add($"已渲染（单击选中+标记后）：{markedPng}");
                }

                if (args.Contains("--real-click-test") && _session != null && _vm != null)
                {
                    win.Activate();
                    await Task.Delay(300);

                    // ——— 1) 单击缩略图 → 上方查看（真实路由事件，走 OnThumbPreviewMouseDown 处理链）———
                    var target = _session.Items.Count > 4 ? _session.Items[4] : _session.Items[^1];
                    var before = _vm.Current;
                    bool raised = win.RaiseThumbPreviewDown(target);
                    var clickSw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 160 && (_vm.Current != target || _vm.LargeImage == null); i++)
                        await Task.Delay(50);
                    clickSw.Stop();
                    bool clickOk = raised && _vm.Current == target && _vm.LargeImage != null;
                    lines.Add($"单击缩略图（路由事件）：{before?.Name} → {_vm.Current?.Name}，容器找到={raised}，大图就绪={_vm.LargeImage != null}，耗时 {clickSw.ElapsedMilliseconds} ms → {(clickOk ? "通过" : "失败")}");
                    if (!clickOk) exitCode = 1;

                    // ——— 1b) 真实鼠标注入点击另一张，端到端验证（鼠标捕获不卡住时才有效）———
                    var target2 = _session.Items.Count > 2 ? _session.Items[2] : _session.Items[0];
                    var screenPos2 = win.GetThumbItemCenterOnScreen(target2);
                    if (screenPos2 != null)
                    {
                        MouseInput.Click((int)screenPos2.Value.X, (int)screenPos2.Value.Y);
                        for (int i = 0; i < 120 && _vm.Current != target2; i++) await Task.Delay(50);
                        bool realOk = _vm.Current == target2 && _vm.LargeImage != null;
                        lines.Add($"真实鼠标点击：目标 {target2.Name}（屏幕 {screenPos2.Value.X:F0},{screenPos2.Value.Y:F0}）→ 当前项={_vm.Current?.Name}，大图就绪={_vm.LargeImage != null} → {(realOk ? "通过" : "失败")}");
                        if (!realOk) exitCode = 1;
                    }

                    // ——— 2) 滚轮规则（Photoshop 手感）：Alt 缩放 / Ctrl 左右 / 滚轮上下 ———
                    if (_vm.LargeImage != null)
                    {
                        var centerPos = new Point(win.GetImageAreaRect().Width / 2, win.GetImageAreaRect().Height / 2);
                        win.ResetFit();
                        await Task.Delay(120);
                        var s0 = win.ZoomState;

                        win.ApplyWheel(centerPos, -120, alt: false, ctrl: false);   // 滚轮向下
                        var s1 = win.ZoomState;
                        bool wheelPanY = Math.Abs(s1.Tx - s0.Tx) < 0.5 && Math.Abs(s1.Ty - s0.Ty) > 1;
                        lines.Add($"滚轮（无修饰）→ 上下平移={wheelPanY}  Ty {s0.Ty:F0}→{s1.Ty:F0}（X 不变={Math.Abs(s1.Tx - s0.Tx) < 0.5}）");
                        if (!wheelPanY) exitCode = 1;

                        win.ApplyWheel(centerPos, -120, alt: false, ctrl: true);    // Ctrl+滚轮
                        var s2 = win.ZoomState;
                        bool wheelPanX = Math.Abs(s2.Tx - s1.Tx) > 1 && Math.Abs(s2.Scale - s1.Scale) < 0.001;
                        lines.Add($"Ctrl+滚轮 → 左右平移={wheelPanX}  Tx {s1.Tx:F0}→{s2.Tx:F0}（缩放不变={Math.Abs(s2.Scale - s1.Scale) < 0.001}）");
                        if (!wheelPanX) exitCode = 1;

                        win.ApplyWheel(centerPos, 120, alt: true, ctrl: false);     // Alt+滚轮
                        var s3 = win.ZoomState;
                        bool wheelZoom = s3.Scale > s2.Scale + 0.01;
                        lines.Add($"Alt+滚轮 → 缩放={wheelZoom}  {s2.Scale:F3}→{s3.Scale:F3}");
                        if (!wheelZoom) exitCode = 1;

                        // ——— 3) 双击：适应窗口 → 100% → 适应窗口 ———
                        win.ResetFit();
                        await Task.Delay(120);
                        var z1 = await win.TestDoubleClickZoomAsync();
                        bool toActual = z1.Text == "100%" && Math.Abs(z1.Scale - 1.0) < 0.001;
                        lines.Add($"双击大图 → 100%：{toActual}（{z1.Text}，scale={z1.Scale:F3}，原图 {_vm.FullImage?.PixelWidth ?? 0}×{_vm.FullImage?.PixelHeight ?? 0}）");
                        if (!toActual) exitCode = 1;

                        // 在 100% 状态下截一张图作为证据
                        await Task.Delay(200);
                        string zoomPng = Path.Combine(Path.GetDirectoryName(outPng)!, Path.GetFileNameWithoutExtension(outPng) + "_zoom100.png");
                        win.RenderToPng(zoomPng);
                        lines.Add($"已渲染（100% 断言时）：{zoomPng}");
                        lines.Add($"100% 布局：{win.LayoutDebug}");
                        lines.Add($"100% 时大图区有效像素占比：{win.MeasureImageAreaBrightness():F1}%（应显著大于 0，证明大图真的画出来了）");
                        if (win.MeasureImageAreaBrightness() < 2.0)
                        {
                            lines.Add("100% 大图区几乎全黑 → 视口裁剪渲染失败");
                            exitCode = 1;
                        }

                        // 在 100% 状态下平移一大步，截图应明显变化（证明画布真的在动）
                        string beforePanHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zoomPng)))[..12];
                        win.PanForTest(-420, -260);
                        await Task.Delay(200);
                        string panPng = Path.ChangeExtension(zoomPng, null) + "_pan.png";
                        win.RenderToPng(panPng);
                        string afterPanHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(panPng)))[..12];
                        lines.Add($"100% 平移后：画面变化={beforePanHash != afterPanHash}（{beforePanHash} → {afterPanHash}），布局：{win.LayoutDebug}");
                        if (beforePanHash == afterPanHash)
                        {
                            lines.Add("100% 平移后画面没有变化 → 平移没生效");
                            exitCode = 1;
                        }

                        var z2 = await win.TestDoubleClickZoomAsync();
                        bool backToFit = z2.Text == "适应窗口" && z2.Scale < 1.0;
                        lines.Add($"再双击大图 → 适应窗口：{backToFit}（{z2.Text}，scale={z2.Scale:F3}）");
                        if (!backToFit) exitCode = 1;

                        // 真实注入滚轮（有桌面会话时生效；无桌面时会安静地无效，仅作参考）
                        var screenPos = win.GetImageAreaCenterOnScreen();
                        var sz0 = win.ZoomState;
                        MouseInput.Wheel((int)screenPos.X, (int)screenPos.Y, -1);
                        await Task.Delay(200);
                        var sz1 = win.ZoomState;
                        bool injectedWheel = Math.Abs(sz1.Ty - sz0.Ty) > 1;
                        lines.Add($"（参考）真实滚轮注入：Ty {sz0.Ty:F0}→{sz1.Ty:F0}，{(injectedWheel ? "生效" : "本机未生效（无交互桌面）")}");
                    }
                }

                if (args.Contains("--mark-demo") && _session != null && _vm != null)
                {
                    // 标记演示：用于截图证明角标/灰调/计数
                    var targets = _session.Items.Skip(1).Where((_, i) => i % 2 == 0).Take(2).ToList();
                    foreach (var t in targets)
                    {
                        var r = await _session.ToggleMarkAsync(t);
                        t.ApplyMarkVisual();
                        lines.Add($"标记 {t.Name}：{(r.Ok ? "成功" : "失败 " + r.Error)}");
                    }
                    _vm.RaiseCounts();
                    _vm.StatusText = $"已筛 {_session.MarkedCount} 张";
                    await Task.Delay(700);
                    string markedPng = Path.Combine(Path.GetDirectoryName(outPng)!, Path.GetFileNameWithoutExtension(outPng) + "_marked.png");
                    win.RenderToPng(markedPng);
                    lines.Add($"已渲染（带标记）：{markedPng}，已筛 {_session.MarkedCount}/{_session.TotalCount}");
                }

                if (vm.Current != null)
                {
                    try
                    {
                        var full = await win.TestFullResAsync();
                        lines.Add($"100% 全量解码：{(full.Ok ? "成功" : "失败")} {full.W}×{full.H}，耗时 {full.Ms:F0} ms（后台线程，界面不阻塞）");
                        if (!full.Ok) exitCode = 1;
                    }
                    catch (Exception ex)
                    {
                        lines.Add("100% 全量解码异常：" + ex.GetType().Name + ": " + ex.Message);
                        exitCode = 1;
                    }
                }

                if (vm.LargeImage == null) exitCode = 1;
            }
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION: " + ex);
            exitCode = 1;
        }
        finally
        {
            try { File.WriteAllLines(logPath, lines, System.Text.Encoding.UTF8); } catch { }
            Application.Current.Shutdown(exitCode);
        }
    }
}
