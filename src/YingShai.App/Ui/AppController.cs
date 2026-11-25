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
