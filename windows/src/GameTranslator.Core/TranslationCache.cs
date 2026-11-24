using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GameTranslator.Core;

public sealed record CachedTranslation(
    string Key, string ProfileId, string Source, string Target, string Engine, DateTimeOffset CreatedAt, int Hits);

/// <summary>Cache SQLite (file) cộng cache bộ nhớ giới hạn 500 mục. Cùng schema với bản macOS.</summary>
public sealed class TranslationCache : IDisposable
{
    public const int MemoryLimit = 500;

    private const string SelectColumns = "SELECT key, profileId, source, target, engine, createdAt, hits FROM translations";

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private readonly Dictionary<string, CachedTranslation> _memory = [];
    private readonly List<string> _memoryOrder = [];

    /// <summary>`path` là file SQLite; dùng ":memory:" cho test.</summary>
    public TranslationCache(string path)
    {
        if (path != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        try
        {
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            _db.Open();
            Exec("""
                CREATE TABLE IF NOT EXISTS translations (
                    key TEXT PRIMARY KEY, profileId TEXT NOT NULL, source TEXT NOT NULL, target TEXT NOT NULL,
                    engine TEXT NOT NULL, createdAt REAL NOT NULL, hits INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS idx_translations_created ON translations(createdAt DESC);
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """);
        }
        catch (SqliteException ex)
        {
            throw new TranslationException(TranslationErrorKind.Unavailable, $"Không mở được cache: {path} ({ex.Message})");
        }
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Giống `String.lowercased()` của Swift: ánh xạ từng ký tự; riêng İ (U+0130) thành "i̇" (i + U+0307).</summary>
    internal static string SwiftLowercase(string s) => s.Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>SHA-256 hex chữ thường của UTF-8(profileId + "\n" + source viết thường).</summary>
    public static string Key(string profileId, string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(profileId + "\n" + SwiftLowercase(source)));
        return Convert.ToHexStringLower(bytes);
    }

    public CachedTranslation? Lookup(string profileId, string source)
    {
        var key = Key(profileId, source);
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var hit))
            {
                BumpHits(key);
                return hit;
            }
            var row = QueryRows(SelectColumns + " WHERE key = $k", ("$k", key)).FirstOrDefault();
            if (row is null) return null;
            Remember(row);
            BumpHits(key);
            return row;
        }
    }

    public void Store(string profileId, string source, string target, string engine)
    {
        var key = Key(profileId, source);
        var entry = new CachedTranslation(key, profileId, source, target, engine, DateTimeOffset.UtcNow, 0);
        lock (_lock)
        {
            Run("""
                INSERT INTO translations (key, profileId, source, target, engine, createdAt, hits) VALUES ($k, $p, $s, $t, $e, $c, 0)
                ON CONFLICT(key) DO UPDATE SET target = excluded.target, engine = excluded.engine
                """,
                ("$k", key), ("$p", profileId), ("$s", source), ("$t", target), ("$e", engine),
                ("$c", entry.CreatedAt.ToUnixTimeMilliseconds() / 1000.0));
            Remember(entry);
        }
    }

    /// <summary>Sửa bản dịch trong màn hình lịch sử; mặc định đánh dấu là sửa tay.</summary>
    public void UpdateTarget(string key, string target, string engine = "manual")
    {
        lock (_lock)
        {
            Run("UPDATE translations SET target = $t, engine = $e WHERE key = $k", ("$t", target), ("$e", engine), ("$k", key));
            _memory.Remove(key);
            _memoryOrder.Remove(key);
        }
    }

    public IReadOnlyList<CachedTranslation> History(string? profileId = null, int limit = 200)
    {
        lock (_lock)
        {
            return profileId is null
                ? QueryRows(SelectColumns + " ORDER BY createdAt DESC LIMIT $l", ("$l", (long)limit))
                : QueryRows(SelectColumns + " WHERE profileId = $p ORDER BY createdAt DESC LIMIT $l", ("$p", profileId), ("$l", (long)limit));
        }
    }

    /// <summary>Số câu đã lưu và tổng số lần dùng lại từ cache.</summary>
    public (int Count, int Hits) Stats(string profileId)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT COUNT(*), COALESCE(SUM(hits), 0) FROM translations WHERE profileId = $p", ("$p", profileId));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ((int)reader.GetInt64(0), (int)reader.GetInt64(1)) : (0, 0);
        }
    }

    public void Clear(string profileId)
    {
        lock (_lock)
        {
            Run("DELETE FROM translations WHERE profileId = $p", ("$p", profileId));
            _memory.Clear();
            _memoryOrder.Clear();
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            Run("INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$k", key), ("$v", value));
        }
    }

    public string? Meta(string key)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT value FROM meta WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    // --- Nội bộ (đã giữ lock) ---

    private void Remember(CachedTranslation entry)
    {
        if (!_memory.ContainsKey(entry.Key)) _memoryOrder.Add(entry.Key);
        _memory[entry.Key] = entry;
        while (_memoryOrder.Count > MemoryLimit)
        {
            _memory.Remove(_memoryOrder[0]);
            _memoryOrder.RemoveAt(0);
        }
    }

    private void BumpHits(string key) => Run("UPDATE translations SET hits = hits + 1 WHERE key = $k", ("$k", key));

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] binds)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in binds) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Run(string sql, params (string Name, object Value)[] binds)
    {
        using var cmd = Command(sql, binds);
        cmd.ExecuteNonQuery();
    }

    private List<CachedTranslation> QueryRows(string sql, params (string Name, object Value)[] binds)
    {
        var rows = new List<CachedTranslation>();
        using var cmd = Command(sql, binds);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new CachedTranslation(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(r.GetDouble(5) * 1000)), r.GetInt32(6)));
        }
        return rows;
    }
}
