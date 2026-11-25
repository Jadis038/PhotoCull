using System.Text.Json;
using Microsoft.Data.Sqlite;
using YingShai.Core.Model;

namespace YingShai.Core.Index;

public sealed class CacheRow
{
    public required string Key { get; init; }
    public string? Thumb { get; init; }
    public string? Preview { get; init; }
    public long Bytes { get; init; }
    public long LastUsedUtc { get; init; }
    public int DecoderVer { get; init; }
}

/// <summary>
/// 状态索引（SQLite，WAL 模式）：
///  - marks 表：每个文件夹里「已移入筛选」的记录（源/副本路径、操作类型、大小、修改时间、伴生文件）
///  - cache 表：磁盘缩略图缓存索引 + LRU 时间戳
/// 放在 %LOCALAPPDATA%\影·筛\state\index.db，图片目录里绝不放数据库文件。
/// 所有访问串行化（单连接 + lock），写操作只发生在后台线程，不会卡 UI。
/// </summary>
public sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions CompanionJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public StateStore(string? dbPath = null)
    {
        AppPaths.EnsureDirectories();
        dbPath ??= AppPaths.IndexDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=5000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS folders(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              path TEXT NOT NULL UNIQUE,
              last_open INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS marks(
              folder_id INTEGER NOT NULL,
              item TEXT NOT NULL,
              src TEXT NOT NULL,
              dst TEXT NOT NULL,
              op INTEGER NOT NULL,
              size INTEGER NOT NULL DEFAULT 0,
              mtime INTEGER NOT NULL DEFAULT 0,
              companions TEXT NOT NULL DEFAULT '[]',
              created INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(folder_id, item)
            );
            CREATE TABLE IF NOT EXISTS cache(
              key TEXT PRIMARY KEY,
              thumb TEXT,
              preview TEXT,
              bytes INTEGER NOT NULL DEFAULT 0,
              last_used INTEGER NOT NULL DEFAULT 0,
              decoder_ver INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    private void Exec(string sql, Action<SqliteCommand>? bind = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }
    }

    private T Query<T>(string sql, Func<SqliteDataReader, T> read, Action<SqliteCommand>? bind = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            using var r = cmd.ExecuteReader();
            return read(r);
        }
    }

    // ——— 文件夹 ———

    public int GetOrCreateFolder(string folderPath) => GetOrCreateFolder(folderPath, out _);

    /// <summary>取文件夹 id；isNew 表示这个路径是第一次见到（用于判断是否需要按「筛选」内容重建索引）。</summary>
    public int GetOrCreateFolder(string folderPath, out bool isNew)
    {
        string norm = NormalizePath(folderPath);
        int existing = Query("SELECT id FROM folders WHERE path=$p", r => r.Read() ? r.GetInt32(0) : 0,
            c => c.Parameters.AddWithValue("$p", norm));
        if (existing > 0)
        {
            Exec("UPDATE folders SET last_open=$t WHERE id=$id",
                c => { c.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks); c.Parameters.AddWithValue("$id", existing); });
            isNew = false;
            return existing;
        }
        isNew = true;
        return Query("""
            INSERT INTO folders(path, last_open) VALUES($p, $t)
            ON CONFLICT(path) DO UPDATE SET last_open=$t
            RETURNING id;
            """, r => r.Read() ? r.GetInt32(0) : 0,
            c => { c.Parameters.AddWithValue("$p", norm); c.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks); });
    }

    public static string NormalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch { return path.ToLowerInvariant(); }
    }

    // ——— 标记记录 ———

    public List<MarkRecord> LoadMarks(int folderId)
    {
        return Query("SELECT item, src, dst, op, size, mtime, companions, created FROM marks WHERE folder_id=$f",
            r =>
            {
                var list = new List<MarkRecord>();
                while (r.Read())
                {
                    var comps = JsonSerializer.Deserialize<List<CompanionCopy>>(r.GetString(6), CompanionJson) ?? new();
                    list.Add(new MarkRecord
                    {
                        FolderId = folderId,
                        ItemName = r.GetString(0),
                        SourcePath = r.GetString(1),
                        DestPath = r.GetString(2),
                        Kind = (MarkOpKind)r.GetInt32(3),
                        DestSize = r.GetInt64(4),
                        DestMtimeTicks = r.GetInt64(5),
                        Companions = comps,
                        CreatedLocal = r.GetInt64(7) > 0 ? new DateTime(r.GetInt64(7), DateTimeKind.Utc).ToLocalTime() : DateTime.Now
                    });
                }
                return list;
            },
            c => c.Parameters.AddWithValue("$f", folderId));
    }

    public void UpsertMark(MarkRecord m)
    {
        string comps = JsonSerializer.Serialize(m.Companions, CompanionJson);
        Exec("""
            INSERT INTO marks(folder_id, item, src, dst, op, size, mtime, companions, created)
            VALUES($f,$i,$s,$d,$o,$z,$m,$c,$cr)
            ON CONFLICT(folder_id, item) DO UPDATE SET
              src=$s, dst=$d, op=$o, size=$z, mtime=$m, companions=$c, created=$cr;
            """,
            c =>
            {
                c.Parameters.AddWithValue("$f", m.FolderId);
                c.Parameters.AddWithValue("$i", m.ItemName);
                c.Parameters.AddWithValue("$s", m.SourcePath);
                c.Parameters.AddWithValue("$d", m.DestPath);
                c.Parameters.AddWithValue("$o", (int)m.Kind);
                c.Parameters.AddWithValue("$z", m.DestSize);
                c.Parameters.AddWithValue("$m", m.DestMtimeTicks);
                c.Parameters.AddWithValue("$c", comps);
                c.Parameters.AddWithValue("$cr", m.CreatedLocal.ToUniversalTime().Ticks);
            });
    }

    public void DeleteMark(int folderId, string itemName)
        => Exec("DELETE FROM marks WHERE folder_id=$f AND item=$i",
            c => { c.Parameters.AddWithValue("$f", folderId); c.Parameters.AddWithValue("$i", itemName); });

    public void UpdateMarkDestination(int folderId, string itemName, string dst, long size, long mtimeTicks)
        => Exec("UPDATE marks SET dst=$d, size=$z, mtime=$m WHERE folder_id=$f AND item=$i",
            c =>
            {
                c.Parameters.AddWithValue("$d", dst);
                c.Parameters.AddWithValue("$z", size);
                c.Parameters.AddWithValue("$m", mtimeTicks);
                c.Parameters.AddWithValue("$f", folderId);
                c.Parameters.AddWithValue("$i", itemName);
            });

    // ——— 磁盘缩略图缓存索引 ———

    public void UpsertCache(string key, string? thumbPath, string? previewPath, long bytes, int decoderVer)
    {
        Exec("""
            INSERT INTO cache(key, thumb, preview, bytes, last_used, decoder_ver)
            VALUES($k,$t,$p,$b,$u,$v)
            ON CONFLICT(key) DO UPDATE SET
              thumb=COALESCE($t, thumb), preview=COALESCE($p, preview),
              bytes=MAX(bytes, $b), last_used=$u, decoder_ver=$v;
            """,
            c =>
            {
                c.Parameters.AddWithValue("$k", key);
                c.Parameters.AddWithValue("$t", (object?)thumbPath ?? DBNull.Value);
                c.Parameters.AddWithValue("$p", (object?)previewPath ?? DBNull.Value);
                c.Parameters.AddWithValue("$b", bytes);
                c.Parameters.AddWithValue("$u", DateTime.UtcNow.Ticks);
                c.Parameters.AddWithValue("$v", decoderVer);
            });
    }

    public (string? Thumb, string? Preview, int DecoderVer)? GetCache(string key)
    {
        return Query("SELECT thumb, preview, decoder_ver FROM cache WHERE key=$k",
            r => r.Read()
                ? (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2))
                : ((string?)null, (string?)null, 0),
            c => c.Parameters.AddWithValue("$k", key));
    }

    public void TouchCache(string key)
        => Exec("UPDATE cache SET last_used=$u WHERE key=$k",
            c => { c.Parameters.AddWithValue("$u", DateTime.UtcNow.Ticks); c.Parameters.AddWithValue("$k", key); });

    public long TotalCacheBytes()
        => Query("SELECT COALESCE(SUM(bytes),0) FROM cache", r => r.Read() ? r.GetInt64(0) : 0L);

    /// <summary>按最久未用排序取出待清理的行。</summary>
    public List<CacheRow> GetLruRows(int limit)
        => Query("SELECT key, thumb, preview, bytes, last_used, decoder_ver FROM cache ORDER BY last_used ASC LIMIT $l",
            r =>
            {
                var list = new List<CacheRow>();
                while (r.Read())
                    list.Add(new CacheRow
                    {
                        Key = r.GetString(0),
                        Thumb = r.IsDBNull(1) ? null : r.GetString(1),
                        Preview = r.IsDBNull(2) ? null : r.GetString(2),
                        Bytes = r.GetInt64(3),
                        LastUsedUtc = r.GetInt64(4),
                        DecoderVer = r.GetInt32(5)
                    });
                return list;
            },
            c => c.Parameters.AddWithValue("$l", limit));

    public void DeleteCache(string key)
        => Exec("DELETE FROM cache WHERE key=$k", c => c.Parameters.AddWithValue("$k", key));

    public int CountCache()
        => Query("SELECT COUNT(*) FROM cache", r => r.Read() ? r.GetInt32(0) : 0);

    public void Dispose()
    {
        lock (_lock)
        {
            try { _conn.Dispose(); } catch { }
        }
    }
}
