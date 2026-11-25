using System.Windows.Media.Imaging;
using YingShai.Core.Cache;
using YingShai.Core.Imaging;
using YingShai.Core.Model;

namespace YingShai.Core.Jobs;

/// <summary>解码优先级：数字越小越先做。</summary>
public static class DecodePriority
{
    public const int CurrentLarge = 0;   // 当前大图
    public const int VisibleStrip = 1;   // 可见条带
    public const int Prefetch = 2;       // 前后各 30 张
    public const int Warmup = 3;         // 剩余全量预热
}

/// <summary>
/// 解码调度：优先级队列 + 固定线程池 + 同键去重 + 动态并发。
///  - 优先级：当前大图 &gt; 可见条带 &gt; 前后预取 &gt; 全量预热
///  - 去重：同一 (文件,尺寸) 同时只有一个解码任务
///  - 并发：SSD 4 / 机械 2 / 网络 2，复制任务运行时可降到 1
///  - 可中断：队列里的任务可被取消（正在跑的一次解码会自然完成）
/// </summary>
public sealed class DecodeScheduler : IDisposable
{
    private sealed class Job
    {
        public required string RowKey { get; init; }
        public required ImageItem Item { get; init; }
        public required int Size { get; init; }
        public required int CacheSize { get; init; }
        public int Priority;
        public long Seq;
        public TaskCompletionSource<BitmapSource?> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Ct;
    }

    private readonly ThumbnailCache _cache;
    private readonly object _lock = new();
    private readonly PriorityQueue<Job, (int Priority, long Seq)> _queue = new();
    private readonly Dictionary<string, Job> _latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<Thread> _threads = new();
    private readonly Dictionary<Thread, int> _threadIndex = new();
    private long _seq;
    private int _workerCount = 4;
    private volatile bool _disposed;
    private volatile bool _paused;

    // 统计（bench 用）
    public long DecodeCount { get; private set; }
    public long DecodeMsTotal { get; private set; }
    public long CacheHits { get; private set; }
    public long SlowPathCount { get; private set; }
    public int ActiveCount { get { lock (_lock) return _active; } }
    private int _active;

    public event EventHandler? QueueChanged;

    public DecodeScheduler(ThumbnailCache cache, int workerCount = 4)
    {
        _cache = cache;
        SetConcurrency(workerCount);
    }

    public int Concurrency
    {
        get { lock (_lock) return _workerCount; }
    }

    /// <summary>调整并发。缩小时空闲线程会自行退出，正在跑的任务会跑完。</summary>
    public void SetConcurrency(int n)
    {
        n = Math.Clamp(n, 1, 8);
        lock (_lock) _workerCount = n;
        lock (_threads)
        {
            while (_threads.Count < n)
            {
                int idx = _threads.Count;
                var t = new Thread(() => WorkerLoop(idx))
                {
                    IsBackground = true,
                    Name = $"影筛解码{idx}",
                    Priority = ThreadPriority.BelowNormal
                };
                _threadIndex[t] = idx;
                _threads.Add(t);
                t.Start();
            }
        }
    }

    public bool Paused
    {
        get => _paused;
        set { _paused = value; Pulse(); }
    }

    private void Pulse()
    {
        for (int i = 0; i < 16; i++) _signal.Release();
    }

    public int PendingCount { get { lock (_lock) return _queue.Count; } }

    /// <summary>
    /// 请求一张缩略图/预览。命中内存直接返回；否则入队，返回可等待的任务。
    /// </summary>
    public Task<BitmapSource?> GetAsync(ImageItem item, int size, int priority, CancellationToken ct = default)
    {
        string key = ThumbnailCache.MakeKey(item);
        int cacheSize = NormalizeSize(size);
        string rowKey = $"{key}:{cacheSize}";

        if (_cache.TryGetMemory(rowKey, out var cached) && cached != null)
        {
            Interlocked.Increment(ref _cacheHits2);
            return Task.FromResult<BitmapSource?>(cached);
        }

        Job job;
        lock (_lock)
        {
            if (_latest.TryGetValue(rowKey, out var existing) && !existing.Tcs.Task.IsCompleted)
            {
                if (existing.Priority <= priority)
                {
                    // 已有同等或更高优先级的任务，直接复用
                    return existing.Tcs.Task;
                }
                // 新请求优先级更高：插一条新任务，旧任务出队时会被跳过
                existing.Priority = priority;
                existing.Seq = Interlocked.Increment(ref _seq);
                _queue.Enqueue(existing, (priority, existing.Seq));
                Pulse();
                return existing.Tcs.Task;
            }

            job = new Job
            {
                RowKey = rowKey,
                Item = item,
                Size = size,
                CacheSize = cacheSize,
                Priority = priority,
                Seq = Interlocked.Increment(ref _seq),
                Ct = ct
            };
            _latest[rowKey] = job;
            _queue.Enqueue(job, (priority, job.Seq));
        }
        Pulse();
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return job.Tcs.Task;
    }

    private long _cacheHits2;

    /// <summary>把已在队列里的任务改优先级（可见性变化时立即重排）。不存在则忽略。</summary>
    public void Reprioritize(ImageItem item, int size, int newPriority)
    {
        string rowKey = $"{ThumbnailCache.MakeKey(item)}:{NormalizeSize(size)}";
        lock (_lock)
        {
            if (!_latest.TryGetValue(rowKey, out var job) || job.Tcs.Task.IsCompleted) return;
            if (job.Priority <= newPriority) return;
            job.Priority = newPriority;
            job.Seq = Interlocked.Increment(ref _seq);
            _queue.Enqueue(job, (newPriority, job.Seq));
        }
        Pulse();
    }

    /// <summary>取消某张图的所有待办任务（不取消正在执行的解码）。</summary>
    public void Forget(ImageItem item)
    {
        string key = ThumbnailCache.MakeKey(item);
        lock (_lock)
        {
            foreach (int size in new[] { ImageDecoder.StripSize, ImageDecoder.PreviewSize })
                _latest.Remove($"{key}:{size}");
        }
    }

    private static int NormalizeSize(int size) => size <= 256 ? ImageDecoder.StripSize : ImageDecoder.PreviewSize;

    private void WorkerLoop(int index)
    {
        while (!_disposed)
        {
            try
            {
                if (_paused) { Thread.Sleep(50); continue; }

                int target;
                lock (_lock) target = _workerCount;
                if (index >= target)
                {
                    // 并发被调小：让位后退出（等到需要时再被唤醒由 SetConcurrency 重新拉起）
                    Thread.Sleep(60);
                    if (_disposed) return;
                    lock (_lock) target = _workerCount;
                    if (index >= target) return;
                    continue;
                }

                if (!_signal.Wait(80)) continue;

                Job? job = null;
                lock (_lock)
                {
                    while (_queue.TryDequeue(out var candidate, out _))
                    {
                        // 只执行「最新登记」的那条，其余是过期副本
                        if (_latest.TryGetValue(candidate.RowKey, out var latest) && ReferenceEquals(latest, candidate))
                        {
                            job = candidate;
                            break;
                        }
                    }
                    if (job != null) { _latest.Remove(job.RowKey); _active++; }
                }
                if (job == null) continue;

                try
                {
                    Execute(job);
                }
                finally
                {
                    lock (_lock) _active--;
                    QueueChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (ThreadInterruptedException) { return; }
            catch { /* 解码线程不能死 */ }
        }
    }

    private void Execute(Job job)
    {
        if (job.Ct.IsCancellationRequested)
        {
            job.Tcs.TrySetResult(null);
            return;
        }

        // 内存
        if (_cache.TryGetMemory(job.RowKey, out var mem) && mem != null)
        {
            Interlocked.Increment(ref _cacheHits2);
            job.Tcs.TrySetResult(mem);
            return;
        }

        // 磁盘
        string key = ThumbnailCache.MakeKey(job.Item);
        var disk = _cache.TryGet(key, job.CacheSize);
        if (disk != null)
        {
            Interlocked.Increment(ref _cacheHits2);
            job.Tcs.TrySetResult(disk);
            return;
        }

        // 源文件解码
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = ImageDecoder.DecodePreview(job.Item.FullPath, job.CacheSize);
        sw.Stop();
        DecodeCount++;
        DecodeMsTotal += sw.ElapsedMilliseconds;
        if (outcome.SlowPath) SlowPathCount++;
        lock (_decodeTimes)
        {
            _decodeTimes.Add(sw.Elapsed.TotalMilliseconds);
            if (_decodeTimes.Count > 5000) _decodeTimes.RemoveAt(0);
        }

        if (!outcome.Ok || outcome.Bitmap == null)
        {
            job.Tcs.TrySetResult(null);
            return;
        }

        _cache.PutMemory(job.RowKey, outcome.Bitmap);
        try { _cache.PutDisk(key, job.CacheSize, outcome.Bitmap); } catch { }
        job.Tcs.TrySetResult(outcome.Bitmap);
    }

    public long CacheHitCount => _cacheHits2;

    private readonly List<double> _decodeTimes = new();

    /// <summary>最近若干次真实解码耗时（毫秒），bench 用来算 P50/P95。</summary>
    public double[] RecentDecodeMs
    {
        get { lock (_decodeTimes) return _decodeTimes.ToArray(); }
    }

    public void Dispose()
    {
        _disposed = true;
        Pulse();
        lock (_threads)
        {
            foreach (var t in _threads) t.Join(200);
            _threads.Clear();
        }
        lock (_lock)
        {
            while (_queue.TryDequeue(out var job, out _)) job.Tcs.TrySetResult(null);
            _latest.Clear();
        }
    }
}
