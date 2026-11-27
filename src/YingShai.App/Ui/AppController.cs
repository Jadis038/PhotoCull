using System.Security.Cryptography;
using System.Windows;
using YingShai.Core;
using YingShai.Core.Model;
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
            // 每次启动检查 GitHub Releases 更新（--update-auto-accept 为自动化测试钩子）
            _ = CheckUpdateAsync(args.Contains("--update-auto-accept"));
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

    /// <summary>启动后检查 GitHub Releases 更新；有新版本弹更新对话框（网络失败静默跳过）。</summary>
    private static async Task CheckUpdateAsync(bool autoAccept)
    {
        try
        {
            var info = await YingShai.Core.Update.UpdateChecker.CheckLatestAsync();
            YingShai.Core.Util.AppLog.Info(info == null
                ? "更新检查：GitHub 不可达，跳过"
                : $"更新检查：最新 {info.TagName}，当前 v{YingShai.Core.Update.UpdateInfo.CurrentVersion.ToString(3)}，有更新={info.HasUpdate}");
            if (info?.HasUpdate != true || _window == null || _vm == null) return;
            await _window.Dispatcher.InvokeAsync(async () =>
            {
                if (autoAccept)
                {
                    // 测试钩子：跳过对话框，直接下载并启动安装
                    var dest = Path.Combine(Path.GetTempPath(), $"PhotoCull-Setup-{info.LatestVersion}.msi");
                    await YingShai.Core.Update.UpdateChecker.DownloadAsync(info.MsiUrl!, dest, null, CancellationToken.None);
                    YingShai.Core.Update.UpdateChecker.LaunchInstaller(dest);
                    Application.Current.Shutdown(0);
                    return;
                }
                await UpdateDialog.ShowAndInstallAsync(_window, info);
            });
        }
        catch { }
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

                        // ——— 4) 回归：换一张图后双击放大，必须显示"新图"的全解，而不是上一张的缓存 ———
                        var itemA = _vm.Current!;
                        var itemB = _session.Items.FirstOrDefault(i => !ReferenceEquals(i, itemA));
                        if (itemB != null)
                        {
                            win.RaiseThumbPreviewDown(itemB);
                            for (int i = 0; i < 120 && _vm.Current != itemB; i++) await Task.Delay(50);
                            bool cleared = win.FullResOwnerForTest != itemA.FullPath;
                            lines.Add($"换图后 100% 缓存作废：{cleared}（当前 {itemB.Name}，缓存归属={win.FullResOwnerForTest ?? "无"}）");
                            if (!cleared) exitCode = 1;

                            var zb = await win.TestDoubleClickZoomAsync();
                            bool ownerOk = win.FullResOwnerForTest == itemB.FullPath && zb.Text == "100%";
                            lines.Add($"新图双击放大：{ownerOk}（{zb.Text}，缓存归属={win.FullResOwnerForTest ?? "无"}，应={itemB.Name}）");
                            if (!ownerOk) exitCode = 1;

                            await win.TestDoubleClickZoomAsync();   // 回到适应窗口
                        }

                        // ——— 5) 真实滚轮：画面必须真的动（对比截图哈希），Ctrl+滚轮走横向 ———
                        win.ResetFit();
                        await Task.Delay(150);
                        string hash0 = RenderHash(win, outPng, "_wheel0");
                        var sp = win.GetImageAreaCenterOnScreen();
                        MouseInput.Wheel((int)sp.X, (int)sp.Y, -2);              // 滚轮向下两格
                        await Task.Delay(250);
                        string hash1 = RenderHash(win, outPng, "_wheel1");
                        var st1 = win.ZoomState;
                        lines.Add($"真实滚轮（上下）：画面变化={hash0 != hash1}，Ty={st1.Ty:F0}");
                        if (hash0 == hash1) exitCode = 1;

                        MouseInput.Wheel((int)sp.X, (int)sp.Y, -2, ctrl: true);  // Ctrl+滚轮
                        await Task.Delay(250);
                        string hash2 = RenderHash(win, outPng, "_wheel2");
                        var st2 = win.ZoomState;
                        lines.Add($"真实 Ctrl+滚轮（左右）：画面变化={hash1 != hash2}，Tx={st2.Tx:F0}（缩放 {st2.Scale:F3} 不变）");
                        if (hash1 == hash2 || Math.Abs(st2.Scale - st1.Scale) > 0.001) exitCode = 1;

                        MouseInput.Wheel((int)sp.X, (int)sp.Y, 2, alt: true);    // Alt+滚轮
                        await Task.Delay(250);
                        var st3 = win.ZoomState;
                        bool altZoom = st3.Scale > st2.Scale + 0.01;
                        lines.Add($"真实 Alt+滚轮（缩放）：{altZoom}  {st2.Scale:F3}→{st3.Scale:F3}");
                        if (!altZoom) exitCode = 1;
                    }
                }

                if (args.Contains("--playhead-test") && _session != null && _vm != null)
                {
                    win.Activate();
                    await Task.Delay(300);

                    // ——— 1) 开启帧针模式 ———
                    win.SetPlayheadForTest(true);
                    lines.Add($"帧针模式：开启={win.PlayheadVisibleForTest()}（中线+顶部三角可见）");
                    if (!win.PlayheadVisibleForTest()) exitCode = 1;

                    // ——— 2) 滚动条拖到第 4 张：帧针判定 → 上方显示这张 ———
                    var t4 = _session.Items.Count > 4 ? _session.Items[4] : _session.Items[^1];
                    win.CenterThumbForTest(t4);
                    for (int i = 0; i < 120 && _vm.Current != t4; i++) await Task.Delay(50);
                    bool pick4 = _vm.Current == t4 && _vm.LargeImage != null;
                    lines.Add($"帧针判定：滚到 {t4.Name} → 当前项={_vm.Current?.Name}，大图就绪={_vm.LargeImage != null} → {(pick4 ? "通过" : "失败")}");
                    if (!pick4) exitCode = 1;

                    // ——— 3) 再滚到第 0 张 ———
                    var t0 = _session.Items[0];
                    win.CenterThumbForTest(t0);
                    for (int i = 0; i < 120 && _vm.Current != t0; i++) await Task.Delay(50);
                    bool pick0 = _vm.Current == t0;
                    lines.Add($"帧针判定：滚回 {t0.Name} → 当前项={_vm.Current?.Name} → {(pick0 ? "通过" : "失败")}");
                    if (!pick0) exitCode = 1;

                    // ——— 4) 空格键 = 双击缩略图（确认标记，画面停留）———
                    var cur = _vm.Current!;
                    win.PressSpaceForTest();
                    for (int i = 0; i < 120 && cur.State != MarkState.Marked; i++) await Task.Delay(50);
                    bool marked = cur.State == MarkState.Marked && _vm.Current == cur;
                    lines.Add($"空格键标记：{cur.Name} 状态={cur.State}（应 Marked），画面停留={_vm.Current == cur}，已筛 {_session.MarkedCount}/{_session.TotalCount}");
                    if (!marked) exitCode = 1;

                    // ——— 5) 空格再按一次 = 取消标记 ———
                    win.PressSpaceForTest();
                    for (int i = 0; i < 120 && cur.State != MarkState.None; i++) await Task.Delay(50);
                    bool unmarked = cur.State == MarkState.None;
                    lines.Add($"空格再按：取消标记={unmarked}，状态={cur.State}");
                    if (!unmarked) exitCode = 1;

                    // 在帧针状态下截一张图（能看到中线）
                    await Task.Delay(300);
                    string phPng = Path.Combine(Path.GetDirectoryName(outPng)!, Path.GetFileNameWithoutExtension(outPng) + "_playhead.png");
                    win.RenderToPng(phPng);
                    lines.Add($"已渲染（帧针模式）：{phPng}");
                    win.SetPlayheadForTest(false);
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

    /// <summary>渲染当前窗口并返回 PNG 的短哈希（用于判断画面是否真的变化）。</summary>
    private static string RenderHash(MainWindow win, string basePng, string suffix)
    {
        var path = Path.Combine(Path.GetDirectoryName(basePng)!, Path.GetFileNameWithoutExtension(basePng) + suffix + ".png");
        win.RenderToPng(path);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..12];
    }
}
