using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TanukiTag.Models;

namespace TanukiTag.Services;

/// <summary>
/// Python版 tagfiler.py の Database クラスと同じスキーマ・同じメソッド構成の移植。
/// メソッド名は対応関係が分かりやすいよう Python版の snake_case を PascalCase にしただけで
/// 意図的に1対1対応させている（例: add_file → AddFile）。
/// </summary>
public sealed class AppDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public AppDatabase(string path)
    {
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA foreign_keys=ON");

        Init();
    }

    private void Init()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT UNIQUE NOT NULL, filename TEXT NOT NULL,
                star INTEGER DEFAULT 0, comment TEXT DEFAULT '',
                added_at TEXT DEFAULT (datetime('now','localtime')),
                accessed_at TEXT DEFAULT (datetime('now','localtime'))
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS tag_groups (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT UNIQUE NOT NULL,
                sort_order INTEGER DEFAULT 0
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT UNIQUE NOT NULL, color TEXT DEFAULT '#888888'
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS file_tags (
                file_id INTEGER, tag_id INTEGER,
                PRIMARY KEY(file_id,tag_id),
                FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE,
                FOREIGN KEY(tag_id)  REFERENCES tags(id)  ON DELETE CASCADE
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS recent (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER,
                opened_at TEXT DEFAULT (datetime('now','localtime')),
                FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL,
                sort_order INTEGER DEFAULT 0
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_fp ON files(path);");

        // 既存DBへの追加列マイグレーション（Python版と同様、失敗は無視）
        foreach (var sql in new[]
        {
            "ALTER TABLE files ADD COLUMN open_count INTEGER DEFAULT 0",
            "ALTER TABLE tags  ADD COLUMN group_id INTEGER REFERENCES tag_groups(id) ON DELETE SET NULL",
            "ALTER TABLE tags  ADD COLUMN sort_order INTEGER DEFAULT 0",
            "ALTER TABLE tag_groups ADD COLUMN collapsed INTEGER DEFAULT 0",
            // タグごとに右側ファイルグリッドの表示形状（正方形/縦長コミック/横長動画）を記憶する。
            // 既定は既存の見た目と同じ'square'。
            "ALTER TABLE tags  ADD COLUMN grid_shape TEXT DEFAULT 'square'",
            // フォルダショートカットもタグと同じくグループへ分類できるようにする。
            "ALTER TABLE folders ADD COLUMN group_id INTEGER REFERENCES tag_groups(id) ON DELETE SET NULL",
        })
        {
            try
            {
                Exec(sql);
            }
            catch (SqliteException ex)
            {
                // 「列が既に存在する」（2回目以降の起動で必ず発生する想定内のエラー）かどうかを
                // メッセージで判別し、それ以外の原因（DBファイル破損・権限不足など）であれば
                // 分かるようにデバッグ出力する。
                bool isDuplicateColumn = ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase);
                Debug.WriteLine(isDuplicateColumn
                    ? $"[AppDatabase.Init] 想定内: 列は既に存在します ({sql}) : {ex.Message}"
                    : $"[AppDatabase.Init] 想定外のSQLiteエラー ({sql}) : {ex.Message}");
            }
        }
    }

    // ── 内部ヘルパー ─────────────────────────
    private void Exec(string sql, params (string name, object? value)[] p)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in p)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteDataReader Query(string sql, params (string name, object? value)[] p)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in p)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteReader();
    }

    private static FileRecord ReadFile(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Path = r.GetString(r.GetOrdinal("path")),
        Filename = r.GetString(r.GetOrdinal("filename")),
        Star = r.GetInt32(r.GetOrdinal("star")),
        Comment = r.IsDBNull(r.GetOrdinal("comment")) ? "" : r.GetString(r.GetOrdinal("comment")),
        AddedAt = r.GetString(r.GetOrdinal("added_at")),
        AccessedAt = r.GetString(r.GetOrdinal("accessed_at")),
        OpenCount = HasColumn(r, "open_count") && !r.IsDBNull(r.GetOrdinal("open_count"))
            ? r.GetInt32(r.GetOrdinal("open_count")) : 0,
    };

    private static bool HasColumn(SqliteDataReader r, string name)
    {
        for (int i = 0; i < r.FieldCount; i++)
            if (r.GetName(i) == name) return true;
        return false;
    }

    private List<FileRecord> QueryFiles(string sql, params (string name, object? value)[] p)
    {
        lock (_lock)
        {
            using var reader = Query(sql, p);
            var result = new List<FileRecord>();
            while (reader.Read()) result.Add(ReadFile(reader));
            return result;
        }
    }

    // ── files ─────────────────────────
    public long? AddFile(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        try
        {
            Exec("INSERT OR IGNORE INTO files(path,filename) VALUES($p,$n)",
                ("$p", fullPath), ("$n", System.IO.Path.GetFileName(fullPath)));
            lock (_lock)
            {
                using var reader = Query("SELECT id FROM files WHERE path=$p", ("$p", fullPath));
                return reader.Read() ? reader.GetInt64(0) : null;
            }
        }
        catch { return null; }
    }

    public FileRecord? GetFileById(long fid)
    {
        var list = QueryFiles("SELECT * FROM files WHERE id=$id", ("$id", fid));
        return list.Count > 0 ? list[0] : null;
    }

    public List<FileRecord> GetAllFiles() =>
        QueryFiles("SELECT * FROM files ORDER BY accessed_at DESC");

    public List<FileRecord> GetFilesByTag(long tid) => QueryFiles("""
        SELECT f.* FROM files f
        JOIN file_tags ft ON f.id=ft.file_id
        WHERE ft.tag_id=$tid ORDER BY f.accessed_at DESC
        """, ("$tid", tid));

    public List<FileRecord> GetUntaggedFiles() => QueryFiles("""
        SELECT * FROM files WHERE id NOT IN
        (SELECT DISTINCT file_id FROM file_tags)
        ORDER BY accessed_at DESC
        """);

    public List<FileRecord> GetStarredFiles() =>
        QueryFiles("SELECT * FROM files WHERE star>0 ORDER BY star DESC");

    public List<FileRecord> GetMostOpened(int limit = 100) => QueryFiles(
        "SELECT * FROM files WHERE open_count>0 ORDER BY open_count DESC LIMIT $lim",
        ("$lim", limit));

    public List<FileRecord> GetRecentFiles(int limit = 50) => QueryFiles("""
        SELECT DISTINCT f.* FROM files f
        JOIN recent r ON f.id=r.file_id
        ORDER BY r.opened_at DESC LIMIT $lim
        """, ("$lim", limit));

    /// <summary>スペース区切りでAND検索（ファイル名・コメント対象）</summary>
    public List<FileRecord> SearchFiles(string q)
    {
        var keywords = q.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keywords.Length == 0) return GetAllFiles();

        var conditions = string.Join(" AND ",
            keywords.Select((_, i) => $"(filename LIKE $k{i} OR comment LIKE $k{i})"));
        var sql = $"SELECT * FROM files WHERE {conditions} ORDER BY accessed_at DESC";

        var ps = keywords.Select((k, i) => ($"$k{i}", (object?)$"%{k}%")).ToArray();
        return QueryFiles(sql, ps);
    }

    public void UpdateStar(long fid, int v) =>
        Exec("UPDATE files SET star=$v WHERE id=$id", ("$v", v), ("$id", fid));

    public void UpdateComment(long fid, string v) =>
        Exec("UPDATE files SET comment=$v WHERE id=$id", ("$v", v), ("$id", fid));

    public void SetAccessedAt(long fid, string dtStr) =>
        Exec("UPDATE files SET accessed_at=$v WHERE id=$id", ("$v", dtStr), ("$id", fid));

    public void UpdatePath(long fid, string newPath) => Exec(
        "UPDATE files SET path=$p, filename=$n WHERE id=$id",
        ("$p", newPath), ("$n", System.IO.Path.GetFileName(newPath)), ("$id", fid));

    public void UpdateAccessed(long fid)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Exec("""
            UPDATE files SET accessed_at=$now, open_count=COALESCE(open_count,0)+1 WHERE id=$id
            """, ("$now", now), ("$id", fid));
    }

    public void UpdateFilePath(string oldPath, string newPath)
    {
        newPath = System.IO.Path.GetFullPath(newPath);
        Exec("UPDATE files SET path=$p, filename=$n WHERE path=$old",
            ("$p", newPath), ("$n", System.IO.Path.GetFileName(newPath)), ("$old", oldPath));
    }

    /// <summary>フォルダ以下の全パスをまとめて更新（フォルダ移動追従）</summary>
    public int UpdateDirPath(string oldDir, string newDir)
    {
        static string Norm(string d) => d.TrimEnd('\\', '/');
        oldDir = Norm(oldDir);
        newDir = Norm(newDir);

        var updated = 0;
        lock (_lock)
        {
            var rows = new List<(long Id, string Path)>();
            using (var reader = Query("SELECT id, path FROM files"))
            {
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            foreach (var (id, p) in rows)
            {
                var pLower = p.ToLowerInvariant();
                var oldLower = oldDir.ToLowerInvariant();
                if (pLower.StartsWith(oldLower + System.IO.Path.DirectorySeparatorChar) || pLower == oldLower)
                {
                    var suffix = p[oldDir.Length..];
                    var newPath = newDir + suffix;
                    var newName = System.IO.Path.GetFileName(newPath);
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "UPDATE files SET path=$p, filename=$n WHERE id=$id";
                    cmd.Parameters.AddWithValue("$p", newPath);
                    cmd.Parameters.AddWithValue("$n", newName);
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                    updated++;
                }
            }
        }
        return updated;
    }

    public void DeleteFile(long fid) => Exec("DELETE FROM files WHERE id=$id", ("$id", fid));

    // ── folders（タグリストにドラッグ登録したフォルダショートカット） ─────────────────────────
    public long? AddFolder(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed;
            Exec("INSERT OR IGNORE INTO folders(path,name) VALUES($p,$n)", ("$p", path), ("$n", name));
            lock (_lock)
            {
                using var reader = Query("SELECT id FROM folders WHERE path=$p", ("$p", path));
                return reader.Read() ? reader.GetInt64(0) : null;
            }
        }
        catch { return null; }
    }

    public List<FolderRecord> GetAllFolders()
    {
        lock (_lock)
        {
            using var reader = Query(
                "SELECT id,path,name,sort_order,group_id FROM folders " +
                "ORDER BY group_id IS NULL, group_id, sort_order, name");
            var result = new List<FolderRecord>();
            while (reader.Read())
            {
                result.Add(new FolderRecord
                {
                    Id = reader.GetInt64(0),
                    Path = reader.GetString(1),
                    Name = reader.GetString(2),
                    SortOrder = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    GroupId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                });
            }
            return result;
        }
    }

    /// <summary>フォルダショートカットの所属グループを変更する（タグのSetTagGroupと同じ考え方）。</summary>
    public void SetFolderGroup(long folderId, long? groupId) =>
        Exec("UPDATE folders SET group_id=$g WHERE id=$id", ("$g", groupId), ("$id", folderId));

    public void DeleteFolder(long id) => Exec("DELETE FROM folders WHERE id=$id", ("$id", id));

    public void RenameFolder(long id, string name) =>
        Exec("UPDATE folders SET name=$n WHERE id=$id", ("$n", name), ("$id", id));

    // ── tags ─────────────────────────
    public long? AddTag(string name, string color = "#888888")
    {
        try
        {
            Exec("INSERT OR IGNORE INTO tags(name,color) VALUES($n,$c)", ("$n", name), ("$c", color));
            lock (_lock)
            {
                using var reader = Query("SELECT id FROM tags WHERE name=$n", ("$n", name));
                return reader.Read() ? reader.GetInt64(0) : null;
            }
        }
        catch { return null; }
    }

    /// <summary>グループ内・未分類内でのタグの並び順を決めるORDER BY句の断片。
    /// group_id自体の並び順（グループ行そのものの表示順）はGetAllTagGroups側のsortKeyで別に制御するため、
    /// ここではグループの枠を崩さない先頭2条件（group_id IS NULL, group_id）は常に固定し、
    /// その内側の並びだけをsortKeyで切り替える。</summary>
    private static string TagOrderByClause(string sortKey) => sortKey switch
    {
        "name_asc" => "t.group_id IS NULL, t.group_id, t.name",
        "name_desc" => "t.group_id IS NULL, t.group_id, t.name DESC",
        "added_asc" => "t.group_id IS NULL, t.group_id, t.id",
        "added_desc" => "t.group_id IS NULL, t.group_id, t.id DESC",
        _ /* count_desc */ => "t.group_id IS NULL, t.group_id, COUNT(ft.file_id) DESC, t.name",
    };

    public List<TagRecord> GetAllTags() => GetAllTags("count_desc");

    /// <summary>タグ一覧を取得する。sortKeyは AppConfig.TagSortKeyOptions のキー
    /// （"count_desc"/"name_asc"/"name_desc"/"added_asc"/"added_desc"）。
    /// グループへの割り当て自体（どのグループに属するか）には影響しない。</summary>
    public List<TagRecord> GetAllTags(string sortKey)
    {
        lock (_lock)
        {
            using var reader = Query($"""
                SELECT t.id, t.name, t.color, t.group_id, t.sort_order,
                       COUNT(ft.file_id) AS file_count, t.grid_shape
                FROM tags t LEFT JOIN file_tags ft ON ft.tag_id=t.id
                GROUP BY t.id
                ORDER BY {TagOrderByClause(sortKey)}
                """);
            var result = new List<TagRecord>();
            while (reader.Read())
            {
                result.Add(new TagRecord
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Color = reader.IsDBNull(2) ? "#888888" : reader.GetString(2),
                    GroupId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    SortOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    FileCount = reader.GetInt32(5),
                    GridShape = reader.IsDBNull(6) ? "square" : reader.GetString(6),
                });
            }
            return result;
        }
    }

    private static string GroupOrderByClause(string sortKey) => sortKey switch
    {
        "name_desc" => "name DESC",
        "added_asc" => "id",
        "added_desc" => "id DESC",
        _ /* name_asc */ => "name",
    };

    public List<TagGroupRecord> GetAllTagGroups() => GetAllTagGroups("name_asc");

    /// <summary>グループ一覧を取得する。sortKeyは AppConfig.GroupSortKeyOptions のキー
    /// （"name_asc"/"name_desc"/"added_asc"/"added_desc"）。タグの並び順（GetAllTagsのsortKey）とは独立。</summary>
    public List<TagGroupRecord> GetAllTagGroups(string sortKey)
    {
        lock (_lock)
        {
            using var reader = Query(
                $"SELECT id,name,sort_order,collapsed FROM tag_groups ORDER BY {GroupOrderByClause(sortKey)}");
            var result = new List<TagGroupRecord>();
            while (reader.Read())
            {
                result.Add(new TagGroupRecord
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    SortOrder = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Collapsed = !reader.IsDBNull(3) && reader.GetInt32(3) != 0,
                });
            }
            return result;
        }
    }

    public long? AddTagGroup(string name)
    {
        try
        {
            Exec("INSERT INTO tag_groups(name) VALUES($n)", ("$n", name));
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT last_insert_rowid()";
                return (long)cmd.ExecuteScalar()!;
            }
        }
        catch { return null; }
    }

    public void RenameTagGroup(long gid, string name) =>
        Exec("UPDATE tag_groups SET name=$n WHERE id=$id", ("$n", name), ("$id", gid));

    public void DeleteTagGroup(long gid)
    {
        Exec("UPDATE tags SET group_id=NULL WHERE group_id=$id", ("$id", gid));
        Exec("UPDATE folders SET group_id=NULL WHERE group_id=$id", ("$id", gid));
        Exec("DELETE FROM tag_groups WHERE id=$id", ("$id", gid));
    }

    public void SetTagGroup(long tagId, long? groupId) =>
        Exec("UPDATE tags SET group_id=$g WHERE id=$id", ("$g", groupId), ("$id", tagId));

    public long? GetTagGroup(long tagId)
    {
        lock (_lock)
        {
            using var reader = Query("SELECT group_id FROM tags WHERE id=$id", ("$id", tagId));
            if (!reader.Read()) return null;
            return reader.IsDBNull(0) ? null : reader.GetInt64(0);
        }
    }

    public long? GetTagGroupByName(string name)
    {
        lock (_lock)
        {
            using var reader = Query("SELECT id FROM tag_groups WHERE name=$n", ("$n", name));
            return reader.Read() ? reader.GetInt64(0) : null;
        }
    }

    public long? GetOrCreateTagGroup(string name) =>
        GetTagGroupByName(name) ?? AddTagGroup(name);

    public void ToggleGroupCollapsed(long gid) =>
        Exec("UPDATE tag_groups SET collapsed=1-collapsed WHERE id=$id", ("$id", gid));

    public bool IsGroupCollapsed(long gid)
    {
        lock (_lock)
        {
            using var reader = Query("SELECT collapsed FROM tag_groups WHERE id=$id", ("$id", gid));
            return reader.Read() && !reader.IsDBNull(0) && reader.GetInt32(0) != 0;
        }
    }

    public void DeleteTag(long tid) => Exec("DELETE FROM tags WHERE id=$id", ("$id", tid));

    public void RenameTag(long tid, string name) =>
        Exec("UPDATE tags SET name=$n WHERE id=$id", ("$n", name), ("$id", tid));

    public void UpdateTagColor(long tid, string color) =>
        Exec("UPDATE tags SET color=$c WHERE id=$id", ("$c", color), ("$id", tid));

    /// <summary>タグごとのファイルグリッド表示形状（"square"/"portrait"/"landscape"）を更新する。</summary>
    public void UpdateTagGridShape(long tid, string shape) =>
        Exec("UPDATE tags SET grid_shape=$s WHERE id=$id", ("$s", shape), ("$id", tid));

    public List<TagRecord> GetFileTags(long fid)
    {
        lock (_lock)
        {
            using var reader = Query("""
                SELECT t.id, t.name, t.color, t.group_id, t.sort_order FROM tags t
                JOIN file_tags ft ON t.id=ft.tag_id
                WHERE ft.file_id=$id
                """, ("$id", fid));
            var result = new List<TagRecord>();
            while (reader.Read())
            {
                result.Add(new TagRecord
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Color = reader.IsDBNull(2) ? "#888888" : reader.GetString(2),
                    GroupId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    SortOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                });
            }
            return result;
        }
    }

    /// <summary>複数ファイルIDに対するタグを一括取得。file_id -> タグ一覧。
    /// 「すべてのファイル」等で件数が非常に多い場合、1クエリに全IDをバインド変数として
    /// 詰め込むとSQLiteのバインド変数上限（環境によっては999個程度）を超えて例外になり得るため、
    /// 安全な塊(chunkSize)に分割して複数回に分けて問い合わせ、結果をマージする。</summary>
    public Dictionary<long, List<TagRecord>> GetTagsForFiles(IReadOnlyList<long> fids, int chunkSize = 400)
    {
        var result = new Dictionary<long, List<TagRecord>>();
        if (fids.Count == 0) return result;

        for (var offset = 0; offset < fids.Count; offset += chunkSize)
        {
            var chunk = fids.Skip(offset).Take(chunkSize).ToList();
            var ph = string.Join(",", chunk.Select((_, i) => $"$f{i}"));
            var sql = $"""
                SELECT ft.file_id, t.id, t.name, t.color, t.group_id, t.sort_order FROM tags t
                JOIN file_tags ft ON t.id=ft.tag_id
                WHERE ft.file_id IN ({ph})
                """;
            var ps = chunk.Select((f, i) => ($"$f{i}", (object?)f)).ToArray();

            lock (_lock)
            {
                using var reader = Query(sql, ps);
                while (reader.Read())
                {
                    var fileId = reader.GetInt64(0);
                    var tag = new TagRecord
                    {
                        Id = reader.GetInt64(1),
                        Name = reader.GetString(2),
                        Color = reader.IsDBNull(3) ? "#888888" : reader.GetString(3),
                        GroupId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    };
                    if (!result.TryGetValue(fileId, out var list))
                        result[fileId] = list = new List<TagRecord>();
                    list.Add(tag);
                }
            }
        }
        return result;
    }

    public void AddFileTag(long fid, long tid)
    {
        try { Exec("INSERT OR IGNORE INTO file_tags(file_id,tag_id) VALUES($f,$t)", ("$f", fid), ("$t", tid)); }
        catch { /* Python版と同様、無視 */ }
    }

    public void RemoveFileTag(long fid, long tid) =>
        Exec("DELETE FROM file_tags WHERE file_id=$f AND tag_id=$t", ("$f", fid), ("$t", tid));

    public void AddRecent(long fid) =>
        Exec("INSERT INTO recent(file_id) VALUES($f)", ("$f", fid));

    public void Dispose() => _conn.Dispose();
}
