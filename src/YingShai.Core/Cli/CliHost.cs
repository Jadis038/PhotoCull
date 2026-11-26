using System.Runtime.InteropServices;
using System.Text;

namespace YingShai.Core.Cli;

/// <summary>命令行入口的统一分发（--selftest / --bench / --version / --help）。</summary>
public static class CliHost
{
    /// <summary>程序版本（跟随程序集版本，避免两处手改不一致）。</summary>
    public static string Version =>
        (typeof(CliHost).Assembly.GetName().Version is { } v ? v.ToString(3) : "1.0.0");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    /// <summary>WinExe 默认没有控制台，需要挂到父进程控制台才能看到输出。</summary>
    public static void EnsureConsole()
    {
        try
        {
            if (!AttachConsole(-1)) AllocConsole();
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { }
    }

    public static bool IsConsoleMode(string[] args)
        => args.Any(a => a is "--selftest" or "--bench" or "--version" or "-v" or "--help" or "-h" or "/?"
            or "--update-check" or "--update-auto-accept" or "--matrix" or "--diag" or "--diag-meta"
            or "--decode-full" or "--shell-thumb" or "--make-icon");

    /// <summary>CLI 模式需要异步的分支走这里。</summary>
    public static Task<int> RunAsync(string[] args) => RunAsyncInner(args);

    private static async Task<int> RunAsyncInner(string[] args)
    {
        if (args.Length == 0) return 0;
        switch (args[0])
        {
            case "--version":
            case "-v":
                Console.WriteLine($"影·筛（YingShai） {Version}");
                Console.WriteLine($"LibRaw {Imaging.LibRawInterop.Version}");
                return 0;

            case "--selftest":
                return new SelfTest().Run();

            case "--update-check":
            case "--update-auto-accept":
            {
                // 更新流程（无 GUI）：--update-check 只检测并打印；--update-auto-accept 检测→下载→启动安装（测试钩子）
                bool auto = args[0] == "--update-auto-accept";
                var info = await Update.UpdateChecker.CheckLatestAsync();
                if (info == null) { Console.WriteLine("更新检查失败：GitHub 不可达"); return 1; }
                Console.WriteLine($"最新版本：{info.TagName}    当前版本：v{Update.UpdateInfo.CurrentVersion.ToString(3)}    有更新：{info.HasUpdate}");
                if (!info.HasUpdate) return 0;
                Console.WriteLine($"安装包：{info.MsiUrl}");
                if (!auto) return 0;
                var dest = Path.Combine(Path.GetTempPath(), $"PhotoCull-Setup-{info.LatestVersion}.msi");
                Console.WriteLine("开始下载…");
                await Update.UpdateChecker.DownloadAsync(info.MsiUrl!, dest,
                    (done, total) => { if (Console.CursorLeft == 0) Console.Write($"  已下载 {done / 1048576.0:F1} MB" + (total > 0 ? $" / {total / 1048576.0:F1} MB" : "")); },
                    CancellationToken.None);
                Console.WriteLine();
                Console.WriteLine($"下载完成：{dest}（{new FileInfo(dest).Length / 1048576.0:F1} MB）");
                Update.UpdateChecker.LaunchInstaller(dest);
                Console.WriteLine("已启动安装程序（应用 2 秒后退出交给安装）");
                return 0;
            }

            case "--decode-full":
            {
                // 诊断：走正式的 100% 全解路径（内嵌全尺寸 JPEG 优先 → demosaic 兜底），可导出 PNG 检查
                if (args.Length < 2) { Console.Error.WriteLine("用法：--decode-full <raw> [out.png]"); return 2; }
                var outcome = Imaging.ImageDecoder.DecodeFull(args[1]);
                Console.WriteLine($"source={outcome.Source} ok={outcome.Ok} err={outcome.Error} {outcome.ElapsedMs:F0}ms " +
                                  $"bitmap={outcome.Bitmap?.PixelWidth}x{outcome.Bitmap?.PixelHeight}");
                if (!outcome.Ok || outcome.Bitmap == null) return 1;
                if (args.Length > 2)
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(outcome.Bitmap));
                    using var fs = File.Create(args[2]);
                    enc.Save(fs);
                    Console.WriteLine($"dumped={args[2]} {outcome.Bitmap.PixelWidth}x{outcome.Bitmap.PixelHeight}");
                }
                return 0;
            }

            case "--shell-thumb":
            {
                // 诊断：测试 Windows 资源管理器同源缩略图（系统 RAW 编解码器/缩略图缓存）
                if (args.Length < 2) { Console.Error.WriteLine("用法：--shell-thumb <图片> [尺寸] [out.png]"); return 2; }
                int size = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 256;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = Imaging.ShellThumbnail.TryGet(args[1], size, out var bmp);
                sw.Stop();
                if (!ok || bmp == null) { Console.WriteLine($"FAIL（{sw.ElapsedMilliseconds}ms）——系统无法生成该格式的缩略图"); return 1; }
                Console.WriteLine($"ok {bmp.PixelWidth}x{bmp.PixelHeight}（{sw.ElapsedMilliseconds}ms）");
                if (args.Length > 3)
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var fs = File.Create(args[3]);
                    enc.Save(fs);
                }
                return 0;
            }

            case "--make-icon":
            {
                // 生成程序图标：--make-icon <out.ico> 用内置默认图；--make-icon <out.ico> <source.png> 用指定图片
                if (args.Length < 2) { Console.Error.WriteLine("用法：--make-icon <out.ico> [source.png]"); return 2; }
                return IconMaker.MakeIcon(args[1], args.Length > 2 ? args[2] : null);
            }

            case "--matrix":
            {
                if (args.Length < 2) { Console.Error.WriteLine("用法：--matrix <文件夹> [--out x.csv] [--full-decode]"); return 2; }
                string? outCsv = null;
                bool full = false;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--out" && i + 1 < args.Length) outCsv = args[++i];
                    else if (args[i] == "--full-decode") full = true;
                }
                return Matrix.Run(args[1], outCsv, full);
            }

            case "--extract-thumb":
            {
                // 测试素材用：把 RAW 的内嵌 JPEG 抽出来存盘（生成 RAW+JPG 双拍样本）
                if (args.Length < 3) { Console.Error.WriteLine("用法：--extract-thumb <raw> <out.jpg>"); return 2; }
                if (!Imaging.LibRawImage.TryOpen(args[1], out var img, out var err) || img == null)
                { Console.Error.WriteLine("打开失败：" + err); return 1; }
                using (img)
                {
                    var prev = img.TryReadPreview(out var perr);
                    if (prev?.JpegData == null) { Console.Error.WriteLine("没有内嵌 JPEG：" + perr); return 1; }
                    File.WriteAllBytes(args[2], prev.JpegData);
                    Console.WriteLine($"ok {args[2]} {prev.JpegData.Length} bytes");
                }
                return 0;
            }

            case "--diag-meta":
            {
                string f = args[1];
                var outcome = Imaging.ImageDecoder.DecodePreview(f, Imaging.ImageDecoder.StripSize);
                var m = outcome.Metadata;
                Console.WriteLine($"file={f}");
                Console.WriteLine($"source={outcome.Source} ok={outcome.Ok} err={outcome.Error} {outcome.ElapsedMs:F0}ms bitmap={outcome.Bitmap?.PixelWidth}x{outcome.Bitmap?.PixelHeight}");
                Console.WriteLine($"make={m.Make} model={m.Model} lens={m.Lens} focal={m.FocalLengthMm} ap={m.ApertureF} sh={m.ShutterSeconds} iso={m.Iso} taken={m.TakenAt} res={m.PixelWidth}x{m.PixelHeight} cs={m.ColorSpace} orient={m.Orientation}");
                if (args.Length > 2 && outcome.Bitmap != null)
                {
                    // 把解码结果写成 PNG，人工核对 EXIF 方向是否正确
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(outcome.Bitmap));
                    using var fs = File.Create(args[2]);
                    enc.Save(fs);
                    Console.WriteLine($"dumped={args[2]} {outcome.Bitmap.PixelWidth}x{outcome.Bitmap.PixelHeight}");
                }
                if (Imaging.LibRawImage.TryOpen(f, out var raw, out var err) && raw != null)
                {
                    var rm = raw.ReadMetadata();
                    Console.WriteLine($"[LibRaw only] make={rm.Make} model={rm.Model} lens={rm.Lens} iso={rm.Iso} taken={rm.TakenAt} res={rm.PixelWidth}x{rm.PixelHeight}");
                    var prev = raw.TryReadPreview(out var perr);
                    Console.WriteLine($"[preview] kind={prev?.Kind} w={prev?.Width} h={prev?.Height} size={prev?.RawSize} err={perr}");
                    if (prev?.JpegData != null)
                    {
                        var d = Imaging.WicImage.DecodeScaled(prev.JpegData, 1024);
                        Console.WriteLine($"[exif] model={d.Exif.Model} lens={d.Exif.LensModel} iso={d.Exif.Iso} taken={d.Exif.DateTimeOriginal} cs={d.Exif.ColorSpace} orient={d.Exif.Orientation}");
                    }
                    raw.Dispose();
                }
                else Console.WriteLine($"[LibRaw] open failed: {err}");
                return 0;
            }

            case "--diag":
            {
                // 诊断：验证用户数据目录可写（中文 + 中间点目录名在个别环境里会踩到权限/安全软件的坑）
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var targets = new (string label, string path)[]
                {
                    ("LOCALAPPDATA/ascii", Path.Combine(baseDir, "yingshai_diag_ascii")),
                    ("LOCALAPPDATA/middot", Path.Combine(baseDir, "影·筛_diag")),
                    ("LOCALAPPDATA/file", Path.Combine(baseDir, "yingshai_diag_file.txt")),
                    ("ROAMING/ascii", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yingshai_diag")),
                    ("TEMP/ascii", Path.Combine(Path.GetTempPath(), "yingshai_diag")),
                    ("USERPROFILE/ascii", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "yingshai_diag")),
                    ("WORKSPACE/ascii", @"D:\DSH工作区\yingshai\toolchain_diag"),
                };
                foreach (var (label, p) in targets)
                {
                    try
                    {
                        if (p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(p, "x");
                            File.Delete(p);
                        }
                        else
                        {
                            Directory.CreateDirectory(p);
                            Directory.Delete(p, true);
                        }
                        Console.WriteLine($"OK   {label,-22} {p}");
                    }
                    catch (Exception ex) { Console.WriteLine($"FAIL {label,-22} {p}  {ex.GetType().Name}: {ex.Message}"); }
                }
                return 0;
            }

            case "--bench":
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("用法：影·筛.exe --bench <文件夹> [--csv <输出.csv>] [--cold] [--nowarm]");
                    return 2;
                }
                string folder = args[1];
                string? csv = null;
                bool cold = false, warm = true;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--csv" && i + 1 < args.Length) csv = args[++i];
                    else if (args[i] == "--cold") cold = true;
                    else if (args[i] == "--nowarm") warm = false;
                }
                return new Bench().Run(folder, csv, cold, warm);
            }

            default:
                PrintHelp();
                return args[0].StartsWith("-") ? 2 : 0;
        }
    }

    /// <summary>同步包装（控制台 Main 用；GUI 入口不走这里）。</summary>
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static void PrintHelp()
    {
        Console.WriteLine("""
            影·筛（YingShai / PhotoCull）— Windows 图片快速筛选工具

            用法：
              影·筛.exe                          打开图形界面
              影·筛.exe <文件夹>                 打开该文件夹
              影·筛.exe <图片文件>               打开所在文件夹并选中该图
              影·筛.exe --selftest               跑内置自检（扫描/排序/复制/冲突改名/取消/索引重建/撤销）
              影·筛.exe --bench <文件夹>         跑性能基准测试并输出 CSV
              影·筛.exe --bench <文件夹> --cold  先清空缩略图缓存再测（冷启动）
              影·筛.exe --matrix <文件夹>        逐文件"能否打开"测试矩阵
              影·筛.exe --update-check           检查 GitHub 最新版本（无 GUI）
              影·筛.exe --update-auto-accept     检测到新版本直接下载并启动安装（自动化测试用）
              影·筛.exe --version                版本信息

            缓存与索引：%LOCALAPPDATA%\影·筛\（cache / state / logs / settings.json）
            """);
    }
}
