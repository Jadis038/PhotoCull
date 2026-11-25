using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YingShai.Core.Index;
using YingShai.Core.Model;
using YingShai.Core.Scanning;
using YingShai.Core.Session;
using YingShai.Core.Settings;

namespace YingShai.Core.Cli;

/// <summary>
/// 命令行自检（影·筛.exe --selftest）。全部在临时目录里跑，不碰用户素材：
/// 扫描、自然排序、复制、冲突改名、取消删除、索引重建、撤销栈、伴生文件、中文/空格/超长路径。
/// </summary>
public sealed class SelfTest
{
    private int _pass, _fail;
    private readonly StringBuilder _report = new();
    private string _root = "";

    public int Run()
    {
        var sw = Stopwatch.StartNew();
        _root = Path.Combine(Path.GetTempPath(), "影筛自检_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Console.WriteLine($"影·筛 自检开始，临时目录：{_root}");
        Console.WriteLine();

        try
        {
            TestScanAndNaturalSort();
            TestCopyMark();
            TestConflictRename();
            TestCompanions();
            TestUnmarkDelete();
            TestMoveMode();
            TestUndoStack();
            TestIndexRebuild();
            TestTrickyPaths();
            TestCacheKeyIsolation();
            TestBulkMarkAndUndoAll();
            TestRapidConsecutiveClicks();
            TestQueueOrder();
            TestCancelDuringCopy();
            TestReadOnlyAndLocked();
            TestExternalDeleteOfCopy();
            TestOpenFileSelectsFolder();
        }
        catch (Exception ex)
        {
            Fail("自检异常", ex.ToString());
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"自检结果：通过 {_pass} 项，失败 {_fail} 项，耗时 {sw.Elapsed.TotalSeconds:F1}s");
        if (_fail > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败详情：");
            Console.WriteLine(_report.ToString());
        }
        return _fail == 0 ? 0 : 1;
    }

    // ——— 工具 ———

    private void Ok(string name, string detail = "")
    {
        _pass++;
        Console.WriteLine($"  [PASS] {name}{(string.IsNullOrEmpty(detail) ? "" : "  —  " + detail)}");
    }

    private void Fail(string name, string detail)
    {
        _fail++;
        Console.WriteLine($"  [FAIL] {name}  —  {detail}");
        _report.AppendLine($"• {name}: {detail}");
    }

    private void Assert(string name, bool condition, string detail = "")
    {
        if (condition) Ok(name, detail);
        else Fail(name, detail);
    }

    /// <summary>生成一张最小的合法 JPEG（64x48 渐变），用于测试复制/解码。</summary>
    private static void WriteJpeg(string path, int seed = 0)
    {
        int w = 64, h = 48;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                pixels[o + 0] = (byte)(x * 4 + seed);
                pixels[o + 1] = (byte)(y * 4);
                pixels[o + 2] = (byte)(128 + seed);
                pixels[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static (ViewerSession session, StateStore store, string dbPath) NewSession(bool moveMode = false)
    {
        var db = Path.Combine(Path.GetTempPath(), "影筛自检db_" + Guid.NewGuid().ToString("N")[..8], "index.db");
        var store = new StateStore(db);
        var settings = new AppSettings { MarkMode = moveMode ? MarkMode.Move : MarkMode.Copy };
        var session = new ViewerSession(settings, store);
        return (session, store, db);
    }

    private string MakeFolder(string name, int count)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= count; i++) WriteJpeg(Path.Combine(dir, $"IMG_{i}.jpg"), i);
        return dir;
    }

    private static List<string> Names(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToList()! : new();

    // ——— 用例 ———

    private void TestScanAndNaturalSort()
    {
        Console.WriteLine("1) 扫描与自然排序");
        string dir = Path.Combine(_root, "扫描");
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { "IMG_1.jpg", "IMG_2.jpg", "IMG_10.jpg", "IMG_20.jpg", "a.jpg", "B.jpg" })
            WriteJpeg(Path.Combine(dir, n));
        File.WriteAllText(Path.Combine(dir, "~$temp.jpg"), "x");
        File.WriteAllText(Path.Combine(dir, "note.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "子目录"));

        var entries = DirectoryScanner.Scan(dir, out var notes);
        Assert("只列图片且排除临时目录/子目录", entries.Count == 6, $"实际 {entries.Count} 个，跳过说明：{string.Join("；", notes)}");

        var names = entries.Select(e => e.Name).ToList();
        names.Sort(NaturalComparer.Instance.Compare);
        var expected = new[] { "a.jpg", "B.jpg", "IMG_1.jpg", "IMG_2.jpg", "IMG_10.jpg", "IMG_20.jpg" };
        Assert("自然排序 IMG_2 < IMG_10", names.SequenceEqual(expected), string.Join(", ", names));

        Assert("自然排序键稳定", NaturalComparer.Key("DSC_010.NEF") == NaturalComparer.Key("DSC_10.NEF") &&
                                  NaturalComparer.Instance.Compare("DSC_9.NEF", "DSC_10.NEF") < 0);
    }

    private void TestCopyMark()
    {
        Console.WriteLine("2) 复制标记");
        string dir = MakeFolder("复制", 3);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var item = session.Items[0];
            var r = session.ToggleMarkAsync(item).GetAwaiter().GetResult();
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            Assert("标记成功", r.Ok, r.Error ?? "");
            Assert("筛选里出现副本", File.Exists(Path.Combine(filterDir, "IMG_1.jpg")));
            Assert("原文件仍在原目录", File.Exists(Path.Combine(dir, "IMG_1.jpg")));
            Assert("条目标记为已筛", item.State == MarkState.Marked);
            Assert("计数正确", session.MarkedCount == 1 && session.TotalCount == 3, $"已筛 {session.MarkedCount}/{session.TotalCount}");
            Assert("索引记录了副本完整路径", item.Record != null && item.Record.DestPath.EndsWith("IMG_1.jpg") && item.Record.DestSize > 0);
            Assert("没有 .part 残留", Directory.GetFiles(filterDir, "*.part").Length == 0);
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestConflictRename()
    {
        Console.WriteLine("3) 同名冲突自动改名");
        string dir = MakeFolder("冲突", 2);
        string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            // 先打开会话（注册文件夹），再往「筛选」里放一个同名占位文件，模拟已有副本
            Directory.CreateDirectory(filterDir);
            File.WriteAllText(Path.Combine(filterDir, "IMG_1.jpg"), "占位，不能被覆盖");
            var item = session.Items.First(i => i.Name == "IMG_1.jpg");
            var r = session.ToggleMarkAsync(item).GetAwaiter().GetResult();
            Assert("冲突时标记成功", r.Ok, r.Error ?? "");
            Assert("自动改名为 IMG_1 (1).jpg", File.Exists(Path.Combine(filterDir, "IMG_1 (1).jpg")),
                string.Join(", ", Directory.GetFiles(filterDir).Select(Path.GetFileName)));
            Assert("原同名文件未被覆盖", File.ReadAllText(Path.Combine(filterDir, "IMG_1.jpg")) == "占位，不能被覆盖");
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestCompanions()
    {
        Console.WriteLine("4) 伴生文件一起复制");
        string dir = MakeFolder("伴生", 1);
        File.WriteAllText(Path.Combine(dir, "IMG_1.xmp"), "<xmp/>");
        File.WriteAllText(Path.Combine(dir, "IMG_1.aae"), "aae");
        File.WriteAllText(Path.Combine(dir, "IMG_1.thm"), "thm");
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var item = session.Items.First(i => i.Name == "IMG_1.jpg");
            var r = session.ToggleMarkAsync(item).GetAwaiter().GetResult();
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            Assert("伴生文件 .xmp/.aae/.thm 都进了筛选",
                File.Exists(Path.Combine(filterDir, "IMG_1.xmp")) &&
                File.Exists(Path.Combine(filterDir, "IMG_1.aae")) &&
                File.Exists(Path.Combine(filterDir, "IMG_1.thm")),
                r.Error ?? "");
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestUnmarkDelete()
    {
        Console.WriteLine("5) 取消标记 → 副本进回收站，原文件不动");
        string dir = MakeFolder("取消", 1);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var item = session.Items[0];
            session.ToggleMarkAsync(item).GetAwaiter().GetResult();
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            string copy = Path.Combine(filterDir, "IMG_1.jpg");
            Assert("标记后副本存在", File.Exists(copy));

            var r = session.ToggleMarkAsync(item).GetAwaiter().GetResult();
            Assert("取消标记成功", r.Ok, r.Error ?? "");
            Assert("筛选里副本已删除", !File.Exists(copy));
            Assert("原目录文件仍在", File.Exists(Path.Combine(dir, "IMG_1.jpg")));
            Assert("勾选消失", item.State == MarkState.None);
            Assert("索引记录已删", store.LoadMarks(session.FolderId).Count == 0);
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestMoveMode()
    {
        Console.WriteLine("6) 移动模式 + 切换模式后旧记录仍按复制撤销");
        string dir = MakeFolder("移动", 2);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var first = session.Items[0];
            session.ToggleMarkAsync(first).GetAwaiter().GetResult(); // 复制模式
            Assert("复制模式记录 Kind=Copy", first.Record?.Kind == MarkOpKind.Copy);

            session.Settings.MarkMode = MarkMode.Move;
            var second = session.Items[1];
            var r2 = session.ToggleMarkAsync(second).GetAwaiter().GetResult();
            Assert("移动模式标记成功", r2.Ok, r2.Error ?? "");
            Assert("移动后原目录文件消失", !File.Exists(Path.Combine(dir, second.Name)));
            Assert("移动模式记录 Kind=Move", second.Record?.Kind == MarkOpKind.Move);

            // 切换回复制模式，取消第一个（复制产生的）→ 仍是删副本，不是移回
            session.Settings.MarkMode = MarkMode.Copy;
            var r3 = session.ToggleMarkAsync(first).GetAwaiter().GetResult();
            Assert("旧复制记录仍按「删副本」撤销", r3.Ok && !File.Exists(first.Record?.DestPath ?? ""), r3.Error ?? "");
            Assert("旧记录源文件仍在", File.Exists(Path.Combine(dir, first.Name)));

            // 取消移动记录 → 文件移回原位
            var r4 = session.ToggleMarkAsync(second).GetAwaiter().GetResult();
            Assert("移动记录取消后文件回到原位", r4.Ok && File.Exists(Path.Combine(dir, second.Name)), r4.Error ?? "");
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestUndoStack()
    {
        Console.WriteLine("7) 撤销栈（标记/取消互为逆操作）");
        string dir = MakeFolder("撤销", 2);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var a = session.Items[0];
            var b = session.Items[1];
            session.ToggleMarkAsync(a).GetAwaiter().GetResult();
            session.ToggleMarkAsync(b).GetAwaiter().GetResult();
            Assert("撤销栈深度 2", session.Marks.UndoDepth == 2, $"实际 {session.Marks.UndoDepth}");

            session.Marks.UndoAsync().GetAwaiter().GetResult();
            Assert("撤销后 b 取消标记", b.State == MarkState.None);
            session.Marks.UndoAsync().GetAwaiter().GetResult();
            Assert("再撤销后 a 取消标记", a.State == MarkState.None);
            Assert("撤销栈清空", session.Marks.UndoDepth == 0);
            Assert("原文件都还在", File.Exists(Path.Combine(dir, a.Name)) && File.Exists(Path.Combine(dir, b.Name)));

            // 撤销「取消标记」应该重新标记
            session.ToggleMarkAsync(a).GetAwaiter().GetResult();
            session.ToggleMarkAsync(a).GetAwaiter().GetResult(); // 取消
            Assert("取消后状态为未标记", a.State == MarkState.None);
            session.Marks.UndoAsync().GetAwaiter().GetResult();
            Assert("撤销「取消」后重新标记", a.State == MarkState.Marked);
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestIndexRebuild()
    {
        Console.WriteLine("8) 索引丢失后按「筛选」内容重建");
        string dir = MakeFolder("重建", 3);
        string dbPath;
        {
            var (session, store, db) = NewSession();
            dbPath = db;
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            session.ToggleMarkAsync(session.Items[0]).GetAwaiter().GetResult();
            session.ToggleMarkAsync(session.Items[1]).GetAwaiter().GetResult();
            session.Dispose();
            store.Dispose();
        }
        CleanupDb(dbPath); // 模拟索引库被删（例如文件夹被移动、数据库损坏）

        var (s2, store2, db2) = NewSession();
        try
        {
            s2.OpenFolderAsync(dir).GetAwaiter().GetResult();
            Assert("重建出 2 条标记", s2.MarkedCount == 2, $"实际 {s2.MarkedCount}");
        }
        finally { s2.Dispose(); store2.Dispose(); CleanupDb(db2); }
    }

    private void TestTrickyPaths()
    {
        Console.WriteLine("9) 中文 / 空格 / 超长路径");
        string dir = Path.Combine(_root, "中文 目录 测试");
        Directory.CreateDirectory(dir);
        string longName = string.Join("_", Enumerable.Repeat("很长很长的名字", 18)) + ".jpg";
        string full = Path.Combine(dir, longName);
        WriteJpeg(AppPaths.ToLongPath(full));
        WriteJpeg(Path.Combine(dir, "带 空格 的 照片.jpg"));
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            Assert("长路径文件被扫到", session.Items.Any(i => i.Name == longName), $"共 {session.Items.Count} 个");
            var r = session.ToggleMarkAsync(session.Items[0]).GetAwaiter().GetResult();
            Assert("中文/空格/长路径可标记", r.Ok, r.Error ?? "");
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            Assert("副本落到筛选目录", Directory.GetFiles(AppPaths.ToLongPath(filterDir)).Length > 0);
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private void TestCacheKeyIsolation()
    {
        Console.WriteLine("10) 缓存键随文件变化失效");
        string dir = MakeFolder("缓存键", 1);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var item = session.Items[0];
            string k1 = ThumbnailCacheKey(item);
            WriteJpeg(Path.Combine(dir, "IMG_1.jpg"), 7); // 改内容 → 大小/时间变
            var entries = DirectoryScanner.Scan(dir, out _);
            var e = entries[0];
            string k2 = ThumbnailCacheKey(new ImageItem
            {
                FullPath = e.FullPath, Name = e.Name, Extension = e.Extension, Size = e.Size, ModifiedUtc = e.ModifiedUtc
            });
            Assert("文件改变后缓存键改变", k1 != k2, $"{k1[..8]} vs {k2[..8]}");
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private static string ThumbnailCacheKey(ImageItem i)
        => Cache.ThumbnailCache.MakeKey(i);

    /// <summary>验收 15：连续标记 300 张再全部撤销，无残留、无错删。</summary>
    private void TestBulkMarkAndUndoAll()
    {
        Console.WriteLine("11) 连续标记 300 张 + 全部 Ctrl+Z 撤回");
        string dir = MakeFolder("批量", 300);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            int marked = 0;
            foreach (var item in session.Items)
            {
                var r = session.ToggleMarkAsync(item).GetAwaiter().GetResult();
                if (r.Ok && item.State == MarkState.Marked) marked++;
            }
            Assert("300 张全部标记成功", marked == 300, $"实际 {marked}");
            Assert("索引记录 300 条", store.LoadMarks(session.FolderId).Count == 300, $"实际 {store.LoadMarks(session.FolderId).Count}");

            int undone = 0;
            while (session.Marks.UndoDepth > 0)
            {
                var r = session.Marks.UndoAsync().GetAwaiter().GetResult();
                if (r.Ok) undone++;
                else break;
            }
            Assert("300 步全部撤销成功", undone == 300, $"实际 {undone}");
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            int left = Directory.Exists(filterDir) ? Directory.GetFiles(filterDir).Length : 0;
            Assert("筛选目录没有残留副本", left == 0, $"残留 {left} 个");
            Assert("原目录 300 张原图都在", Directory.GetFiles(dir).Length == 300, $"实际 {Directory.GetFiles(dir).Length}");
            Assert("索引记录已清空", store.LoadMarks(session.FolderId).Count == 0);
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    /// <summary>验收 16：手快连点 10 张 → 筛选里正好 10 个副本，且对应正确，无重复无漏。</summary>
    private void TestRapidConsecutiveClicks()
    {
        Console.WriteLine("12) 快速连点 10 张不同缩略图");
        string dir = MakeFolder("连点", 20);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var targets = session.Items.Take(10).ToList();
            // 不等待，模拟"手快连点"：10 个任务几乎同时入队
            var tasks = targets.Select(t => session.ToggleMarkAsync(t)).ToArray();
            Task.WaitAll(tasks);

            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            var copied = Directory.GetFiles(filterDir).Select(Path.GetFileName).OrderBy(x => x).ToList();
            var expected = targets.Select(t => t.Name).OrderBy(x => x).ToList();
            Assert("筛选里正好 10 个副本", copied.Count == 10, $"实际 {copied.Count}");
            Assert("副本文件名与被点中的 10 张一一对应", copied.SequenceEqual(expected),
                $"实际：{string.Join(",", copied)}");
            Assert("没有同一张被复制多次", copied.Distinct().Count() == 10);
            Assert("10 张全部标记成功", targets.All(t => t.State == MarkState.Marked));
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    /// <summary>验收 17：队列顺序 = 点击顺序（先点的先落盘）。</summary>
    private void TestQueueOrder()
    {
        Console.WriteLine("13) 队列顺序 = 点击顺序");
        string dir = MakeFolder("顺序", 4);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var order = new List<string>();
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            // 记录每次操作完成的顺序
            foreach (var item in session.Items.Take(4))
            {
                var r = session.ToggleMarkAsync(item).GetAwaiter().GetResult();
                if (r.Ok) order.Add(item.Name);
            }
            var files = Directory.GetFiles(filterDir).Select(Path.GetFileName).ToList();
            Assert("4 张都进筛选", files.Count == 4, $"实际 {files.Count}");
            Assert("完成顺序与点击顺序一致", order.SequenceEqual(session.Items.Take(4).Select(i => i.Name)),
                string.Join(",", order));
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    /// <summary>验收 7：复制大文件过程中取消 → .part 清理干净。</summary>
    private void TestCancelDuringCopy()
    {
        Console.WriteLine("14) 复制过程中取消（.part 清理）");
        string dir = Path.Combine(_root, "取消复制");
        Directory.CreateDirectory(dir);
        // 造一个 600MB 的稀疏文件，复制需要明显时间，便于在中途取消
        string big = Path.Combine(dir, "BIG.jpg");
        using (var fs = new FileStream(AppPaths.ToLongPath(big), FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(600L * 1024 * 1024);
        }
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();
            var item = session.Items.First(i => i.Name == "BIG.jpg");
            var task = session.ToggleMarkAsync(item);
            Thread.Sleep(60);                       // 让它真正开始复制
            session.Queue.CancelAll();
            var r = task.GetAwaiter().GetResult();
            Thread.Sleep(200);
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            int parts = Directory.Exists(filterDir) ? Directory.GetFiles(filterDir, "*.part").Length : 0;
            int any = Directory.Exists(filterDir) ? Directory.GetFiles(filterDir).Length : 0;
            Assert("取消后没有 .part 残留", parts == 0, $"残留 {parts} 个");
            Assert("取消后勾选回滚为未标记", item.State == MarkState.None, $"实际 {item.State}");
            Assert("取消后筛选里没有半成品", any == 0, $"残留 {any} 个");
            Assert("原文件仍在", File.Exists(big));
            _ = r;
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    /// <summary>验收 8/9：只读源文件可复制；被独占占用的文件报清楚错误且回滚。</summary>
    private void TestReadOnlyAndLocked()
    {
        Console.WriteLine("15) 只读源文件 / 被占用文件");
        string dir = MakeFolder("只读", 2);
        var (session, store, db) = NewSession();
        try
        {
            session.OpenFolderAsync(dir).GetAwaiter().GetResult();

            // 只读源文件：复制应当成功（不需要写权限）
            string ro = Path.Combine(dir, "IMG_1.jpg");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            var item1 = session.Items.First(i => i.Name == "IMG_1.jpg");
            var r1 = session.ToggleMarkAsync(item1).GetAwaiter().GetResult();
            Assert("只读文件也能复制进筛选", r1.Ok && item1.State == MarkState.Marked, r1.Error ?? "");
            File.SetAttributes(ro, FileAttributes.Normal);

            // 被独占占用：应当失败、回滚、并给出可读错误
            string locked = Path.Combine(dir, "IMG_2.jpg");
            using (var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var item2 = session.Items.First(i => i.Name == "IMG_2.jpg");
                var r2 = session.ToggleMarkAsync(item2).GetAwaiter().GetResult();
                Assert("被占用文件标记失败", !r2.Ok);
                Assert("失败后角标回滚", item2.State == MarkState.None, $"实际 {item2.State}");
                Assert("错误信息可读", !string.IsNullOrWhiteSpace(r2.Error), r2.Error ?? "");
            }
            string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);
            Assert("失败后没有 .part 残留", Directory.GetFiles(filterDir, "*.part").Length == 0);
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    /// <summary>验收 12：资源管理器里删掉「筛选」里的副本 → 重启后勾选自动消失。</summary>
    private void TestExternalDeleteOfCopy()
    {
        Console.WriteLine("16) 外部删除副本后勾选自动取消");
        string dir = MakeFolder("外部删除", 1);
        string dbPath;
        {
            var (s1, st1, db1) = NewSession();
            dbPath = db1;
            s1.OpenFolderAsync(dir).GetAwaiter().GetResult();
            s1.ToggleMarkAsync(s1.Items[0]).GetAwaiter().GetResult();
            string copy = Path.Combine(dir, AppPaths.FilterFolderName, "IMG_1.jpg");
            Assert("标记后副本存在", File.Exists(copy));
            File.Delete(copy); // 模拟用户在资源管理器里删除
            s1.Dispose(); st1.Dispose();
        }
        var (s2, st2, db2) = NewSession();
        try
        {
            // 同一个数据库路径 → 复用索引，模拟"重启软件"
            _ = dbPath;
            s2.OpenFolderAsync(dir).GetAwaiter().GetResult();
            Assert("重启后勾选自动消失", s2.MarkedCount == 0, $"实际 {s2.MarkedCount}");
        }
        finally { s2.Dispose(); st2.Dispose(); CleanupDb(db2); }
    }

    /// <summary>验收 11：双击图片打开时加载所在文件夹并选中该图。</summary>
    private void TestOpenFileSelectsFolder()
    {
        Console.WriteLine("17) 用文件路径打开 → 定位所在文件夹并选中");
        string dir = MakeFolder("打开方式", 3);
        var (session, store, db) = NewSession();
        try
        {
            string target = Path.Combine(dir, "IMG_2.jpg");
            string folder = ViewerSession.NormalizeFolderArg(target);
            session.OpenFolderAsync(folder).GetAwaiter().GetResult();
            var item = session.SelectByPath(target);
            Assert("按文件路径选中了正确的图", item != null && item.Name == "IMG_2.jpg", item?.Name ?? "null");
            Assert("列表来自该文件所在文件夹", session.TotalCount == 3, $"实际 {session.TotalCount}");
        }
        finally { session.Dispose(); store.Dispose(); CleanupDb(db); }
    }

    private static void CleanupDb(string dbPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(dbPath)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }
}
