namespace TanukiTag.Models;

public class FileRecord
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Filename { get; set; }
    public int Star { get; set; }
    public string Comment { get; set; } = "";
    public string AddedAt { get; set; } = "";
    public string AccessedAt { get; set; } = "";
    public int OpenCount { get; set; }

    /// <summary>「フォルダを開く」表示中、サブフォルダを表すエントリかどうか。
    /// フォルダのエントリはDBに登録されず、サムネイル生成の対象にもならない。</summary>
    public bool IsFolder { get; set; }
}

public class TagRecord
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#888888";
    public long? GroupId { get; set; }
    public int SortOrder { get; set; }
    public int FileCount { get; set; }
    /// <summary>このタグを表示したときのファイルグリッド形状。"square"（正方形）/"portrait"（コミック向け縦長）
    /// /"landscape"（動画向け横長）のいずれか。既定は"square"。</summary>
    public string GridShape { get; set; } = "square";
}

public class TagGroupRecord
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public bool Collapsed { get; set; }
}

/// <summary>左のタグリストへドラッグ&ドロップで登録した「フォルダ」エントリ。
/// タグと違い、ファイルにタグ付けするものではなく、右のファイルグリッドへ
/// フォルダの中身を表示するためのショートカットとして機能する。</summary>
public class FolderRecord
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    /// <summary>タグと同じくグループへ分類できるようにするためのグループID。未分類の場合null。</summary>
    public long? GroupId { get; set; }
}
