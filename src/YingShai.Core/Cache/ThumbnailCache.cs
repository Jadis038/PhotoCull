using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using YingShai.Core.Imaging;
using YingShai.Core.Index;
using YingShai.Core.Model;
using YingShai.Core.Settings;

namespace YingShai.Core.Cache;

/// <summary>
/// 三级缓存的中间两级：
///   1. 内存 LRU（预算默认 512MB，见 settings.MemoryCacheMb）
///   2. 磁盘缓存（%LOCALAPPDATA%\影·筛\cache\&lt;2位&gt;\&lt;hash&gt;_160.jpg / _1024.jpg，索引进 SQLite，上限默认 5GB，LRU 清理）
/// 第三级是源文件解码（ImageDecoder）。
/// 缓存键 = hash(完整路径 + 大小 + 修改时间 + 解码器版本)，文件被替换后自然失效。
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    /// <summary>解码器版本：改了缩略图算法就 +1，旧缓存自动失效。</summary>
    public const int DecoderVersion = 3;

    private readonly StateStore _store;
    private readonly AppSettings _settings;
    private readonly object _lock = new();

    private sealed class Entry
    {
        public required string Key;
        public required BitmapSource Bitmap;
        public long Bytes;
    }

    private readonly Dictionary<string, LinkedListNode<Entry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _memoryBytes;
    private readonly long _memoryBudget;
    private long _lastDiskEvictTicks;

    public ThumbnailCache(StateStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
        _memoryBudget = Math.Max(64L, settings.MemoryCacheMb) * 1024 * 1024;
    }

    public long MemoryBytes { get { lock (_lock) return _memoryBytes; } }
    public int MemoryCount { get { lock (_lock) return _memory.Count; } }

    /// <summary>缓存键。</summary>
    public static string MakeKey(string path, long size, long mtimeTicks)
    {
        var seed = $"{path}|{size}|{mtimeTicks}|v{DecoderVersion}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string MakeKey(ImageItem item) => MakeKey(item.FullPath, item.Size, item.ModifiedUtc.Ticks);

    private static string RowKey(string key, int size) => $"{key}:{size}";

    private string DiskPath(string key, int size)
        => Path.Combine(AppPaths.CacheDir, key.Substring(0, 2), $"{key}_{size}.jpg");

    // ——— 读 ———

    /// <summary>先内存后磁盘；命中磁盘会回填内存。</summary>
    public BitmapSource? TryGet(string key, int size)
    {
        string rk = RowKey(key, size);
        if (TryGetMemory(rk, out var bmp)) return bmp;
        var fromDisk = TryGetDisk(key, size);
        if (fromDisk != null) PutMemory(rk, fromDisk);
        return fromDisk;
    }

    public bool TryGetMemory(string rowKey, out BitmapSource? bmp)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(rowKey, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bmp = node.Value.Bitmap;
                return true;
            }
        }
        bmp = null;
        return false;
    }

    private BitmapSource? TryGetDisk(string key, int size)
    {
        string rk = RowKey(key, size);
        var row = _store.GetCache(rk);
        if (row == null) return null;
        string? path = row.Value.Thumb;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(data, false);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            // 统一 96 DPI：老缓存里可能存着 300dpi 的 JFIF 密度，会让 WPF 按物理尺寸显示
            var bitmap = Math.Abs(bi.DpiX - 96) > 0.5 || Math.Abs(bi.DpiY - 96) > 0.5 ? WicImage.CopyPixels(bi) : (BitmapSource)bi;
            _store.TouchCache(rk);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ——— 写 ———

    public void PutMemory(string rowKey, BitmapSource bmp)
    {
        long bytes = WicImage.EstimateBytes(bmp);
        lock (_lock)
        {
            if (_memory.TryGetValue(rowKey, out var old))
            {
                _memoryBytes -= old.Value.Bytes;
                _lru.Remove(old);
                _memory.Remove(rowKey);
            }
            var node = _lru.AddFirst(new Entry { Key = rowKey, Bitmap = bmp, Bytes = bytes });
            _memory[rowKey] = node;
            _memoryBytes += bytes;
            EvictMemoryLocked();
        }
    }

    private void EvictMemoryLocked()
    {
        while (_memoryBytes > _memoryBudget && _lru.Last != null)
        {
            var last = _lru.Last;
            _lru.RemoveLast();
            _memory.Remove(last.Value.Key);
            _memoryBytes -= last.Value.Bytes;
        }
    }

    /// <summary>把图像编码成 JPEG 落盘（后台线程调用）。</summary>
    public void PutDisk(string key, int size, BitmapSource bmp)
    {
        string rk = RowKey(key, size);
        string path = DiskPath(key, size);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var jpeg = WicImage.EncodeJpeg(bmp, size <= 200 ? 82 : 90);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, jpeg);
            File.Move(tmp, path, overwrite: true);
            _store.UpsertCache(rk, path, null, jpeg.Length, DecoderVersion);
            MaybeEvictDisk();
        }
        catch
        {
            // 缓存写失败不影响主流程
        }
    }

    /// <summary>按 LRU 把磁盘缓存压到上限以内（最多每 30 秒检查一次）。</summary>
    public void MaybeEvictDisk(bool force = false)
    {
        long now = DateTime.UtcNow.Ticks;
        if (!force && now - Interlocked.Read(ref _lastDiskEvictTicks) < TimeSpan.TicksPerSecond * 30) return;
        Interlocked.Exchange(ref _lastDiskEvictTicks, now);

        long limit = (long)(Math.Max(0.2, _settings.CacheLimitGb) * 1024 * 1024 * 1024);
        long total = _store.TotalCacheBytes();
        if (total <= limit) return;

        foreach (var row in _store.GetLruRows(512))
        {
            if (total <= limit) break;
            try { if (!string.IsNullOrEmpty(row.Thumb) && File.Exists(row.Thumb)) File.Delete(row.Thumb); } catch { }
            _store.DeleteCache(row.Key);
            total -= row.Bytes;
        }
    }

    /// <summary>把内存缓存压回预算内（可视区外的大量缩略图由会话层主动释放）。</summary>
    public void TrimMemory()
    {
        lock (_lock) EvictMemoryLocked();
    }

    /// <summary>丢弃某一个键的两档缓存（源文件变了/被删了）。</summary>
    public void Drop(string key)
    {
        foreach (int size in new[] { ImageDecoder.StripSize, ImageDecoder.PreviewSize })
        {
            string rk = RowKey(key, size);
            lock (_lock)
            {
                if (_memory.TryGetValue(rk, out var node))
                {
                    _memoryBytes -= node.Value.Bytes;
                    _lru.Remove(node);
                    _memory.Remove(rk);
                }
            }
            try
            {
                string p = DiskPath(key, size);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
            _store.DeleteCache(rk);
        }
    }

    public void ClearAll()
    {
        lock (_lock) { _memory.Clear(); _lru.Clear(); _memoryBytes = 0; }
        try
        {
            if (Directory.Exists(AppPaths.CacheDir))
            {
                foreach (var d in Directory.EnumerateDirectories(AppPaths.CacheDir))
                {
                    try { Directory.Delete(d, recursive: true); } catch { }
                }
                foreach (var f in Directory.EnumerateFiles(AppPaths.CacheDir))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    public void Dispose() { /* StateStore 由会话层持有 */ }
}
