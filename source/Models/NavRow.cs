using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FontWeight = Windows.UI.Text.FontWeight;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace TanukiTag.Models;

/// <summary>
/// 左サイドバーのナビゲーション1行分（グループヘッダー or タグ）を表す。
/// Python版の _nav_items ( (kind, item_id, label) のタプルリスト) に相当。
/// WinUI3のListViewは1つのDataTemplateで複数種別を出し分けるのが面倒なため、
/// グループヘッダー用・タグ用の見た目の出し分けは全部このクラス生成時にプロパティとして
/// 確定させてしまい、XAML側は単純なx:Bindだけで済むようにしている。
/// </summary>
public sealed class NavRow
{
    /// <summary>true: グループヘッダー行 / false: タグ行</summary>
    public bool IsGroupHeader { get; set; }

    /// <summary>「（未分類）」ヘッダー行かどうか（グループ操作の対象外にするため区別する）</summary>
    public bool IsUngroupedHeader { get; set; }

    /// <summary>グループヘッダー行の場合のグループID</summary>
    public long? GroupId { get; set; }

    /// <summary>タグ行の場合のタグID</summary>
    public long TagId { get; set; }

    /// <summary>true: フォルダ行（ドラッグ登録したフォルダショートカット）</summary>
    public bool IsFolder { get; set; }

    /// <summary>フォルダ行の場合のフォルダID・実パス</summary>
    public long FolderId { get; set; }
    public string FolderPath { get; set; } = "";

    /// <summary>タグ行の場合のタグ名（表示用ラベルは件数付きのLabel、こちらは名前のみ）</summary>
    public string Name { get; set; } = "";

    /// <summary>タグ行の場合、そのタグ選択時に適用するファイルグリッド形状
    /// （"square"/"portrait"/"landscape"）。</summary>
    public string GridShape { get; set; } = "square";

    /// <summary>表示ラベル（タグ行はタグ名のみ、グループ行はグループ名）</summary>
    public string Label { get; set; } = "";

    /// <summary>タグ行の件数チップに表示する数字（件数）。タグ行以外は空文字。</summary>
    public string CountText { get; set; } = "";

    /// <summary>件数チップの表示/非表示（タグ行のみ表示）。</summary>
    public Visibility CountVisibility { get; set; } = Visibility.Collapsed;

    public SolidColorBrush ColorBrush { get; set; } = new(Microsoft.UI.Colors.Gray);

    public Thickness Indent { get; set; } = new Thickness(0);

    /// <summary>グループヘッダー行の折りたたみ状態を表す▼/▶</summary>
    public string CollapseGlyph { get; set; } = "";

    public Visibility GlyphVisibility { get; set; } = Visibility.Collapsed;
    public Visibility DotVisibility { get; set; } = Visibility.Visible;
    /// <summary>フォルダ行の先頭に出すフォルダアイコン（📁）の表示/非表示。
    /// フォルダ行では色ドット(DotVisibility)の代わりにこちらを表示する。</summary>
    public Visibility FolderGlyphVisibility { get; set; } = Visibility.Collapsed;
    public FontWeight LabelWeight { get; set; } = FontWeights.Normal;
}
