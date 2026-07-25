using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TanukiTag.Models;

/// <summary>グリッドセル下部に表示するタグ1件分の見た目情報（名前＋タグの色）。
/// 多くのタグが付いていると横に収まりきらないため、Nameは元の名前を保持しつつ、
/// 表示には切り詰めたDisplayNameを使う（切り詰め文字数はタグチップサイズ設定や
/// タグが1件のみかどうかによって変わるため、SetItemTags側で算出して渡す）。</summary>
public class TagChip
{
    public required string Name { get; init; }

    /// <summary>チップに実際に表示する短縮名。SetItemTags側で算出済みのものをそのまま使う。</summary>
    public required string DisplayName { get; init; }

    public required SolidColorBrush Background { get; init; }
    public required SolidColorBrush Foreground { get; init; }

    /// <summary>直前のチップとの間隔（左マージン）。SetItemTags側で、セル幅に対して
    /// 余裕があれば均等配置になるよう正の値に、収まりきらない場合のみ必要な分だけ
    /// 負の値（重なり）になるよう計算される。先頭チップは常に0。</summary>
    public Thickness Margin { get; init; } = new Thickness(0);
}

/// <summary>
/// グリッドの1セルに対応するファイル情報。
/// Thumbnail は最初 null（プレースホルダー表示）で、
/// GridView のコンテナが実際に画面に現れたタイミングで非同期にセットする。
/// タグ付け・お気に入り切替でセルの見た目を即時更新できるよう INotifyPropertyChanged を実装。
/// </summary>
public class FileItem : INotifyPropertyChanged
{
    public long Id { get; set; }

    private string _path = "";
    /// <summary>ファイル名変更（リネーム）時にx:Bindへ即座に反映できるよう通知プロパティにしている。</summary>
    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(); }
    }

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    private string _extension = "";
    public string Extension
    {
        get => _extension;
        set
        {
            _extension = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsZip));
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(IsOtherFile));
        }
    }

    public bool IsZip => Extension is ".zip" or ".cbz" or ".cbr" or ".7z" or ".rar";
    public bool IsVideo => TanukiTag.Services.ThumbnailGenerator.VideoExts.Contains(Extension);

    /// <summary>「フォルダを開く」表示中のサブフォルダを表すエントリかどうか。</summary>
    public bool IsFolder { get; set; }

    /// <summary>サムネイル生成の対象外（画像・動画・アーカイブのいずれでもない）ファイルかどうか。
    /// 「フォルダを開く」で画像/動画以外の一般ファイルも一覧表示できるようにした際、
    /// サムネの代わりに汎用ファイルアイコンを出すための判定に使う。
    /// フォルダは専用のフォルダアイコン（IsFolder側）で表示するため、ここでは除外する。</summary>
    public bool IsOtherFile =>
        !IsFolder &&
        !TanukiTag.Services.ThumbnailGenerator.ImageExts.Contains(Extension) &&
        !IsVideo && !IsZip;

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    private int _openCount;
    /// <summary>開いた回数（「開いた回数」ソート時のみサムネ右上に表示する）</summary>
    public int OpenCount
    {
        get => _openCount;
        set { _openCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(OpenCountGlyph)); }
    }

    /// <summary>バッジ表示用テキスト（例: "12"）</summary>
    public string OpenCountGlyph => OpenCount.ToString();

    private int _star;
    /// <summary>0〜5のスター評価（Python版と同じ。0=未評価）</summary>
    public int Star
    {
        get => _star;
        set { _star = value; OnPropertyChanged(); OnPropertyChanged(nameof(StarGlyph)); }
    }

    /// <summary>お気に入り表示用グリフ。未評価なら非表示、それ以外は★の数だけ表示</summary>
    public string StarGlyph => Star > 0 ? new string('\u2605', Star) : "";

    /// <summary>セル下部に表示する色付きタグチップ一覧（Python版と同じくタグの下地が色付き）。
    /// コレクション自体は差し替えず中身をClear/Addすることで、x:Bind先の
    /// ItemsControlが自動的に再描画される（ObservableCollectionの通知による）。</summary>
    public ObservableCollection<TagChip> Tags { get; } = new();

    /// <summary>タグチップサイズ設定が「小」の時だけ使う2行目。それ以外は常に空のままにする
    /// ことで、XAML側のStackPanelが高さ0となり1行表示のまま隙間も生まれない。</summary>
    public ObservableCollection<TagChip> Tags2 { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
