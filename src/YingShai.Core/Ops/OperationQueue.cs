using System.Collections.Concurrent;

namespace YingShai.Core.Ops;

public sealed class OpProgress
{
    public string Message { get; init; } = "";
    public long Done { get; init; }
    public long Total { get; init; }
    public int QueueDepth { get; init; }
}

public sealed class OpResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    /// <summary>成功但有需要提示的情况（例如跨卷移动退化成复制+删除）。</summary>
    public string? Warning { get; init; }
    public object? Payload { get; init; }

    public static OpResult Success(object? payload = null) => new() { Ok = true, Payload = payload };
    public static OpResult Fail(string error) => new() { Ok = false, Error = error };
    public static OpResult Cancelled() => new() { Ok = false, Error = "已取消" };
}

/// <summary>
/// 单写线程队列（并发 1）：复制/移动/删除全部串行执行，绝不阻塞预览与切图。
/// 每个任务独立成功或失败；可取消单个任务，也可全部取消（复制中取消会清理 .part）。
/// </summary>
public sealed class OperationQueue : IDisposable
{
    private sealed class WorkItem
    {
        public required string Title { get; init; }
        public required Func<CancellationToken, IProgress<OpProgress>, Task<OpResult>> Body { get; init; }
        public required object Tag { get; init; }
        public TaskCompletionSource<OpResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly LinkedList<WorkItem> _pending = new();
    private readonly object _lock = new();
    private readonly Thread _worker;
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _disposed;
    private WorkItem? _running;

    /// <summary>队列变化（新增/开始/完成），UI 据此刷新状态区。</summary>
    public event EventHandler? Changed;
    /// <summary>运行中的任务进度。</summary>
    public event EventHandler<OpProgress>? Progress;

    public OperationQueue()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "影筛文件操作",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    public bool IsBusy
    {
        get { lock (_lock) return _running != null || _pending.Count > 0; }
    }

    public int PendingCount
    {
        get { lock (_lock) return _pending.Count + (_running != null ? 1 : 0); }
    }

    public string? CurrentTitle
    {
        get { lock (_lock) return _running?.Title; }
    }

    /// <summary>入队一个操作，返回其完成结果。Tag 用于按对象取消（同一张图只允许一个进行中的标记任务）。</summary>
    public Task<OpResult> EnqueueAsync(string title, object tag,
        Func<CancellationToken, IProgress<OpProgress>, Task<OpResult>> body)
    {
        var item = new WorkItem { Title = title, Body = body, Tag = tag };
        lock (_lock)
        {
            _pending.AddLast(item);
        }
        _signal.Release();
        Changed?.Invoke(this, EventArgs.Empty);
        return item.Tcs.Task;
    }

    /// <summary>取消某个 Tag 对应的待办任务（如果它正在执行，则请求取消）。</summary>
    public bool CancelFor(object tag)
    {
        WorkItem? toCancel = null;
        lock (_lock)
        {
            var node = _pending.First;
            while (node != null)
            {
                if (ReferenceEquals(node.Value.Tag, tag))
                {
                    toCancel = node.Value;
                    _pending.Remove(node);
                    break;
                }
                node = node.Next;
            }
            if (toCancel == null && _running != null && ReferenceEquals(_running.Tag, tag)) toCancel = _running;
        }
        if (toCancel == null) return false;
        try { toCancel.Cts.Cancel(); } catch { }
        toCancel.Tcs.TrySetResult(OpResult.Cancelled());
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CancelAll()
    {
        var list = new List<WorkItem>();
        lock (_lock)
        {
            list.AddRange(_pending);
            _pending.Clear();
            if (_running != null) list.Add(_running);
        }
        foreach (var w in list)
        {
            try { w.Cts.Cancel(); } catch { }
            w.Tcs.TrySetResult(OpResult.Cancelled());
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            WorkItem? work = null;
            lock (_lock)
            {
                if (_pending.Count > 0)
                {
                    work = _pending.First!.Value;
                    _pending.RemoveFirst();
                    _running = work;
                }
            }
            if (work == null)
            {
                try { _signal.Wait(100); } catch { }
                continue;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            var progress = new ProgressReporter(this, work);
            OpResult result;
            try
            {
                if (work.Cts.IsCancellationRequested) result = OpResult.Cancelled();
                else result = work.Body(work.Cts.Token, progress).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { result = OpResult.Cancelled(); }
            catch (Exception ex) { result = OpResult.Fail(ex.Message); }

            lock (_lock) _running = null;
            work.Tcs.TrySetResult(result);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ProgressReporter : IProgress<OpProgress>
    {
        private readonly OperationQueue _owner;
        private readonly WorkItem _work;
        public ProgressReporter(OperationQueue owner, WorkItem work) { _owner = owner; _work = work; }
        public void Report(OpProgress value)
        {
            int depth;
            lock (_owner._lock) depth = _owner._pending.Count;
            _owner.Progress?.Invoke(_owner, new OpProgress
            {
                Message = value.Message, Done = value.Done, Total = value.Total, QueueDepth = depth
            });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelAll();
        _signal.Release();
        try { _worker.Join(500); } catch { }
    }
}
