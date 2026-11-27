using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using YingShai.Core.Cache;
using YingShai.Core.Imaging;
using YingShai.Core.Index;
using YingShai.Core.Jobs;
using YingShai.Core.Model;
using YingShai.Core.Ops;
using YingShai.Core.Scanning;
using YingShai.Core.Settings;

namespace YingShai.Core.Session;

/// <summary>
/// 一个「当前文件夹」的会话：扫描、排序、缩略图调度、索引一致性、标记与撤销。
/// UI 只跟这个类打交道，引擎不认识任何 WPF 控件。
/// </summary>
public sealed class ViewerSession : IDisposable
{
    public AppSettings Settings { get; }
    public StateStore Store { get; }
    public ThumbnailCache Cache { get; }
    public DecodeScheduler Scheduler { get; }
    public OperationQueue Queue { get; }
    public MarkService Marks { get; }

    public ObservableCollection<ImageItem> Items { get; } = new();

    public string FolderPath { get; private set; } = "";
    public int FolderId { get; private set; }
    public SortMode SortMode { get; private set; }
    public bool SortDescending { get; private set; }

    public ImageItem? Current { get; private set; }

    // ——— 事件 ———
    public event EventHandler? ItemsReset;
    public event EventHandler? CurrentChanged;
    public event EventHandler? CountsChanged;
    public event EventHandler<string>? StatusMessage;

    public int MarkedCount => Items.Count(i => i.State == MarkState.Marked);
    public int TotalCount => Items.Count;
    public int RemainingCount => TotalCount - MarkedCount;

    /// <summary>最近一次扫描耗时（bench 用）。</summary>
    public double LastScanMs { get; private set; }

    private readonly SemaphoreSlim _fullGate = new(1, 1);

    /// <summary>UI 线程的同步上下文（由 WPF 层设置；命令行模式下为 null，直接执行）。</summary>
    public SynchronizationContext? UiContext { get; set; }

    private Task PostAsync(Action action)
    {
        if (UiContext == null)
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UiContext.Post(_ =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    public ViewerSession(AppSettings settings, StateStore store)
    {
        Settings = settings;
        Store = store;
        Cache = new ThumbnailCache(store, settings);
        Scheduler = new DecodeScheduler(Cache, ResolveConcurrency());
        Queue = new OperationQueue();
        Marks = new MarkService(store, settings, Queue);
        Marks.UndoChanged += (_, _) => CountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>并发探测：网络盘/可移动盘 2，其余默认 6（缩略图主要是 IO+轻解码，6 比较均衡）；用户设置优先。</summary>
    private int ResolveConcurrency()
    {
        if (Settings.DecodeConcurrency > 0) return Settings.DecodeConcurrency;
        return 6;
    }

    // —————————— 打开文件夹 ——————————

    public async Task<bool> OpenFolderAsync(string folder, IProgress<string>? status = null, CancellationToken ct = default)
    {
        folder = NormalizeFolderArg(folder);
        if (!Directory.Exists(AppPaths.ToLongPath(folder)))
        {
            StatusMessage?.Invoke(this, $"目录不存在：{folder}");
            return false;
        }

        var sw = Stopwatch.StartNew();
        FolderPath = folder;
        FolderId = Store.GetOrCreateFolder(folder, out bool isNewFolder);
        Marks.SetFolder(folder, FolderId);

        status?.Report("正在枚举文件…");
        var notes = new List<string>();
        var entries = await Task.Run(() => DirectoryScanner.Scan(folder, out notes, ct), ct).ConfigureAwait(false);
        LastScanMs = sw.Elapsed.TotalMilliseconds;
        if (notes.Count > 0) StatusMessage?.Invoke(this, string.Join("，", notes));

        var items = entries.Select(e => new ImageItem
        {
            FullPath = e.FullPath,
            Name = e.Name,
            Extension = e.Extension,
            Size = e.Size,
            ModifiedUtc = e.ModifiedUtc
        }).ToList();

        status?.Report("正在读取索引…");
        var marks = Store.LoadMarks(FolderId);
        var rebuilt = false;
        // 只有「第一次见到这个文件夹路径 + 索引里没有记录」时才按「筛选」内容重建
        // （覆盖：用户在资源管理器把整个文件夹改名/移动过，旧索引对不上新路径）
        if (marks.Count == 0 && isNewFolder) rebuilt = TryRebuildFromFilterFolder(items, out marks);

        ApplyMarks(items, marks);
        await CheckConsistencyAsync(items, ct).ConfigureAwait(false);

        // 恢复保护（锁定）与星级
        var lockedSet = Store.GetLockedItems(FolderId);
        var starMap = Store.GetStars(FolderId);
        foreach (var it in items)
        {
            it.Locked = lockedSet.Contains(it.Name);
            if (starMap.TryGetValue(it.Name, out var st)) it.Stars = Math.Clamp(st, 0, 5);
        }

        SortItems(items);
        for (int i = 0; i < items.Count; i++) items[i].Index = i;

        await PostAsync(() =>
        {
            Items.Clear();
            foreach (var it in items) Items.Add(it);
        }).ConfigureAwait(false);
        ItemsReset?.Invoke(this, EventArgs.Empty);
        CountsChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage?.Invoke(this, rebuilt
            ? $"已打开：{Path.GetFileName(folder)}（索引丢失，已按「筛选」内容重建 {marks.Count} 条标记）"
            : $"已打开：{Path.GetFileName(folder)}，共 {items.Count} 张，已筛 {MarkedCount} 张");
        Util.AppLog.Info($"打开文件夹：{folder}，{items.Count} 张，枚举 {LastScanMs:F0}ms，已筛 {MarkedCount}");
        return true;
    }

    /// <summary>把命令行/拖拽进来的参数统一成文件夹路径（可以是文件）。</summary>
    public static string NormalizeFolderArg(string arg)
    {
        arg = arg.Trim().Trim('"');
        try
        {
            if (File.Exists(AppPaths.ToLongPath(arg))) return Path.GetDirectoryName(Path.GetFullPath(arg))!;
            if (Directory.Exists(AppPaths.ToLongPath(arg))) return Path.GetFullPath(arg);
        }
        catch { }
        return arg;
    }

    /// <summary>定位到某个文件（用于「打开方式」/拖拽单文件）。</summary>
    public ImageItem? SelectByPath(string path)
    {
        string full = Path.GetFullPath(path);
        var item = Items.FirstOrDefault(i => string.Equals(i.FullPath, full, StringComparison.OrdinalIgnoreCase))
                   ?? Items.FirstOrDefault(i => string.Equals(i.Name, Path.GetFileName(full), StringComparison.OrdinalIgnoreCase));
        if (item != null) SetCurrent(item);
        return item;
    }

    // —————————— 排序 ——————————

    public void SetSort(SortMode mode, bool descending, bool applyNow = true)
    {
        SortMode = mode;
        SortDescending = descending;
        Settings.SortMode = mode;
        Settings.SortDescending = descending;
        if (applyNow) SortItems(Items.ToList());
    }

    private void SortItems(List<ImageItem> items)
    {
        Comparison<ImageItem> cmp = SortMode switch
        {
            SortMode.Name => (a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name),
            SortMode.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortMode.ModifiedTime => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            SortMode.TakenTime => (a, b) => (a.Metadata?.TakenAt ?? DateTime.MinValue).CompareTo(b.Metadata?.TakenAt ?? DateTime.MinValue),
            _ => (a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name)
        };
        items.Sort(cmp);
        if (SortDescending) items.Reverse();
    }

    /// <summary>按拍摄时间排序前，先用「只 open 不解码」的廉价方式补齐时间（2000 张约 2~4 秒，后台线程）。</summary>
    public async Task EnsureTakenTimesAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var missing = Items.Where(i => i.Metadata?.TakenAt == null).ToList();
        if (missing.Count == 0) return;
        int done = 0;
        await Task.Run(() =>
        {
            foreach (var item in missing)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (LibRawImage.IsRawExtension(item.Extension) && LibRawImage.TryOpen(item.FullPath, out var img, out _) && img != null)
                    {
                        using (img) item.Metadata = img.ReadMetadata();
                    }
                    else
                    {
                        var dec = WicImage.DecodeFileScaled(item.FullPath, ImageDecoder.StripSize);
                        item.Metadata = new PhotoMetadata
                        {
                            TakenAt = ExifReader.ParseExifDate(dec.Exif.DateTimeOriginal),
                            PixelWidth = dec.SourceWidth, PixelHeight = dec.SourceHeight,
                            Model = dec.Exif.Model, Lens = dec.Exif.LensModel
                        };
                    }
                }
                catch { }
                int d = Interlocked.Increment(ref done);
                if (d % 10 == 0) progress?.Report(d);
            }
        }, ct).ConfigureAwait(false);

        var sorted = Items.ToList();
        SortItems(sorted);
        await PostAsync(() =>
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Index = i;
                int oldIndex = Items.IndexOf(sorted[i]);
                if (oldIndex != i) Items.Move(oldIndex, i);
            }
        }).ConfigureAwait(false);
    }

    // —————————— 索引一致性 ——————————

    private static void ApplyMarks(List<ImageItem> items, List<MarkRecord> marks)
    {
        var byName = items.ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase);
        foreach (var m in marks)
        {
            if (byName.TryGetValue(m.ItemName, out var item))
            {
                item.Record = m;
                item.State = MarkState.Marked;
            }
        }
    }

    /// <summary>
    /// 启动/切换文件夹/刷新时的核对：
    ///  - 用户在资源管理器删了「筛选」里的副本 → 勾选自动取消；
    ///  - 副本被改名/整个文件夹被移动 → 按文件名在「筛选」里重新匹配，别让用户丢状态；
    ///  - 原文件被删除 → 标记「源文件已不存在」，副本不删。
    /// </summary>
    private async Task CheckConsistencyAsync(List<ImageItem> items, CancellationToken ct)
    {
        var filterDir = Path.Combine(FolderPath, AppPaths.FilterFolderName);
        var filterFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(AppPaths.ToLongPath(filterDir)))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(AppPaths.ToLongPath(filterDir)))
                    filterFiles[Path.GetFileName(f)] = f;
            }
            catch { }
        }

        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var rec = item.Record;
                if (rec != null)
                {
                    if (!File.Exists(AppPaths.ToLongPath(rec.DestPath)))
                    {
                        // 副本没了：先按文件名在「筛选」里找（用户可能改过名/搬过目录）
                        string name = Path.GetFileName(rec.DestPath);
                        if (filterFiles.TryGetValue(name, out var found))
                        {
                            rec.DestPath = found;
                            var fi = new FileInfo(found);
                            rec.DestSize = fi.Length;
                            rec.DestMtimeTicks = fi.LastWriteTimeUtc.Ticks;
                            Store.UpdateMarkDestination(FolderId, rec.ItemName, rec.DestPath, rec.DestSize, rec.DestMtimeTicks);
                        }
                        else
                        {
                            Store.DeleteMark(FolderId, rec.ItemName);
                            item.Record = null;
                            item.State = MarkState.None;
                            continue;
                        }
                    }
                    if (!File.Exists(AppPaths.ToLongPath(rec.SourcePath)))
                        item.SourceMissing = true;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>索引库丢了（文件夹被移动/重建），但「筛选」里还有文件：按文件名重建标记状态。</summary>
    private bool TryRebuildFromFilterFolder(List<ImageItem> items, out List<MarkRecord> marks)
    {
        marks = new List<MarkRecord>();
        var filterDir = Path.Combine(FolderPath, AppPaths.FilterFolderName);
        if (!Directory.Exists(AppPaths.ToLongPath(filterDir))) return false;
        var byName = items.ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.ToLongPath(filterDir)))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (!byName.TryGetValue(name, out var item)) continue;
                var fi = new FileInfo(f);
                var rec = new MarkRecord
                {
                    FolderId = FolderId,
                    ItemName = name,
                    SourcePath = item.FullPath,
                    DestPath = f,
                    Kind = MarkOpKind.Copy,
                    DestSize = fi.Length,
                    DestMtimeTicks = fi.LastWriteTimeUtc.Ticks
                };
                Store.UpsertMark(rec);
                marks.Add(rec);
                count++;
            }
        }
        catch { }
        return count > 0;
    }

    /// <summary>手动刷新（重新枚举 + 重新核对）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        string? currentPath = Current?.FullPath;
        await OpenFolderAsync(FolderPath, null, ct).ConfigureAwait(false);
        if (currentPath != null)
        {
            var item = Items.FirstOrDefault(i => string.Equals(i.FullPath, currentPath, StringComparison.OrdinalIgnoreCase));
            if (item != null) SetCurrent(item);
        }
    }

    // —————————— 当前项 / 缩略图 ——————————

    public void SetCurrent(ImageItem? item)
    {
        Current = item;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        ReleaseFarThumbs();
    }

    /// <summary>离开当前张 ±200 的缩略图释放掉（缓存里还有，滚回来秒出）。</summary>
    public void ReleaseFarThumbs(int radius = 200)
    {
        if (Current == null) return;
        int cur = Current.Index;
        foreach (var item in Items)
        {
            if (item.Thumb == null) continue;
            if (Math.Abs(item.Index - cur) > radius) item.ClearThumb();
        }
        Cache.TrimMemory();
    }

    /// <summary>优先请求可见区条带缩略图，并对前后各 30 张预取；其余全量预热。</summary>
    public void RequestThumb(ImageItem item, int priority)
    {
        if (item.Thumb != null) return;
        _ = Scheduler.GetAsync(item, ImageDecoder.StripSize, priority).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result != null) item.SetThumb(t.Result);
        }, TaskScheduler.Default);
    }

    public void Reprioritize(ImageItem item, int priority)
        => Scheduler.Reprioritize(item, ImageDecoder.StripSize, priority);

    /// <summary>直接等待一张条带缩略图（bench / 预取用）。</summary>
    public Task<BitmapSource?> GetThumbAsync(ImageItem item, int priority, CancellationToken ct = default)
        => Scheduler.GetAsync(item, ImageDecoder.StripSize, priority, ct);

    /// <summary>大图预览（1024px），优先级最高。</summary>
    public Task<BitmapSource?> GetPreviewAsync(ImageItem item, CancellationToken ct)
        => Scheduler.GetAsync(item, ImageDecoder.PreviewSize, DecodePriority.CurrentLarge, ct);

    /// <summary>
    /// 100% 查看：后台做一次完整 demosaic（45MP 约 1~3 秒），同一时刻只允许一个，期间界面可操作。
    /// </summary>
    public async Task<BitmapSource?> GetFullResAsync(ImageItem item, CancellationToken ct)
    {
        await _fullGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var outcome = ImageDecoder.DecodeFull(item.FullPath);
                if (outcome.Ok)
                {
                    item.Metadata ??= outcome.Metadata;
                    return outcome.Bitmap;
                }
                return null;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _fullGate.Release();
        }
    }

    /// <summary>复制任务在进行时把解码并发降到 1，避免跟文件复制抢 IO。</summary>
    public void OnQueueActivity(bool busy)
    {
        if (busy) Scheduler.SetConcurrency(1);
        else Scheduler.SetConcurrency(ResolveConcurrency());
    }

    // —————————— 标记 ——————————

    public async Task<OpResult> ToggleMarkAsync(ImageItem item)
    {
        OpResult r = item.State == MarkState.Marked
            ? await Marks.UnmarkAsync(item).ConfigureAwait(false)
            : await Marks.MarkAsync(item).ConfigureAwait(false);
        CountsChanged?.Invoke(this, EventArgs.Empty);
        return r;
    }

    // ——— 保护（锁定）与星级 ———

    /// <summary>切换当前文件夹里某张图的保护（锁定）状态，立即写库。受保护的图不会被取消筛选。</summary>
    public bool ToggleLock(ImageItem item)
    {
        item.Locked = !item.Locked;
        Store.SetLock(FolderId, item.Name, item.Locked);
        YingShai.Core.Util.AppLog.Info($"{(item.Locked ? "保护" : "解除保护")}：{item.Name}");
        return item.Locked;
    }

    /// <summary>设置星级（1~5；与当前相同则清零），立即写库。</summary>
    public void SetStars(ImageItem item, int stars)
    {
        if (item.Stars == stars) stars = 0;
        stars = Math.Clamp(stars, 0, 5);
        item.Stars = stars;
        Store.SetStar(FolderId, item.Name, stars);
        YingShai.Core.Util.AppLog.Info($"星级：{item.Name} = {stars} 星");
    }

    public void Dispose()
    {
        try { Queue.Dispose(); } catch { }
        try { Scheduler.Dispose(); } catch { }
        try { Cache.Dispose(); } catch { }
    }
}
