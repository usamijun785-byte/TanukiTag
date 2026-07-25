using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TanukiTag.Services;

/// <summary>
/// タグリスト（左）とファイルグリッド（右）でフォントサイズを別々に持たせるための
/// シングルトン。DataTemplate側から x:Bind local:UiSettings.Instance.XxxFontSize で
/// 直接参照させることで、設定変更時に ItemsSource の張り直し無しにライブ反映される。
/// </summary>
public sealed class UiSettings : INotifyPropertyChanged
{
    public static UiSettings Instance { get; } = new();
    private UiSettings() { }

    private double _tagListFontSize = 11; // Python版 DEFAULT_CONFIG["tag_font_size"] と同じ既定値
    public double TagListFontSize
    {
        get => _tagListFontSize;
        set { _tagListFontSize = value; OnPropertyChanged(); }
    }

    private double _fileListFontSize = 13; // Python版 DEFAULT_CONFIG["font_size"] と同じ既定値
    public double FileListFontSize
    {
        get => _fileListFontSize;
        set { _fileListFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>ファイル名の下に出すタグ一覧の文字サイズ。以前はFileListFontSizeから自動計算していたが、
    /// ファイル名の文字サイズと切り離して独立に調整できるようにした。
    /// スライダーではなく「小・中・大」の3段階のみ（AppConfig.TagChipSizeOptions参照）。</summary>
    private double _fileListTagsFontSize = 11;
    public double FileListTagsFontSize
    {
        get => _fileListTagsFontSize;
        set { _fileListTagsFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>タグチップの表示文字数上限（全角換算）。タグチップサイズ設定「小」は7文字、
    /// 「中」「大」は4文字。SetItemTagsが静的メソッドのため、_configの代わりにここで保持する。</summary>
    private int _tagChipMaxChars = 4;
    public int TagChipMaxChars
    {
        get => _tagChipMaxChars;
        set { _tagChipMaxChars = value; OnPropertyChanged(); }
    }

    /// <summary>タグチップサイズ設定が「小」の間だけtrue。SetItemTags/SelectFittingTagsが
    /// 2行分のタグを選出してTags2にも入れるかどうかの判定に使う。</summary>
    private bool _tagChipTwoRows;
    public bool TagChipTwoRows
    {
        get => _tagChipTwoRows;
        set { _tagChipTwoRows = value; OnPropertyChanged(); }
    }

    /// <summary>タグチップ表示エリア（Grid.Row=2）の確保高さ（px）。通常は1行分、
    /// タグチップサイズ「小」の時だけ2行分になる。以前は固定24pxだったが、
    /// 「小」設定時に1行では下に隙間が余るため、2行表示ぶんの高さに切り替えられるようにした。</summary>
    private double _tagsAreaHeight = 24;
    public double TagsAreaHeight
    {
        get => _tagsAreaHeight;
        set { _tagsAreaHeight = value; OnPropertyChanged(); }
    }

    /// <summary>タグチップ表示エリア（Grid.Row=1）の上マージン。通常は0。
    /// 「2列表示」オプションがオンの時は負値になり、2行目の高さぶんをサムネイル画像側へ
    /// 重ねて表示させる（セルの行の高さ自体は1行分のまま増やさないため、ファイル名の位置は動かない）。</summary>
    private Microsoft.UI.Xaml.Thickness _tagsAreaOverlapMargin = new(0);
    public Microsoft.UI.Xaml.Thickness TagsAreaOverlapMargin
    {
        get => _tagsAreaOverlapMargin;
        set { _tagsAreaOverlapMargin = value; OnPropertyChanged(); }
    }

    // タグ名・ファイル名だけに適用するフォント。ボタンやラベル等のUIクロームには
    // 影響させない方針（WinUI標準コントロールへのFontFamily継承はコンテナ側の
    // 既定スタイルで途中で切られてしまい不安定なため、対象のTextBlockへ
    // x:Bindで直接指定する方式にしている）。
    private Microsoft.UI.Xaml.Media.FontFamily _nameFontFamily =
        new("Yu Gothic UI");
    public Microsoft.UI.Xaml.Media.FontFamily NameFontFamily
    {
        get => _nameFontFamily;
        set { _nameFontFamily = value; OnPropertyChanged(); }
    }

    /// <summary>NameFontFamilyと組で使うフォントの太さ。源真ゴシックPのように1つのファミリー名の中に
    /// 複数の太さ(ExtraLight〜Black)を持つフォントは、FontFamily名だけを変えても常にRegular相当の
    /// 見た目のままになるため、太さを別プロパティとして明示的に指定できるようにしている。</summary>
    private Windows.UI.Text.FontWeight _nameFontWeight = Microsoft.UI.Text.FontWeights.Normal;
    public Windows.UI.Text.FontWeight NameFontWeight
    {
        get => _nameFontWeight;
        set { _nameFontWeight = value; OnPropertyChanged(); }
    }

    // タグ名（サイドバーのタグ一覧・ファイル下のタグチップ）専用のフォント。
    // NameFontFamily/NameFontWeight（ファイル名用）とは独立に切り替えられる。
    private Microsoft.UI.Xaml.Media.FontFamily _tagFontFamily =
        new("Yu Gothic UI");
    public Microsoft.UI.Xaml.Media.FontFamily TagFontFamily
    {
        get => _tagFontFamily;
        set { _tagFontFamily = value; OnPropertyChanged(); }
    }

    /// <summary>TagFontFamilyと組で使うフォントの太さ。NameFontWeightと同じ理由で必要。</summary>
    private Windows.UI.Text.FontWeight _tagFontWeight = Microsoft.UI.Text.FontWeights.Normal;
    public Windows.UI.Text.FontWeight TagFontWeight
    {
        get => _tagFontWeight;
        set { _tagFontWeight = value; OnPropertyChanged(); }
    }

    /// <summary>サムネイルグリッドのセル表示サイズ（正方形の一辺、px）。
    /// トップバーのスライダーでライブに変更できる。デコード解像度(AppConfig.ThumbSize＝「サムネイル解像度」)
    /// とは別物で、こちらは純粋に見た目のセルサイズ（GridViewのレイアウト）のみに影響する。</summary>
    private double _thumbCellSize = 160; // AppConfig.ThumbGridCellSize と同じ既定値
    public double ThumbCellSize
    {
        get => _thumbCellSize;
        set
        {
            _thumbCellSize = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbCellWidth));
            OnPropertyChanged(nameof(ThumbCellHeight));
        }
    }

    /// <summary>現在表示中のグリッドの形状。"square"=正方形（既定）、"portrait"=コミック向け縦長、
    /// "landscape"=動画向け横長。タグ選択時にそのタグへ設定された形状へ切り替わる。</summary>
    private string _gridShape = "square";
    public string GridShape
    {
        get => _gridShape;
        set
        {
            if (_gridShape == value) return;
            _gridShape = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbCellWidth));
            OnPropertyChanged(nameof(ThumbCellHeight));
        }
    }

    /// <summary>各形状の縦横比（幅:高さ）。コミックは3:4、動画は16:9を採用。</summary>
    public static readonly IReadOnlyDictionary<string, (double W, double H)> GridShapeRatios =
        new Dictionary<string, (double, double)>
        {
            ["square"] = (1.0, 1.0),
            ["portrait"] = (3.0, 4.0),
            ["landscape"] = (16.0, 9.0),
        };

    /// <summary>実際にセルへ適用する幅（px）。ThumbCellSizeを基準の「長辺」とみなし、
    /// 縦横比に応じて短辺を縮める形で計算する（正方形時は従来どおりThumbCellSizeそのもの）。</summary>
    public double ThumbCellWidth
    {
        get
        {
            var (w, h) = GridShapeRatios.TryGetValue(_gridShape, out var r) ? r : (1.0, 1.0);
            return w >= h ? _thumbCellSize : _thumbCellSize * (w / h);
        }
    }

    /// <summary>実際にセルへ適用する高さ（px）。計算方法はThumbCellWidthと対称。</summary>
    public double ThumbCellHeight
    {
        get
        {
            var (w, h) = GridShapeRatios.TryGetValue(_gridShape, out var r) ? r : (1.0, 1.0);
            return h >= w ? _thumbCellSize : _thumbCellSize * (h / w);
        }
    }

    /// <summary>サムネ右上のスター評価表示のオン/オフ。AppConfig.ShowStarから起動時・設定変更時に反映される。
    /// 既定はオン（常に表示）。</summary>
    private bool _showStar = true;
    public bool ShowStar
    {
        get => _showStar;
        set { _showStar = value; OnPropertyChanged(); }
    }

    /// <summary>「開いた回数」でソートしている間だけサムネ右上に開いた回数バッジを出すためのフラグ。
    /// ソート条件変更時にMainWindow側からセットする。</summary>
    private bool _showOpenCountBadge;
    public bool ShowOpenCountBadge
    {
        get => _showOpenCountBadge;
        set { _showOpenCountBadge = value; OnPropertyChanged(); }
    }

    /// <summary>グリッドのセル同士の間隔（Thickness、全辺同値）。DataTemplate側のセルMarginへ
    /// x:Bindで直接反映する。実際の隙間は隣接セル同士のMarginが合わさるため、この値のさらに2倍になる。
    /// 「広い・普通・狭い」の3段階のみ（AppConfig.GridSpacingOptions参照）。既定は"普通"=4px
    /// （この設定を導入する以前から固定で使われていた値）。</summary>
    private Microsoft.UI.Xaml.Thickness _gridItemMargin = new(4);
    public Microsoft.UI.Xaml.Thickness GridItemMargin
    {
        get => _gridItemMargin;
        set { _gridItemMargin = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
