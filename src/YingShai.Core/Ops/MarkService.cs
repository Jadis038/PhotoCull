using YingShai.Core.Index;
using YingShai.Core.Model;
using YingShai.Core.Settings;

namespace YingShai.Core.Ops;

/// <summary>
/// 标记 / 取消标记 的业务逻辑。严格按规格：
///  - 单张即时执行，一次一个独立入队；任何一张失败只回滚它自己；
///  - 复制流程：校验源 → 校验空间 → 建「筛选」→ 复制成 .part → 原子改名 → 冲突改名不覆盖 → 伴生文件 → 写索引；
///  - 取消标记：复制模式的副本进回收站（原文件绝不动）；移动模式的副本移回原位（冲突改名）；
///    每条索引记录各自记住 op 类型，切换设置不影响旧记录的撤销方式；
///  - Ctrl+Z 撤销栈：标记↔取消 互为逆操作。
/// </summary>
public sealed class MarkService
{
    private readonly StateStore _store;
    private readonly AppSettings _settings;
    private readonly OperationQueue _queue;
    private readonly object _undoLock = new();
    private readonly List<UndoEntry> _undo = new();
    private volatile bool _inUndo;

    private const long SpaceMargin = 64L * 1024 * 1024; // 目标盘至少多留 64MB

    public MarkService(StateStore store, AppSettings settings, OperationQueue queue)
    {
        _store = store;
        _settings = settings;
        _queue = queue;
    }

    public int FolderId { get; private set; }
    public string FolderPath { get; private set; } = "";

    public void SetFolder(string folderPath, int folderId)
    {
        FolderPath = folderPath;
        FolderId = folderId;
    }

    public OperationQueue Queue => _queue;

    // —————————— 撤销栈 ——————————

    public sealed record UndoEntry(string Description, Func<Task<OpResult>> Action);

    public event EventHandler? UndoChanged;

    public int UndoDepth { get { lock (_undoLock) return _undo.Count; } }

    public string? UndoDescription
    {
        get { lock (_undoLock) return _undo.Count == 0 ? null : _undo[^1].Description; }
    }

    private void PushUndo(UndoEntry entry)
    {
        if (_inUndo) return; // 撤销动作本身不再产生新的撤销记录
        lock (_undoLock)
        {
            _undo.Add(entry);
            if (_undo.Count > 2000) _undo.RemoveAt(0); // 保留最近 2000 步（规格要求 ≥50；验收要求连续标记 300 张后能全部撤回）
        }
        UndoChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearUndo()
    {
        lock (_undoLock) _undo.Clear();
        UndoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>撤销上一步（复制/取消/移动都算）。失败时把记录放回栈顶，保持可重试。</summary>
    public async Task<OpResult> UndoAsync()
    {
        UndoEntry? entry;
        lock (_undoLock)
        {
            if (_undo.Count == 0) return OpResult.Fail("没有可撤销的操作");
            entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
        }
        UndoChanged?.Invoke(this, EventArgs.Empty);
        OpResult result;
        _inUndo = true;
        try { result = await entry.Action().ConfigureAwait(false); }
        finally { _inUndo = false; }
        if (!result.Ok)
        {
            lock (_undoLock) _undo.Add(entry);
            UndoChanged?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    // —————————— 标记 ——————————

    /// <summary>把一张图移入「筛选」。使用调用瞬间的设置（复制/移动），旧记录不受影响。</summary>
    public Task<OpResult> MarkAsync(ImageItem item)
    {
        if (item.State is MarkState.Queued or MarkState.Working) return Task.FromResult(OpResult.Fail("该图正在处理"));
        if (item.State == MarkState.Marked) return Task.FromResult(OpResult.Fail("已经标记过了"));

        var mode = _settings.MarkMode == MarkMode.Move ? MarkOpKind.Move : MarkOpKind.Copy;
        return RunMark(item, mode, () => PushUndo(new UndoEntry($"标记「{item.Name}」", () => UnmarkAsync(item))));
    }

    private Task<OpResult> RunMark(ImageItem item, MarkOpKind mode, Action onSuccess)
    {
        item.State = MarkState.Queued;
        item.Status = null;
        return _queue.EnqueueAsync($"移入筛选：{item.Name}", item, (ct, progress) =>
        {
            item.State = MarkState.Working;
            return DoMarkAsync(item, mode, ct, progress);
        }).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : OpResult.Fail(t.Exception?.GetBaseException().Message ?? "操作异常");
            if (r.Ok)
            {
                onSuccess();
                Util.AppLog.Info($"移入筛选：{item.Name} -> {item.Record?.DestPath}（{mode}）");
            }
            else
            {
                item.State = MarkState.None;
                item.Status = r.Error;
                Util.AppLog.Warn($"移入筛选失败：{item.Name} | {r.Error}");
            }
            return r;
        }, TaskScheduler.Default);
    }

    /// <summary>取消标记：删掉「筛选」里的副本（回收站），或把移动过的副本移回原位。</summary>
    public Task<OpResult> UnmarkAsync(ImageItem item)
    {
        if (item.Record == null) return Task.FromResult(OpResult.Fail("这张图没有可取消的标记"));
        if (item.Locked)
            return Task.FromResult(OpResult.Fail("该图已被保护锁定（按 S 解锁后再取消）"));

        // 若还有排队/进行中的标记任务，先取消（正在复制的任务会自行清理 .part）
        _queue.CancelFor(item);

        var record = item.Record;
        item.State = MarkState.Queued;
        item.Status = null;
        return _queue.EnqueueAsync($"取消筛选：{item.Name}", item, (ct, progress) =>
        {
            item.State = MarkState.Working;
            return DoUnmarkAsync(item, record, ct, progress);
        }).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : OpResult.Fail(t.Exception?.GetBaseException().Message ?? "操作异常");
            if (r.Ok)
            {
                PushUndo(new UndoEntry($"取消「{item.Name}」", () => RemMarkAsync(item, record)));
                Util.AppLog.Info($"取消筛选：{item.Name}（{record.Kind}）");
            }
            else
            {
                item.State = MarkState.Marked;
                item.Status = r.Error;
                Util.AppLog.Warn($"取消筛选失败：{item.Name} | {r.Error}");
            }
            return r;
        }, TaskScheduler.Default);
    }

    /// <summary>撤销「取消标记」：按原记录的操作类型重新标记。</summary>
    private Task<OpResult> RemMarkAsync(ImageItem item, MarkRecord oldRecord)
    {
        item.State = MarkState.Queued;
        item.Status = null;
        return _queue.EnqueueAsync($"重新标记：{item.Name}", item,
            (ct, progress) => DoMarkAsync(item, oldRecord.Kind, ct, progress)).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : OpResult.Fail("操作异常");
            if (r.Ok) PushUndo(new UndoEntry($"取消「{item.Name}」", () => UnmarkAsync(item)));
            else { item.State = MarkState.None; item.Status = r.Error; }
            return r;
        }, TaskScheduler.Default);
    }

    // —————————— 核心实现（运行在单写线程上） ——————————

    private async Task<OpResult> DoMarkAsync(ImageItem item, MarkOpKind kind, CancellationToken ct, IProgress<OpProgress> progress)
    {
        string src = item.FullPath;
        string dir = Path.GetDirectoryName(src)!;
        string filterDir = Path.Combine(dir, AppPaths.FilterFolderName);

        // 失败回滚需要的东西
        var created = new List<string>();       // 我们在「筛选」里新建的文件（失败要删掉）
        var movedBack = new List<(string From, string To)>(); // 移动模式已移动的文件（失败要移回）
        try
        {
            progress.Report(new OpProgress { Message = $"准备 {item.Name}" });

            // 1. 校验源文件存在且可读（只读源文件也能复制，不需要写权限）
            if (!FileOps.Exists(src)) return OpResult.Fail("源文件已不存在");
            var readError = TryCheckReadable(src);
            if (readError != null) return OpResult.Fail(readError);

            // 2. 目标盘空间校验（不足直接失败，不要复制一半）
            var companions = FileOps.FindCompanions(src);
            long totalBytes = item.Size + companions.Sum(CompanionSize);
            long free = FileOps.GetFreeBytes(dir);
            if (free >= 0 && free < totalBytes + SpaceMargin)
                return OpResult.Fail($"目标磁盘空间不足：需要约 {Fmt(totalBytes + SpaceMargin)}，可用 {Fmt(free)}");

            // 3. 首次标记时才创建「筛选」
            try { Directory.CreateDirectory(AppPaths.ToLongPath(filterDir)); }
            catch (Exception ex) { return OpResult.Fail($"无法创建「筛选」目录：{ex.Message}"); }

            var record = new MarkRecord
            {
                FolderId = FolderId,
                ItemName = item.Name,
                SourcePath = src,
                DestPath = "",
                Kind = kind,
            };

            string? warning = null;
            long done = 0;

            if (kind == MarkOpKind.Copy)
            {
                var main = await CopyOneAsync(src, filterDir, item.Size, done, totalBytes, created, ct, progress, "复制").ConfigureAwait(false);
                if (!main.Ok) return main;
                record.DestPath = (string)main.Payload!;
                done += item.Size;

                foreach (var comp in companions)
                {
                    ct.ThrowIfCancellationRequested();
                    long size = CompanionSize(comp);
                    var cr = await CopyOneAsync(comp, filterDir, size, done, totalBytes, created, ct, progress, "复制伴生文件").ConfigureAwait(false);
                    if (!cr.Ok) return cr;
                    record.Companions.Add(new CompanionCopy { SourcePath = comp, DestPath = (string)cr.Payload! });
                    done += size;
                }
            }
            else
            {
                var main = await MoveOneAsync(src, filterDir, item.Size, done, totalBytes, ct, progress).ConfigureAwait(false);
                if (!main.Ok) return main;
                record.DestPath = (string)main.Payload!;
                movedBack.Add((record.DestPath, src));
                if (main.Warning != null) warning = main.Warning;
                done += item.Size;

                foreach (var comp in companions)
                {
                    ct.ThrowIfCancellationRequested();
                    long size = CompanionSize(comp);
                    var cr = await MoveOneAsync(comp, filterDir, size, done, totalBytes, ct, progress).ConfigureAwait(false);
                    if (!cr.Ok) return cr;
                    record.Companions.Add(new CompanionCopy { SourcePath = comp, DestPath = (string)cr.Payload! });
                    movedBack.Add(((string)cr.Payload!, comp));
                    done += size;
                }
            }

            // 5. 写索引（副本完整路径 + 大小 + 修改时间，供一致性核对）
            var fi = new FileInfo(AppPaths.ToLongPath(record.DestPath));
            record.DestSize = fi.Exists ? fi.Length : item.Size;
            record.DestMtimeTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : item.ModifiedUtc.Ticks;
            _store.UpsertMark(record);

            item.Record = record;
            item.Status = null;
            item.State = MarkState.Marked;
            progress.Report(new OpProgress { Message = $"已移入筛选：{item.Name}", Done = totalBytes, Total = totalBytes });
            return new OpResult { Ok = true, Payload = record, Warning = warning };
        }
        catch (OperationCanceledException)
        {
            Rollback(created, movedBack);
            return OpResult.Cancelled();
        }
        catch (Exception ex)
        {
            Rollback(created, movedBack);
            return OpResult.Fail(ex.Message);
        }
    }

    /// <summary>失败/取消时回滚：删掉刚复制的副本，把已移动的文件移回原位。</summary>
    private static void Rollback(List<string> created, List<(string From, string To)> movedBack)
    {
        foreach (var f in created)
        {
            try { if (File.Exists(AppPaths.ToLongPath(f))) File.Delete(AppPaths.ToLongPath(f)); } catch { }
        }
        for (int i = movedBack.Count - 1; i >= 0; i--)
        {
            var (from, to) = movedBack[i];
            try
            {
                if (!File.Exists(AppPaths.ToLongPath(from))) continue;
                string dest = FileOps.Exists(to) ? FileOps.UniquePath(Path.GetDirectoryName(to)!, Path.GetFileName(to)) : to;
                FileOps.TryMoveSameVolume(from, dest, out bool needCopy, out _);
                if (needCopy)
                {
                    File.Copy(AppPaths.ToLongPath(from), AppPaths.ToLongPath(dest), overwrite: false);
                    File.Delete(AppPaths.ToLongPath(from));
                }
            }
            catch { }
        }
    }

    private async Task<OpResult> DoUnmarkAsync(ImageItem item, MarkRecord record, CancellationToken ct, IProgress<OpProgress> progress)
    {
        // 这里全部是同步文件操作（队列线程上执行），await 只为统一签名，不切线程
        await Task.CompletedTask;
        try
        {
            progress.Report(new OpProgress { Message = $"取消筛选 {item.Name}" });

            if (record.Kind == MarkOpKind.Copy)
            {
                // 复制模式：删「筛选」里的副本（含伴生文件）→ 回收站；原目录文件绝对不动
                var targets = new List<string>();
                if (FileOps.Exists(record.DestPath)) targets.Add(record.DestPath);
                foreach (var c in record.Companions)
                    if (FileOps.Exists(c.DestPath)) targets.Add(c.DestPath);

                if (targets.Count > 0 && !FileOps.RecycleToTrash(targets, out string err))
                    return OpResult.Fail(err);
            }
            else
            {
                // 移动模式：把副本移回原位（冲突则改名，不覆盖）
                var pairs = new List<CompanionCopy>
                {
                    new() { SourcePath = record.SourcePath, DestPath = record.DestPath }
                };
                pairs.AddRange(record.Companions);

                foreach (var pair in pairs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!FileOps.Exists(pair.DestPath)) continue; // 副本已被用户在资源管理器里删掉
                    string srcDir = Path.GetDirectoryName(pair.SourcePath)!;
                    try { Directory.CreateDirectory(AppPaths.ToLongPath(srcDir)); } catch { }
                    string back = FileOps.UniquePath(srcDir, Path.GetFileName(pair.SourcePath));

                    if (!FileOps.TryMoveSameVolume(pair.DestPath, back, out bool needCopy, out string err))
                    {
                        if (!needCopy) return OpResult.Fail($"移回失败：{err}");
                        string part = back + ".part";
                        try
                        {
                            if (!FileOps.CopyFile(pair.DestPath, part, null, ct, out err))
                                return OpResult.Fail($"移回失败：{err}");
                            FileOps.AtomicRename(part, back);
                        }
                        finally
                        {
                            try { if (File.Exists(AppPaths.ToLongPath(part))) File.Delete(AppPaths.ToLongPath(part)); } catch { }
                        }
                        if (!FileOps.RecycleToTrash(new[] { pair.DestPath }, out err)) return OpResult.Fail(err);
                    }
                }
            }

            _store.DeleteMark(FolderId, item.Name);
            item.Record = null;
            item.Status = null;
            item.State = MarkState.None;
            return OpResult.Success();
        }
        catch (OperationCanceledException) { return OpResult.Cancelled(); }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    private static async Task<OpResult> CopyOneAsync(string src, string destDir, long size, long offset, long total,
        List<string> created, CancellationToken ct, IProgress<OpProgress> progress, string label)
    {
        string name = Path.GetFileName(src);
        string dest = FileOps.UniquePath(destDir, name);
        string part = dest + ".part";
        progress.Report(new OpProgress { Message = $"{label} {name}", Done = offset, Total = total });

        string copyError = "";
        bool ok = await Task.Run(() => FileOps.CopyFile(src, part, (doneBytes, tot) =>
        {
            progress.Report(new OpProgress { Message = $"{label} {name}", Done = offset + doneBytes, Total = total });
        }, ct, out copyError), ct).ConfigureAwait(false);

        if (!ok)
        {
            try { if (File.Exists(AppPaths.ToLongPath(part))) File.Delete(AppPaths.ToLongPath(part)); } catch { }
            return OpResult.Fail(string.IsNullOrEmpty(copyError) ? "复制失败" : copyError);
        }

        // 原子改名；若恰好被别的进程抢先占用，改名后重试（绝不覆盖）
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    FileOps.AtomicRename(part, dest);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    string newDest = FileOps.UniquePath(destDir, name);
                    string newPart = newDest + ".part";
                    File.Move(AppPaths.ToLongPath(part), AppPaths.ToLongPath(newPart), overwrite: false);
                    dest = newDest;
                    part = newPart;
                }
            }
        }
        catch (Exception ex)
        {
            try { if (File.Exists(AppPaths.ToLongPath(part))) File.Delete(AppPaths.ToLongPath(part)); } catch { }
            return OpResult.Fail($"重命名失败：{ex.Message}");
        }

        created.Add(dest);
        return new OpResult { Ok = true, Payload = dest };
    }

    private static async Task<OpResult> MoveOneAsync(string src, string destDir, long size, long offset, long total,
        CancellationToken ct, IProgress<OpProgress> progress)
    {
        string name = Path.GetFileName(src);
        string dest = FileOps.UniquePath(destDir, name);
        progress.Report(new OpProgress { Message = $"移动 {name}", Done = offset, Total = total });

        if (FileOps.TryMoveSameVolume(src, dest, out bool needCopy, out string err))
            return new OpResult { Ok = true, Payload = dest };

        if (!needCopy) return OpResult.Fail($"移动失败：{err}");

        // 跨卷：先复制成 .part、改名，再把源文件丢进回收站（可恢复，绝不硬删）
        string part = dest + ".part";
        try
        {
            string copyError = "";
            bool ok = await Task.Run(() => FileOps.CopyFile(src, part, null, ct, out copyError), ct).ConfigureAwait(false);
            if (!ok) return OpResult.Fail("跨卷移动失败：" + (string.IsNullOrEmpty(copyError) ? "复制失败" : copyError));
            FileOps.AtomicRename(part, dest);
        }
        finally
        {
            try { if (File.Exists(AppPaths.ToLongPath(part))) File.Delete(AppPaths.ToLongPath(part)); } catch { }
        }

        if (!FileOps.RecycleToTrash(new[] { src }, out string rerr))
            return OpResult.Fail("跨卷移动：源文件删除失败，" + rerr);
        return new OpResult { Ok = true, Payload = dest, Warning = "跨卷移动：已退化为「复制 + 删除源文件」" };
    }

    /// <summary>源文件可读性检查：返回 null 表示可读，否则返回中文原因。</summary>
    private static string? TryCheckReadable(string path)
    {
        try
        {
            using var fs = new FileStream(AppPaths.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            return null;
        }
        catch (FileNotFoundException) { return "源文件已不存在"; }
        catch (DirectoryNotFoundException) { return "源文件所在目录不存在"; }
        catch (UnauthorizedAccessException) { return "没有读取权限"; }
        catch (IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code switch
            {
                32 => "源文件正被其它程序占用",
                33 => "源文件被锁定",
                53 or 64 => "网络盘已断开",
                _ => "无法读取源文件：" + ex.Message
            };
        }
    }

    private static long CompanionSize(string path)
    {
        try { return new FileInfo(AppPaths.ToLongPath(path)).Length; } catch { return 0; }
    }

    public static string Fmt(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1048576.0:0.#} MB";
        return $"{bytes / 1073741824.0:0.##} GB";
    }
}
