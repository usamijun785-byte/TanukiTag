using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TanukiTag.Services;

/// <summary>
/// サムネイルキャッシュ。Python版(tagfiler.py)と同じく、ファイル1件ずつではなく
/// SQLite 1本に BLOB として格納する。理由は同じ:
///   - 高速スクロールで大量の新規セルが可視範囲に入っても、ファイル単位の
///     open/read/close が発生しない（ディスクI/Oオーバーヘッドの削減）
///   - 「キャッシュ済みかどうか」は起動時にロードしたメモリ上の key 集合で
///     判定できるため、判定自体にディスクI/Oが要らない
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _dbLock = new();
    private readonly HashSet<string> _knownKeys = new();

    public ThumbnailCache(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS thumbs (key TEXT PRIMARY KEY, data BLOB);";
            cmd.ExecuteNonQuery();
        }

        // 既存キーを起動時に1回だけメモリへロード
        using var loadCmd = _conn.CreateCommand();
        loadCmd.CommandText = "SELECT key FROM thumbs;";
        using var reader = loadCmd.ExecuteReader();
        while (reader.Read())
        {
            _knownKeys.Add(reader.GetString(0));
        }
    }

    /// <summary>キー生成（パス＋幅＋高さ＋余白背景色＋任意の追加情報のSHA256。プロセス再起動後も安定）。
    /// 幅と高さを別々にキーへ含めるのは、グリッド形状（正方形/縦長/横長）ごとに
    /// パディングの入り方が変わるサムネイルを別物として正しくキャッシュし分けるため。
    /// 背景色をキーに含めるのは、テーマ切り替えでサムネイル余白の色を正しく
    /// 再生成させるため（含めないと古いテーマの余白色のキャッシュがヒットし続けてしまう）。
    /// extraは、動画の再生時間表示オン/オフのように「同じファイル・同じサイズでも
    /// 出力される画像バイト列自体が変わる」設定を切り替えた際に、古い見た目のキャッシュが
    /// ヒットし続けるのを防ぐための任意の追加識別子。</summary>
    public static string MakeKey(string path, int width, int height, string bgColorHex, string extra = "")
    {
        var raw = $"{path}|{width}x{height}|{bgColorHex}|{extra}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..24];
    }

    /// <summary>ディスクI/Oなしでキャッシュ有無を判定</summary>
    public bool Has(string key)
    {
        lock (_dbLock)
        {
            return _knownKeys.Contains(key);
        }
    }

    public byte[]? Get(string key)
    {
        lock (_dbLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT data FROM thumbs WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            var result = cmd.ExecuteScalar();
            return result as byte[];
        }
    }

    public void Set(string key, byte[] data)
    {
        lock (_dbLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO thumbs(key, data) VALUES ($key, $data);";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$data", data);
            cmd.ExecuteNonQuery();
            _knownKeys.Add(key);
        }
    }

    /// <summary>キャッシュ済みサムネイルを全件削除する（設定初期化ボタン用）。
    /// 削除後はVACUUMでファイルサイズも縮小しておく。</summary>
    public void Clear()
    {
        lock (_dbLock)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM thumbs;";
                cmd.ExecuteNonQuery();
            }
            _knownKeys.Clear();
            using (var vacuum = _conn.CreateCommand())
            {
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }
        }
    }

    public void Dispose() => _conn.Dispose();
}
