using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TanukiTag.Models;
using TanukiTag.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT.Interop;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace TanukiTag;

/// <summary>タグ一覧サイドバー表示用（DBのTagRecordをUI表示向けに整形したもの）</summary>
public class TagDisplayItem
{
    public long Id { get; set; }
    public string Label { get; set; } = "";   // "タグ名 (件数)"
    public SolidColorBrush ColorBrush { get; set; } = new(Microsoft.UI.Colors.Gray);
}

public sealed partial class MainWindow : Window
{
    // タイトルバー（DWMが描画する非クライアント領域）をテーマに合わせて強制的に
    // ダーク/ライト化するためのP/Invoke。AppWindow.TitleBarの色指定だけでは
    // 環境によってタイトルバー帯自体の色が変わらないことがあるため、こちらも併用する。
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>タイトルバーの非クライアント領域をダーク/ライトに切り替える。</summary>
    private void SetTitleBarDarkMode(bool isDark)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            int useDark = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch { /* 古いWindowsビルド等で失敗しても起動は継続する */ }
    }

    // 設定で変更可能なので const ではなくフィールドにする
    private int _thumbSize = 320;

    private readonly ObservableCollection<FileItem> _items = new();

    /// <summary>ファイル一覧が空の時だけ、ドラッグ＆ドロップでフォルダを開く操作・
    /// ファイルを開くとタグ付けできる旨のガイド文言(EmptyStateHint)を表示する。
    /// _items.CollectionChangedから呼ばれるほか、フォルダ切り替え直後にも明示的に呼ぶ。</summary>
    private void UpdateEmptyStateHint()
    {
        EmptyStateHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>左サイドバーのタグ一覧（グループヘッダー＋タグ行が混在する）。
    /// Python版の _nav_items 相当。</summary>
    public ObservableCollection<NavRow> NavRows { get; } = new();

    // ── ファイル検索（Python版 search_var / _on_search_change 相当） ─────────
    // 検索ボックスが空のときに表示すべき一覧（現在選択中のフィルタ/タグ/フォルダ）を
    // 遅延評価で保持しておき、検索語をクリアしたときにそのまま復元できるようにする。
    private Func<List<FileRecord>> _currentViewQuery = () => new List<FileRecord>();
    private string _currentViewLabel = "";

    // ── フォルダ移動履歴（戻る/進む/上へボタン用） ──
    /// <summary>現在フォルダ表示中の場合のそのパス。タグ/フィルタ表示中はnull。</summary>
    private string? _currentFolderPath;

    /// <summary>次にOpenFolderPathIntoGridが呼ばれた際にキャッシュを許可するかどうかの
    /// 「予約」フラグ。タグリストの登録フォルダ行を開く直前にだけ true にする。
    /// OpenFolderPathIntoGrid側で読み取った直後に false へ戻すため、そこから先の
    /// サブフォルダ移動（ダブルクリック/戻る/進む/上へ等）では明示的にセットし直さない限り
    /// 自動的にキャッシュ無効になる。</summary>
    private bool _folderCacheEnabled = false;

    /// <summary>現在フォルダ表示中の内容について、実際にサムネイルキャッシュへ
    /// 保存/参照してよいかどうか。OpenFolderPathIntoGridの呼び出し1回ごとに、
    /// その時点の_folderCacheEnabledの値で確定させる（登録フォルダ行を開いた
    /// その1回の表示だけがtrueになり、そこから移動すると次のOpenFolderPathIntoGrid
    /// 呼び出しでfalseに確定し直される）。</summary>
    private bool _currentFolderCacheAllowed = true;

    /// <summary>今表示中の内容についてサムネイルキャッシュを使ってよいか。
    /// フォルダ表示中でなければ（タグ/フィルタ表示中は）常にキャッシュを使う。
    /// フォルダ表示中は _currentFolderCacheAllowed に従う。</summary>
    private bool ThumbnailCacheAllowed => _currentFolderPath == null || _currentFolderCacheAllowed;
    private readonly Stack<string> _folderBackStack = new();
    private readonly Stack<string> _folderForwardStack = new();
    /// <summary>OpenFolderPathIntoGrid内でFilterListView.SelectedIndexを0に戻す際、
    /// それが同期的にFilterListView_SelectionChangedを発火させ、そのハンドラが
    /// _currentFolderPath/フォルダ履歴スタックをクリアしてしまう（＝タグリストからフォルダを
    /// 選んだ直後に戻る/進む/上へボタンが効かなくなる）のを防ぐためのガード。</summary>
    private bool _suppressFolderNavReset;
    // 現在ファイルリストに表示中のタグ（タグ一覧で選択中のタグ）。
    // フィルタ（すべて/未タグ/お気に入り/最近開いた）表示中や未選択時はnull。
    private long? _currentTagId;

    /// <summary>左上のフィルタ一覧（すべて/よく使うファイル/スター/最近開いたファイル）で
    /// 現在選択中のTag文字列。「よく使うファイル」表示中は開いた回数バッジを自動表示するために使う。
    /// タグクリック時やフォルダを開いた時はnullに戻す。</summary>
    private string? _currentFilterKey;

    // 起動直後、コンストラクタ内で FilterListView.SelectedIndex = 0 等を設定した際に
    // SelectionChanged が同期発火してファイル一覧の自動読み込みが起きるのを防ぐためのフラグ。
    // 初期化完了後（コンストラクタの最後）に false へ戻す。
    private bool _isInitializing = true;
    /// <summary>タグ切り替え時のサムネ先読み用。切替のたびに増やし、待機中に別の切替が
    /// 割り込んだら古い方の処理を後から実行しないようにするための世代カウンタ。</summary>
    private int _viewSwitchGeneration = 0;

    // ── ラバーバンド選択（Python版 _on_drag の空白ドラッグ選択相当） ─────────
    private Windows.Foundation.Point? _rbStartViewport; // ドラッグ開始点（ビューポート座標、矩形の描画用）
    private double _rbStartScrollOffset;   // ドラッグ開始時点のVerticalOffset（オートスクロール時に開始点を補正するため）
    private bool _rbActive;          // 閾値を超えて実際にドラッグ選択が始まったか
    private DispatcherTimer? _rbAutoScrollTimer;   // ドラッグ選択中、端に近づいた時の自動スクロール用
    private DispatcherTimer? _tagAutoScrollTimer;  // タグ一覧へファイルをドラッグ中、端に近づいた時の自動スクロール用
    private Windows.Foundation.Point? _tagLastPointerPos; // ↑ 直近のポインタ位置（ウィンドウ座標）
    private ScrollViewer? _tagScrollViewer;        // TagListView内部のScrollViewer（遅延取得）
    private Windows.Foundation.Point? _rbLastPointerPos; // 自動スクロール中も参照する最新のポインタ位置（ビューポート座標）
    private const double CellWidth = 168;   // GridView.ItemTemplateの実サイズ(160)+Margin(4*2)
    private const double CellHeight = 220;  // 同上(212+4*2)
    private const double GridContentPadding = 8; // GridViewのPadding

    // ドラッグ視覚（既定ではセルのスクリーンショット＝サムネイル画像が表示される）を
    // 「+N件」のキャプションのみに差し替えるため、コンテナごとにDragStartingを一度だけフックする。
    private readonly HashSet<GridViewItem> _dragStartingHooked = new();

    private readonly ThumbnailCache _thumbCache;
    /// <summary>設定ダイアログが開いている間trueにするフラグ。SettingsButton_Clickはasync voidで、
    /// ダイアログ構築（フォント列挙等）に時間がかかる間にボタンを連打されると、
    /// ShowAsync()が同時に複数回呼ばれて「Only a single ContentDialog can be open at any time」
    /// の未処理例外でアプリごと落ちていた。このフラグで多重起動を防ぐ。</summary>
    private bool _settingsDialogOpen;
    private readonly AppDatabase _db;
    private AppConfig _config = new();

    /// <summary>FolderPicker/FileOpenPickerが開いている間trueにする共有フラグ。
    /// Windowsのシェル選択ダイアログ(COM)は同一プロセス内で多重に呼び出すと不安定になり、
    /// 「選択」ボタンが灰色のまま押せなくなったり、フォルダを選んでも結果が反映されない
    /// （＝ファイルグリッドに何も表示されない）現象の原因になる。
    /// ボタン連打や、別のダイアログ内の「選択...」ボタンとの同時押しでpickerが多重起動
    /// されないよう、ここで直列化する。</summary>
    private bool _filePickerOpen;

    // サムネイル正方形の余白合成色。テーマ切り替えで変わるので readonly ではなくする。
    private Color _bgColor = Color.FromArgb(255, 0x3A, 0x3A, 0x3A);

    // 現在フォルダ内のファイルの DB レコード（path -> FileRecord）。
    // フォルダを開いた直後の一覧表示、タグ/星の初期反映に使う。
    private Dictionary<string, FileRecord> _currentFolderRecords = new();

    private readonly Dictionary<GridViewItem, CancellationTokenSource> _loadTokens = new();

    // 一気にスクロールした際、200個近いアイコン取得(GetThumbnailAsync)が同時に走って
    // シェルAPI呼び出しが殺到し不安定になるのを防ぐための同時実行数制限。
    // 一時期CPUコア数に応じて可変にしていたが、画像の多いフォルダを高速スクロールすると
    // カクつく現象が報告されたため、固定の4に戻す。
    private readonly SemaphoreSlim _thumbLoadSemaphore = new(1);

    // 現在グリッドに表示中の元レコード（並替コンボボックス変更時に再クエリ無しで並べ替え直すため保持）
    private List<FileRecord> _lastRecords = new();

    /// <summary>直近にグリッドへ読み込んだ各ファイルの全タグ（表示幅で間引く前の完全なリスト）。
    /// サムネ表示サイズ変更時、DBへ再問い合わせせずにチップの再選定（間引き直し）だけを
    /// 行えるようにするためのキャッシュ。file.Id をキーとする。</summary>
    private Dictionary<long, List<TagRecord>> _itemTagsCache = new();

    /// <summary>表示サイズスライダーのドラッグ中に毎ピクセルでタグチップを再計算すると重いため、
    /// 操作が止まってから少し待ってまとめて再計算するデバウンス用タイマー。</summary>
    private DispatcherTimer? _tagChipResizeTimer;

    // Python版 self._sort_labels 相当（表示ラベル ⇄ 内部キー）
    private static readonly (string Key, string Label)[] SortOptions =
    {
        ("accessed",   "最終アクセス日"),
        ("win_mtime",  "最終更新"),
        ("added",      "追加日"),
        ("name",       "名前↑"),
        ("name_desc",  "名前↓"),
        ("star",       "スター"),
        ("open_count", "開いた回数"),
        ("size",       "サイズ"),
        ("duration",   "再生時間"),
    };

    public MainWindow()
    {
        // ★ 重要: InitializeComponent()より前に_configとUiSettings.Instance.ThumbCellSizeを
        // 確定させておく。ThumbGridSizeSliderはXAML側で
        // Value="{x:Bind services:UiSettings.Instance.ThumbCellSize, Mode=TwoWay}" と
        // バインドされており、InitializeComponent()内でのx:Bind初期評価時に
        // ThumbGridSizeSlider_ValueChangedが同期発火する。
        // このタイミングで_configがまだ既定値（フィールド初期化子のnew AppConfig()）のままだと、
        // ハンドラ内のif (_config.ThumbGridCellSize == e.NewValue) return; を素通りして
        // 「まだ何も読み込んでいない既定値の_config」がSave()でファイルへ書き込まれてしまい、
        // 直前にユーザーが保存したTheme/TagListFontSize/FileListFontSize等が
        // 次回起動時に既定値へ巻き戻る、という不具合の原因になっていた。
        _config = AppConfig.Load();
        UiSettings.Instance.ThumbCellSize = _config.ThumbGridCellSize;

        InitializeComponent();

        // タスクバー/タイトルバーのアイコン。ApplicationIcon(csproj)はexeファイル自体の
        // アイコン(エクスプローラー上の表示)には効くが、非パッケージ化WinUI3アプリでは
        // 実行中ウィンドウのタスクバー/タイトルバーアイコンには別途AppWindow.SetIconが必要。
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tag.ico"));
        }
        catch { /* アイコン未配置でも起動は継続する */ }

        // タイトルバーの表示名
        Title = "TanukiTag";
        AppWindow.Title = "TanukiTag";

        // DB/サムネイルキャッシュ等のアプリデータ一式は、USBメモリ等に入れて持ち運べる
        // ポータブル運用をしやすくするため、%LocalAppData%ではなくexe（実行ファイル）と
        // 同じ場所の"Settings"フォルダにまとめて設置する（設定ファイルAppConfigと同じ場所）。
        var appDataDir = Path.Combine(AppContext.BaseDirectory, "Settings");
        Directory.CreateDirectory(appDataDir);
        ThumbnailLog.Initialize(appDataDir);

        // ファイル一覧の空/非空でガイド文言(EmptyStateHint)の表示を切り替える。
        _items.CollectionChanged += (_, _) => UpdateEmptyStateHint();
        UpdateEmptyStateHint();

        _thumbCache = new ThumbnailCache(Path.Combine(appDataDir, "thumb_cache.db"));
        _db = new AppDatabase(Path.Combine(appDataDir, "tagfiler.db"));

        _thumbSize = _config.ThumbSize;
        ApplyTheme(_config.Theme);
        ApplyNameFont(_config.UiFont, _config.UiFontWeight);
        ApplyTagFont(_config.TagFont, _config.TagFontWeight);
        UiSettings.Instance.TagListFontSize = _config.TagListFontSize;
        UiSettings.Instance.FileListFontSize = _config.FileListFontSize;
        UiSettings.Instance.FileListTagsFontSize = ResolveTagChipFontSize(_config.TagChipSize);
        UiSettings.Instance.TagChipMaxChars = ResolveTagChipMaxCharsFromLabel(_config.TagChipSize);
        ApplyTagChipRowSettings();
        UiSettings.Instance.GridItemMargin = new Microsoft.UI.Xaml.Thickness(ResolveGridSpacingMargin(_config.GridSpacing));
        UiSettings.Instance.ShowStar = _config.ShowStar;

        ThumbGridView.ItemsSource = _items;
        RefreshNavList();

        foreach (var (_, label) in SortOptions) SortComboBox.Items.Add(label);
        var curLabel = SortOptions.FirstOrDefault(o => o.Key == _config.SortKey).Label ?? SortOptions[0].Label;
        SortComboBox.SelectedItem = curLabel;

        // XAML側で IsSelected="True" を使うと InitializeComponent() 実行中に
        // SelectionChanged が同期発火し、_db 初期化前に呼ばれてクラッシュするため、
        // 初期選択は _db 準備完了後にここで行う。
        // ただし _isInitializing が true の間は SelectionChanged 側でファイル一覧の
        // 読み込み（RefreshFilesView）をスキップするため、ここで選択しても
        // 起動直後の画面には何も表示されない（空欄のまま）。
        FilterListView.SelectedIndex = 0;

        // 初期化完了。以降のユーザー操作による SelectionChanged は通常どおり
        // ファイル一覧を読み込む。
        _isInitializing = false;
    }

    /// <summary>タグ・スター・コメントなど、DBへの保存が必要な操作を行う直前に呼び出す。
    /// フォルダ表示直後などまだDB未登録（Id==0）のファイルであれば、ここで初めてDBへ追加し、
    /// item.Idを実際のIDに差し替える。すでに登録済みなら何もしない。
    /// 「フォルダを開いただけでは登録しない。実際に何か（タグ付け等）を行ったときだけ登録する」
    /// という方針の要。</summary>
    private long EnsureRegistered(FileItem item)
    {
        if (item.Id > 0) return item.Id;
        var fid = _db.AddFile(item.Path);
        if (fid is { } id) item.Id = id;
        return item.Id;
    }

    // ── フォルダを開く ─────────────────────────
    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        // 連打や他のpicker（データ管理ダイアログの「選択...」等）との同時起動を防ぐ。
        // 多重起動するとシェルの選択ダイアログが不安定になり、「選択」ボタンが灰色のまま
        // 押せなくなったり、フォルダを選んでも結果が反映されない現象が起きていた。
        if (_filePickerOpen) return;
        _filePickerOpen = true;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            // SuggestedStartLocationは「このSettingsIdentifierでのピッカー利用が初回のときだけ」
            // 有効になる仕様で、2回目以降は前回ユーザーが実際に選んだ場所をOS側が記憶してそちらを
            // 優先してしまう（前回の修正でSuggestedStartLocationを指定しても直らなかったのはこれが原因）。
            // さらに、記憶された（あるいはプログラムから直接ジャンプした）フォルダを開いた直後は、
            // 中身が空だったりリスト側で何も選択されていなかったりすると「フォルダー:」欄が
            // 空のままになり、「フォルダーの選択」ボタンが押せない状態になる。
            // 対策として、毎回ランダムなSettingsIdentifierを割り当てて「常に初回」の状態にし、
            // SuggestedStartLocationを確実に優先させることで、記憶された不安定な場所へ飛ぶのを防ぐ。
            picker.SettingsIdentifier = Guid.NewGuid().ToString();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            // 「フォルダを開く」ボタン経由なので、この中身はサムネイルキャッシュを作らない。
            _folderCacheEnabled = false;
            OpenFolderPathIntoGrid(folder.Path);
        }
        finally
        {
            _filePickerOpen = false;
        }
    }

    /// <summary>フォルダ移動の種別。通常のクリック/ドロップ等での移動は新規履歴として積み、
    /// 戻る/進むボタンからの移動は履歴を操作しない。</summary>
    private enum FolderNavMode { Normal, Back, Forward }

    /// <summary>指定フォルダの中身を右のファイルグリッドに表示する。
    /// 「フォルダを開く」ボタンと、タグリストに登録したフォルダ行のクリックの両方から使う共通処理。
    /// フォルダの中身はDBへは登録せず（未登録ファイルはEnsureRegistered経由で操作時に初めて登録）、
    /// 既にDB登録済み（タグ付け等をした）ファイルはそのタグ・スター等の情報を引き継いで表示する。</summary>
    private void OpenFolderPathIntoGrid(string path, FolderNavMode navMode = FolderNavMode.Normal)
    {
        // この呼び出しで表示するフォルダについてキャッシュを使ってよいかをここで確定させる。
        // タグリストの登録フォルダ行を開いた直後の呼び出しだけ _folderCacheEnabled が true になっており、
        // 読み取ったら即座にfalseへ戻すので、ここから先へ移動（ダブルクリック/戻る/進む/上へ等）した際の
        // 次のOpenFolderPathIntoGrid呼び出しは、明示的に再設定しない限り自動的に無効側になる。
        _currentFolderCacheAllowed = _folderCacheEnabled;
        _folderCacheEnabled = false;

        // 戻る/進むボタン以外からの移動（通常のフォルダクリック・ドロップ・上へボタン等）は、
        // 直前のフォルダを「戻る」履歴に積み、「進む」履歴は破棄する
        // （ブラウザやエクスプローラーのアドレスバーと同じ挙動）。
        if (navMode == FolderNavMode.Normal)
        {
            if (_currentFolderPath != null && !string.Equals(_currentFolderPath, path, StringComparison.OrdinalIgnoreCase))
                _folderBackStack.Push(_currentFolderPath);
            _folderForwardStack.Clear();
        }
        else if (navMode == FolderNavMode.Back)
        {
            if (_currentFolderPath != null) _folderForwardStack.Push(_currentFolderPath);
        }
        else if (navMode == FolderNavMode.Forward)
        {
            if (_currentFolderPath != null) _folderBackStack.Push(_currentFolderPath);
        }
        _currentFolderPath = path;
        UpdateFolderNavButtons();

        // フィルタ選択を「すべて」に戻す。
        // ★ 重要: これは後段のLoadRecordsIntoGrid/_currentViewQueryより前に行うこと。
        // FilterListView.SelectedIndexを変更するとFilterListView_SelectionChangedが
        // 同期的に発火し、そのハンドラが_currentViewQueryを_db.GetAllFiles()へ、
        // グリッドの中身もDB登録済みファイル全体へ上書きしてしまう。
        // 以前はこの行を最後に置いていたため、せっかく読み込んだフォルダの中身
        // （特にDB未登録のファイル）がここで消され、「フォルダを開いても
        // 一部（DB登録済み分）しか表示・選択できない」不具合の原因になっていた。
        // さらに、このFilterListView_SelectionChangedは_currentFolderPath/フォルダ履歴
        // スタックまでクリアしてしまうため、_suppressFolderNavResetで一時的にガードし、
        // 直前にセットしたフォルダナビゲーション状態（戻る/進む/上へボタン）が
        // 巻き戻されないようにする。
        _suppressFolderNavReset = true;
        FilterListView.SelectedIndex = 0;
        _suppressFolderNavReset = false;
        // 上のFilterListView.SelectedIndex=0は_currentFilterKey等の内部状態をリセットする
        // ためだけの内部操作で、実際に「すべてのファイル」を選んだわけではない。
        // 選択したままにすると「すべてのファイル」の方に選択色が付いて見えてしまう
        // （タグリストのフォルダ行をクリックしたときの選択色がそちらへ飛んで見える不具合の原因）ため、
        // 内部状態の反映が済んだらここで選択を解除しておく。
        FilterListView.SelectedItem = null;
        // タグリスト側の選択もフォルダ行以外は解除しておく（タグとフォルダは排他）。
        if (TagListView.SelectedItem is NavRow selectedRow && !(selectedRow.IsFolder && selectedRow.FolderPath == path))
            TagListView.SelectedItem = null;

        // 「フォルダを開く」では画像・動画・アーカイブに限らず、フォルダ内の全ファイルを
        // 一覧表示する（画像/動画以外はサムネの代わりに汎用ファイルアイコンを表示：
        // FileItem.IsOtherFile / XAMLのFontIcon参照）。
        // タグリストに登録済みのフォルダが後から削除／移動されている場合、
        // Directory.EnumerateFilesは例外を投げる。ここで捕まえずに投げっぱなしにすると、
        // 直前のFilterListView.SelectedIndex=0の副作用で既に「すべてのファイル」がグリッドに
        // 反映された状態のまま処理が中断してしまい、「消えたフォルダを選ぶと全ファイルが
        // 表示される」ように見える不具合になっていた。フォルダが存在しない場合は
        // 空の一覧として扱い、必ず最後まで処理を進めて空のグリッドを明示的に表示する。
        var folderExists = Directory.Exists(path);
        var files = folderExists
            ? Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        // サブフォルダも一覧に表示する（エクスプローラーと同じくフォルダを先頭にまとめる）。
        var subfolders = folderExists
            ? Directory.EnumerateDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        if (!folderExists)
            TagActionStatusText.Text = "このフォルダは見つかりませんでした（削除または移動された可能性があります）。";

        // 「フォルダを開く」は右側のファイルグリッドにフォルダの中身を表示するだけで、
        // DBへは登録しない（以前は_db.AddFileを全件に呼んで無条件にリスト登録していたが、
        // 単に眺めただけのファイルまでライブラリに残ってしまうのは意図と異なる）。
        // 実際にタグ付け・スター評価・コメント編集など何か操作したファイルだけ、
        // その時点でEnsureRegistered()を通じて初めてDBへ追加される。
        // すでに登録済み（過去にタグ付け等をした）ファイルは、そのDB上の情報（タグ・スター等）を
        // そのまま引き継いで表示できるよう、パスが一致する既存レコードがあればそれを使う。
        // BuildFolderRecordsをその都度呼び直すことで、フォルダ表示中にタグ付けして
        // 新たにDB登録されたファイルの状態も再表示時に正しく反映される。
        List<FileRecord> BuildFolderRecords()
        {
            var existing = _db.GetAllFiles()
                .Where(r => files.Contains(r.Path, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
            _currentFolderRecords = existing;
            // 未登録ファイル(DB未登録)にはId=0を共通で振っていたため、フォルダ内に未登録ファイルが
            // 複数あるとFileItem.Idが重複し、ラバーバンド選択のId基準の選択済み判定
            // （SelectItemsInRect内のHashSet<long>）が衝突して、一部のファイルしか
            // 選択できない不具合の原因になっていた。DBの正のIdとぶつからないよう
            // 負の連番を振って、未登録ファイルにも一意なIdを持たせる。
            long unregisteredId = -1;
            var folderRecords = subfolders.Select(p => new FileRecord
            {
                Id = unregisteredId--,
                Path = p,
                Filename = System.IO.Path.GetFileName(p),
                IsFolder = true,
            });
            var fileRecords = files.Select(p => existing.TryGetValue(p, out var rec)
                ? rec
                : new FileRecord { Id = unregisteredId--, Path = p, Filename = Path.GetFileName(p) });
            return folderRecords.Concat(fileRecords).ToList();
        }

        BuildFolderRecords(); // _currentFolderRecords（既存レコードとの突き合わせ）を先に確定させておく
        UiSettings.Instance.GridShape = "square";

        // 検索語をクリアしたときに戻ってこられるよう、このフォルダの一覧を記録しておく
        _currentViewQuery = BuildFolderRecords;
        _currentViewLabel = path;

        // タグ切替時と同じく、表示前に裏でサムネ（特に動画）をキャッシュへ温めてから
        // グリッドを差し替える。以前はここで直接LoadRecordsIntoGridを呼んでいたため、
        // 動画が多いフォルダを開いた瞬間に可視セル分の動画サムネイル生成が
        // 一斉に走ってしまい、無言のネイティブクラッシュを起こすことがあった。
        RefreshFilesViewWithPrefetch();
    }

    /// <summary>戻る/進む/上へボタンの有効/無効を現在の状態に合わせて更新する。</summary>
    private void UpdateFolderNavButtons()
    {
        if (FolderBackButton == null) return; // XAML初期化前の保険
        FolderBackButton.IsEnabled = _folderBackStack.Count > 0;
        FolderForwardButton.IsEnabled = _folderForwardStack.Count > 0;
        FolderUpButton.IsEnabled = _currentFolderPath != null &&
            Directory.GetParent(_currentFolderPath) != null;
    }

    private void FolderBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folderBackStack.Count == 0) return;
        var target = _folderBackStack.Pop();
        OpenFolderPathIntoGrid(target, FolderNavMode.Back);
    }

    private void FolderForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folderForwardStack.Count == 0) return;
        var target = _folderForwardStack.Pop();
        OpenFolderPathIntoGrid(target, FolderNavMode.Forward);
    }

    private void FolderUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolderPath == null) return;
        var parent = Directory.GetParent(_currentFolderPath);
        if (parent == null) return;
        OpenFolderPathIntoGrid(parent.FullName);
    }

    /// <summary>現在グリッドに表示中のファイルをすべて選択する。</summary>
    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        ThumbGridView.SelectAll();
    }

    /// <summary>「すべて選択」の右にあるタグ付けボタン。選択状態に関わらず、現在グリッドに
    /// 表示されているファイル（開いているフォルダ、またはタグの中身）すべてを対象に、
    /// 右クリックメニューと同じ3種類のタグ付け操作（タグを付ける／ファイル名から自動タグ付け／
    /// 該当単語でタグ付け）をメニューから選んで実行できるようにする。「フォルダを開く」表示中の
    /// サブフォルダ行はタグ付け対象外のため除外する。</summary>
    private async void BulkTagButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = _items.Where(f => !f.IsFolder).ToList();
        if (targets.Count == 0)
        {
            TagActionStatusText.Text = "タグ付け対象のファイルがありません。";
            return;
        }

        var flyout = new MenuFlyout();

        var tagAssignItem = new MenuFlyoutItem { Text = $"🏷 タグを付ける（{targets.Count}件）..." };
        tagAssignItem.Click += async (_, _) => await ShowTagAssignPickerAsync(targets);
        flyout.Items.Add(tagAssignItem);

        var autoTagItem = new MenuFlyoutItem { Text = $"🏷 ファイル名から自動タグ付け（{targets.Count}件）..." };
        autoTagItem.Click += async (_, _) => await ShowAutoTagFromFilenameAsync(targets, folderPath: _currentFolderPath);
        flyout.Items.Add(autoTagItem);

        var tagByWordItem = new MenuFlyoutItem { Text = $"🏷 該当単語でタグ付け（{targets.Count}件）..." };
        tagByWordItem.Click += async (_, _) => await ShowTagByWordAsync(targets, folderPath: _currentFolderPath);
        flyout.Items.Add(tagByWordItem);

        flyout.ShowAt((Button)sender);
    }


    // ── フィルタ/タグ選択でグリッドを差し替え ─────────────────────────
    private void FilterListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_db == null) return; // 初期化完了前の予期しない発火を防ぐ保険
            if (FilterListView.SelectedItem is not ListViewItem lvi) return;
            // 通常はタグ選択と排他だが、フォルダを開く際にOpenFolderPathIntoGridが内部的に
            // フィルタを「すべて」へ一時的に戻すためだけにここへ来た場合（_suppressFolderNavReset）は、
            // タグリスト側の選択（開いたフォルダ行のハイライト）を巻き込んで消してはいけない。
            // これを無条件に消していたせいで、フォルダ行をクリックしたときの選択色の変化が
            // 実際にはこの「すべてのファイル」側へ付いてしまう不具合になっていた。
            if (TagListView != null && !_suppressFolderNavReset) TagListView.SelectedItem = null; // タグ選択と排他

            var tagKey = lvi.Tag as string;
            _currentTagId = null;
            _currentFilterKey = tagKey;
            if (!_suppressFolderNavReset)
            {
                _currentFolderPath = null; // フォルダ表示から抜けるので戻る/進む/上への対象ではなくなる
                _folderBackStack.Clear();
                _folderForwardStack.Clear();
                UpdateFolderNavButtons();
            }
            _currentViewQuery = tagKey switch
            {
                "untagged"    => () => _db.GetUntaggedFiles(),
                "starred"     => () => _db.GetStarredFiles(),
                "recent"      => () => _db.GetRecentFiles(),
                "most_opened" => () => _db.GetMostOpened(),
                _             => () => _db.GetAllFiles(),
            };
            _currentViewLabel = lvi.Content?.ToString() ?? "";
            // タグ選択で縦長/横長に切り替わっていた場合に備え、フィルタ側は常に正方形に戻す。
            // ★ 重要: ここでRefreshAllTagChipWidths()を呼んではいけない。
            // その時点でThumbGridViewにはまだ「切替前」の（時には数千件規模の）アイテム集合が
            // 生きたままぶら下がっており、そこへ GridShape変更（セル幅/高さのライブ再計算）と
            // タグチップの再選定を立て続けに叩き込むと、直後にRefreshFilesView()でどうせ
            // 全部作り直すにもかかわらず、GridViewの仮想化パネル（ネイティブ側）に
            // 過大な再レイアウト負荷がかかり、管理コードの例外にすらならない
            // （crash.logにも残らない）無言のクラッシュを引き起こすことがあった。
            // 新しいセル幅/形状は、この後のRefreshFilesView()→LoadRecordsIntoGrid()が
            // 新しいアイテムをSetItemTagsで生成する時点で自動的に反映されるため、
            // ここで旧アイテム集合に対して先読みで反映する必要はない。
            UiSettings.Instance.GridShape = "square";
            if (_isInitializing) return; // 起動直後は一覧を読み込まず空欄のままにする
            RefreshFilesView();
        }
        catch (Exception ex)
        {
            // 「すべてのファイル」等からタグへ切り替える際に原因不明の落ちが報告されていたための対策。
            // ここで捕まえてログに残すことで、アプリごと落ちるのを防ぎつつ原因調査ができるようにする。
            App.LogCrash(ex);
            if (TagActionStatusText != null)
                TagActionStatusText.Text = $"表示の切り替えでエラーが発生しました（ログに記録しました）: {ex.Message}";
        }
    }

    private void TagListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_db == null) return; // 初期化完了前の予期しない発火を防ぐ保険

            // Ctrl/Shiftで複数選択できるようにしたため、SelectedItem（先頭の1件）ではなく
            // SelectedItems全体からグループヘッダーを除いた「実際に意味のある行」を見る。
            var selectedRows = TagListView.SelectedItems.OfType<NavRow>()
                .Where(r => !r.IsGroupHeader)
                .ToList();
            if (selectedRows.Count == 0) return; // グループヘッダーのみのクリックはハイライトのみ

            if (FilterListView != null) FilterListView.SelectedItem = null; // フィルタ選択と排他

            if (selectedRows.Count == 1)
            {
                var row = selectedRows[0];
                if (row.IsFolder)
                {
                    _currentTagId = null;
                    _currentFilterKey = null;
                    // タグリストに登録されたフォルダ行なので、この中身はサムネイルキャッシュを作る。
                    _folderCacheEnabled = true;
                    OpenFolderPathIntoGrid(row.FolderPath);
                    return;
                }

                var tagId = row.TagId;
                _currentTagId = tagId;
                _currentFilterKey = null;
                _currentFolderPath = null;
                _folderBackStack.Clear();
                _folderForwardStack.Clear();
                UpdateFolderNavButtons();
                _currentViewQuery = () => _db.GetFilesByTag(tagId);
                _currentViewLabel = $"タグ: {row.Name}";
                // ★ 重要: FilterListView_SelectionChanged側と同じ理由で、ここでも
                // RefreshAllTagChipWidths()を旧アイテム集合に対して呼ばない
                // （下のRefreshFilesView()が新しいアイテムを正しい形状/サイズで作り直す）。
                UiSettings.Instance.GridShape = row.GridShape;
                if (_isInitializing) return; // 起動直後は一覧を読み込まず空欄のままにする
                RefreshFilesViewWithPrefetch();
                return;
            }

            // 複数タグ選択時: フォルダ行は対象外にし、選択中の各タグが付いたファイルの
            // 和集合（OR）を表示する。グループへの移動・削除・グリッド選択などの一括操作は
            // TagListView_RightTapped側でTagListView.SelectedItemsを直接見て行う。
            var tagRows = selectedRows.Where(r => !r.IsFolder).ToList();
            if (tagRows.Count == 0) return;

            _currentTagId = null;
            _currentFilterKey = null;
            _currentFolderPath = null;
            _folderBackStack.Clear();
            _folderForwardStack.Clear();
            UpdateFolderNavButtons();
            var tagIds = tagRows.Select(r => r.TagId).ToList();
            _currentViewQuery = () =>
            {
                var seen = new HashSet<long>();
                var merged = new List<FileRecord>();
                foreach (var tid in tagIds)
                {
                    foreach (var f in _db.GetFilesByTag(tid))
                    {
                        if (seen.Add(f.Id)) merged.Add(f);
                    }
                }
                return merged;
            };
            _currentViewLabel = $"タグ: {string.Join(", ", tagRows.Select(r => r.Name))}（{tagRows.Count}件選択）";
            UiSettings.Instance.GridShape = "square"; // 複数タグ混在時は形状の基準が定まらないため正方形に統一
            if (_isInitializing) return;
            RefreshFilesViewWithPrefetch();
        }
        catch (Exception ex)
        {
            // 「すべてのファイル」等からタグへ切り替える際に原因不明の落ちが報告されていたための対策。
            // ここで捕まえてログに残すことで、アプリごと落ちるのを防ぎつつ原因調査ができるようにする。
            App.LogCrash(ex);
            if (TagActionStatusText != null)
                TagActionStatusText.Text = $"表示の切り替えでエラーが発生しました（ログに記録しました）: {ex.Message}";
        }
    }

    /// <summary>グループヘッダーのダブルクリックで折りたたみをトグルする（Python版 _on_nav_dblclick 相当）</summary>
    private void TagListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        var container = d != null ? FindAncestor<ListViewItem>(d) : null;
        if (container == null) return;
        if (TagListView.ItemFromContainer(container) is not NavRow row) return;
        if (row.IsGroupHeader && !row.IsUngroupedHeader && row.GroupId.HasValue)
        {
            _db.ToggleGroupCollapsed(row.GroupId.Value);
            RefreshNavList();
        }
    }

    /// <summary>Python版 _on_nav_rclick 相当。タグ／グループヘッダー／空白のどこを右クリックしたかで
    /// 出すメニューを切り替える。</summary>
    private async void TagListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        var container = d != null ? FindAncestor<ListViewItem>(d) : null;
        var row = container != null ? TagListView.ItemFromContainer(container) as NavRow : null;

        // 右クリックした行が既に（Ctrl/Shiftによる）複数選択に含まれている場合は選択を維持し、
        // 選択中の全タグを対象にした一括操作メニューを出す。
        // それ以外（未選択の行を右クリックした場合など）は、従来どおりその1行だけの選択に絞る。
        var alreadyInMultiSelection = row != null
            && TagListView.SelectedItems.Count > 1
            && TagListView.SelectedItems.Contains(row);
        if (row != null && !alreadyInMultiSelection) TagListView.SelectedItem = row;

        var selectedTagRows = TagListView.SelectedItems.OfType<NavRow>()
            .Where(r => !r.IsGroupHeader && !r.IsFolder)
            .ToList();

        var flyout = new MenuFlyout();

        var newTagItem = new MenuFlyoutItem { Text = "新規タグ..." };
        newTagItem.Click += async (_, _) => await NewTagDialogAsync();
        flyout.Items.Add(newTagItem);

        var newGroupItem = new MenuFlyoutItem { Text = "新規グループ..." };
        newGroupItem.Click += async (_, _) => await NewGroupDialogAsync();
        flyout.Items.Add(newGroupItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // タグの並び替え・グループの並び替えは互いに独立したモードとして選べる
        // （タグは各グループ内・未分類内での並び、グループはグループヘッダー自体の並び）。
        var tagSortSub = new MenuFlyoutSubItem { Text = "タグの並び替え" };
        foreach (var (label, key) in AppConfig.TagSortKeyOptions)
        {
            var item = new MenuFlyoutItem { Text = _config.TagSortKey == key ? $"✓ {label}" : label };
            item.Click += (_, _) =>
            {
                if (_config.TagSortKey == key) return;
                _config.TagSortKey = key;
                _config.Save();
                RefreshNavList();
            };
            tagSortSub.Items.Add(item);
        }
        flyout.Items.Add(tagSortSub);

        var groupSortSub = new MenuFlyoutSubItem { Text = "グループの並び替え" };
        foreach (var (label, key) in AppConfig.GroupSortKeyOptions)
        {
            var item = new MenuFlyoutItem { Text = _config.GroupSortKey == key ? $"✓ {label}" : label };
            item.Click += (_, _) =>
            {
                if (_config.GroupSortKey == key) return;
                _config.GroupSortKey = key;
                _config.Save();
                RefreshNavList();
            };
            groupSortSub.Items.Add(item);
        }
        flyout.Items.Add(groupSortSub);

        if (row != null) flyout.Items.Add(new MenuFlyoutSeparator());

        if (selectedTagRows.Count > 1)
        {
            // ── 複数タグを選択中の一括操作メニュー ──────────────────────
            var countLabel = new MenuFlyoutItem { Text = $"{selectedTagRows.Count} 件のタグを選択中", IsEnabled = false };
            flyout.Items.Add(countLabel);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveSubMulti = new MenuFlyoutSubItem { Text = "グループに移動" };
            var noneItemMulti = new MenuFlyoutItem { Text = "（なし）" };
            noneItemMulti.Click += (_, _) => MoveTagsToGroup(selectedTagRows, null);
            moveSubMulti.Items.Add(noneItemMulti);
            foreach (var g in _db.GetAllTagGroups())
            {
                var gi = new MenuFlyoutItem { Text = g.Name };
                var gid = g.Id;
                gi.Click += (_, _) => MoveTagsToGroup(selectedTagRows, gid);
                moveSubMulti.Items.Add(gi);
            }
            flyout.Items.Add(moveSubMulti);

            var selectInGridItem = new MenuFlyoutItem { Text = "このタグのファイルをグリッドで選択" };
            selectInGridItem.Click += (_, _) => SelectFilesForTagsInGrid(selectedTagRows);
            flyout.Items.Add(selectInGridItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteMultiItem = new MenuFlyoutItem { Text = "選択したタグを削除..." };
            deleteMultiItem.Click += async (_, _) => await DeleteTagsDialogAsync(selectedTagRows);
            flyout.Items.Add(deleteMultiItem);

            flyout.ShowAt(TagListView, e.GetPosition(TagListView));
            return;
        }

        if (row != null && !row.IsGroupHeader && row.IsFolder)
        {
            var openItem = new MenuFlyoutItem { Text = "開く" };
            // タグリストに登録されたフォルダ行なので、この中身はサムネイルキャッシュを作る。
            openItem.Click += (_, _) => { _folderCacheEnabled = true; OpenFolderPathIntoGrid(row.FolderPath); };
            flyout.Items.Add(openItem);

            var renameItem = new MenuFlyoutItem { Text = "表示名を変更" };
            renameItem.Click += async (_, _) => await RenameFolderDialogAsync(row.FolderId, row.Name);
            flyout.Items.Add(renameItem);

            // タグと同じく、フォルダショートカットもグループへ分類できるようにする。
            var moveFolderSub = new MenuFlyoutSubItem { Text = "グループに移動" };
            var noneFolderItem = new MenuFlyoutItem { Text = "（なし）" };
            var folderIdForMove = row.FolderId;
            noneFolderItem.Click += (_, _) => { _db.SetFolderGroup(folderIdForMove, null); RefreshNavList(); };
            moveFolderSub.Items.Add(noneFolderItem);
            foreach (var g in _db.GetAllTagGroups())
            {
                var gi = new MenuFlyoutItem { Text = g.Name };
                var gid = g.Id;
                gi.Click += (_, _) => { _db.SetFolderGroup(folderIdForMove, gid); RefreshNavList(); };
                moveFolderSub.Items.Add(gi);
            }
            flyout.Items.Add(moveFolderSub);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "タグリストから削除" };
            deleteItem.Click += (_, _) =>
            {
                _db.DeleteFolder(row.FolderId);
                RefreshNavList();
                TagActionStatusText.Text = $"「{row.Name}」をタグリストから削除しました（実際のフォルダは削除されません）。";
            };
            flyout.Items.Add(deleteItem);
        }
        else if (row != null && !row.IsGroupHeader)
        {
            var renameItem = new MenuFlyoutItem { Text = "タグ名を変更" };
            renameItem.Click += async (_, _) => await RenameTagDialogAsync(row.TagId, row.Name);
            flyout.Items.Add(renameItem);

            var colorItem = new MenuFlyoutItem { Text = "色を変更" };
            colorItem.Click += async (_, _) => await ChangeTagColorAsync(row.TagId, row.ColorBrush.Color);
            flyout.Items.Add(colorItem);

            // このタグを選択したときに右のファイルグリッドをどの形状で表示するか
            // （正方形／コミック向け縦長／動画向け横長）を選べるサブメニュー。
            var shapeSub = new MenuFlyoutSubItem { Text = "グリッド形状" };
            var tagIdForShape = row.TagId;
            foreach (var (label, key) in AppConfig.GridShapeOptions)
            {
                var shapeItem = new MenuFlyoutItem { Text = row.GridShape == key ? $"✓ {label}" : label };
                shapeItem.Click += (_, _) =>
                {
                    _db.UpdateTagGridShape(tagIdForShape, key);
                    // 現在このタグを表示中であれば、グリッドの見た目にもすぐ反映する。
                    // サムネイル画像自体の縦横比（パディングの入り方）も変わるため、
                    // ItemsSourceを一旦外して付け直し、全コンテナのサムネイルを再生成させる
                    // （設定ダイアログの「サムネイル解像度」変更時と同じ手法）。
                    if (_currentTagId == tagIdForShape)
                    {
                        UiSettings.Instance.GridShape = key;
                        RefreshAllTagChipWidths();
                        var current = ThumbGridView.ItemsSource;
                        ThumbGridView.ItemsSource = null;
                        ThumbGridView.ItemsSource = current;
                    }
                    RefreshNavList();
                };
                shapeSub.Items.Add(shapeItem);
            }
            flyout.Items.Add(shapeSub);

            var moveSub = new MenuFlyoutSubItem { Text = "グループに移動" };
            var noneItem = new MenuFlyoutItem { Text = "（なし）" };
            var tagIdForMove = row.TagId;
            noneItem.Click += (_, _) => { _db.SetTagGroup(tagIdForMove, null); RefreshNavList(); };
            moveSub.Items.Add(noneItem);
            foreach (var g in _db.GetAllTagGroups())
            {
                var gi = new MenuFlyoutItem { Text = g.Name };
                var gid = g.Id;
                gi.Click += (_, _) => { _db.SetTagGroup(tagIdForMove, gid); RefreshNavList(); };
                moveSub.Items.Add(gi);
            }
            flyout.Items.Add(moveSub);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "タグを削除" };
            deleteItem.Click += async (_, _) => await DeleteTagDialogAsync(row.TagId, row.Name);
            flyout.Items.Add(deleteItem);
        }
        else if (row != null && row.IsGroupHeader && !row.IsUngroupedHeader && row.GroupId.HasValue)
        {
            var gid = row.GroupId.Value;
            var collapsed = _db.IsGroupCollapsed(gid);
            var toggleItem = new MenuFlyoutItem { Text = collapsed ? "開く" : "閉じる" };
            toggleItem.Click += (_, _) => { _db.ToggleGroupCollapsed(gid); RefreshNavList(); };
            flyout.Items.Add(toggleItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var renameGroupItem = new MenuFlyoutItem { Text = "グループ名を変更" };
            renameGroupItem.Click += async (_, _) => await RenameGroupDialogAsync(gid, row.Label);
            flyout.Items.Add(renameGroupItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteGroupItem = new MenuFlyoutItem { Text = "グループを削除（タグは残す）" };
            deleteGroupItem.Click += async (_, _) => await DeleteGroupDialogAsync(gid, row.Label);
            flyout.Items.Add(deleteGroupItem);
        }

        flyout.ShowAt(TagListView, e.GetPosition(TagListView));
    }

    // ── タグ／グループの追加・変更・削除ダイアログ（Python版 simpledialog相当） ──
    private async Task<string?> PromptTextAsync(string title, string initial = "")
    {
        var textBox = new TextBox { Text = initial, PlaceholderText = "名前" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        var text = textBox.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private async Task NewTagDialogAsync()
    {
        var name = await PromptTextAsync("タグ追加");
        if (name == null) return;
        _db.AddTag(name);
        RefreshNavList();
    }

    private async Task NewGroupDialogAsync()
    {
        var name = await PromptTextAsync("グループ追加");
        if (name == null) return;
        _db.AddTagGroup(name);
        RefreshNavList();
    }

    private async Task RenameTagDialogAsync(long tagId, string current)
    {
        var name = await PromptTextAsync("タグ名を変更", current);
        if (name == null) return;
        _db.RenameTag(tagId, name);
        RefreshNavList();
        RefreshFilesView();
    }

    private async Task RenameFolderDialogAsync(long folderId, string current)
    {
        var name = await PromptTextAsync("表示名を変更", current);
        if (name == null) return;
        _db.RenameFolder(folderId, name);
        RefreshNavList();
    }

    private async Task RenameGroupDialogAsync(long groupId, string current)
    {
        var name = await PromptTextAsync("グループ名を変更", current);
        if (name == null) return;
        _db.RenameTagGroup(groupId, name);
        RefreshNavList();
    }

    // ── 複数タグ選択時の一括操作（グループ移動／グリッド選択／削除） ──────────────
    private void MoveTagsToGroup(List<NavRow> tagRows, long? groupId)
    {
        foreach (var row in tagRows) _db.SetTagGroup(row.TagId, groupId);
        RefreshNavList();
        TagActionStatusText.Text = $"{tagRows.Count} 件のタグをグループへ移動しました。";
    }

    /// <summary>選択中の各タグが付いたファイルの和集合を、右のファイルグリッド上で選択状態にする
    /// （現在グリッドに表示されている範囲の中から一致するものだけ。表示自体は変更しない）。</summary>
    private void SelectFilesForTagsInGrid(List<NavRow> tagRows)
    {
        var idSet = new HashSet<long>();
        foreach (var row in tagRows)
            foreach (var f in _db.GetFilesByTag(row.TagId))
                idSet.Add(f.Id);

        ThumbGridView.SelectedItems.Clear();
        var matched = 0;
        foreach (var item in _items)
        {
            if (!idSet.Contains(item.Id)) continue;
            ThumbGridView.SelectedItems.Add(item);
            matched++;
        }
        TagActionStatusText.Text = matched > 0
            ? $"{tagRows.Count} 件のタグに該当する {matched} 件のファイルをグリッドで選択しました。"
            : "現在グリッドに表示されている範囲に該当ファイルがありませんでした。";
    }

    private async Task DeleteTagsDialogAsync(List<NavRow> tagRows)
    {
        var names = string.Join("、", tagRows.Select(r => r.Name));
        var dialog = new ContentDialog
        {
            Title = "タグ削除",
            Content = $"次の {tagRows.Count} 件のタグを削除しますか？\n{names}",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var row in tagRows) _db.DeleteTag(row.TagId);
        RefreshNavList();
        RefreshFilesView();
        TagActionStatusText.Text = $"{tagRows.Count} 件のタグを削除しました。";
    }

    private async Task DeleteTagDialogAsync(long tagId, string name)
    {
        var dialog = new ContentDialog
        {
            Title = "タグ削除",
            Content = $"タグ「{name}」を削除しますか？",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _db.DeleteTag(tagId);
        RefreshNavList();
        RefreshFilesView();
    }

    private async Task DeleteGroupDialogAsync(long groupId, string name)
    {
        var dialog = new ContentDialog
        {
            Title = "グループを削除",
            Content = $"グループ「{name}」を削除しますか？\n（グループ内のタグはそのまま残ります）",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _db.DeleteTagGroup(groupId);
        RefreshNavList();
    }

    /// <summary>Python版「🎨 タグの色を変更」相当。右クリックしたタグの色をColorPickerで変更する。</summary>
    private async Task ChangeTagColorAsync(long tagId, Windows.UI.Color initial)
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
        };
        // Color をコンストラクタ直後に設定すると、2回目以降スペクトラム描画が
        // 白飛びする WinUI3 の既知の癖があるため、Loaded 後に設定する。
        picker.Loaded += (_, _) => picker.Color = initial;

        var dialog = new ContentDialog
        {
            Title = "色を変更",
            Content = picker,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var c = picker.Color;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _db.UpdateTagColor(tagId, hex);
        RefreshNavList();
        RefreshFilesView();
    }

    // ── ドラッグ＆ドロップ（Python版 _on_drag / _on_drag_release / _on_external_drop 相当） ──

    // ドラッグ中のファイルID一覧を DataPackage の Text に載せる際の目印。
    // 通常のテキストドロップ（例えばURLやファイルパスの文字列）と区別するために使う。
    private const string FileIdsPrefix = "TAGFILER_FILE_IDS:";
    private const string TagIdsPrefix = "TAGFILER_TAG_IDS:";

    private static List<long> ParseDraggedFileIds(string text)
    {
        if (!text.StartsWith(FileIdsPrefix, StringComparison.Ordinal)) return new List<long>();
        return text[FileIdsPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var v) ? (long?)v : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
    }

    private static List<long> ParseDraggedTagIds(string text)
    {
        if (!text.StartsWith(TagIdsPrefix, StringComparison.Ordinal)) return new List<long>();
        return text[TagIdsPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var v) ? (long?)v : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
    }

    /// <summary>ThumbGridViewでのドラッグ開始。選択中のファイルID一覧をDataPackageに載せる。</summary>
    // ── フォルダのパスを変更（Python版 _rename_folder_dialog / DB.update_dir_path の移植） ──
    private async void RenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var allFiles = _db.GetAllFiles();
        if (allFiles.Count == 0)
        {
            var infoDialog = new ContentDialog
            {
                Title = "フォルダ変更",
                Content = "登録ファイルがありません。",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await infoDialog.ShowAsync();
            return;
        }

        // フォルダ単位でグループ化（同じフォルダ内のファイルを集約）
        var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in allFiles)
        {
            var dir = System.IO.Path.GetDirectoryName(f.Path) ?? "";
            folderCounts[dir] = folderCounts.GetValueOrDefault(dir) + 1;
        }
        var allFolders = folderCounts.Keys.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        var visibleFolders = new List<string>();

        // ── フィルタ検索バー ──
        var filterBox = new TextBox { PlaceholderText = "絞り込み" };

        // ── フォルダ一覧 ──
        var folderList = new ListView
        {
            Height = 260,
            SelectionMode = ListViewSelectionMode.Single,
        };

        void Populate(string query)
        {
            folderList.Items.Clear();
            visibleFolders.Clear();
            var q = query.Trim();
            foreach (var d in allFolders)
            {
                if (q.Length > 0 && d.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var exists = System.IO.Directory.Exists(d) ? "✓" : "✗";
                folderList.Items.Add($"{exists}  [{folderCounts[d],4}件]  {d}");
                visibleFolders.Add(d);
            }
        }
        Populate("");
        filterBox.TextChanged += (_, _) => Populate(filterBox.Text);

        // ── 選択フォルダ表示 ──
        var oldDirText = new TextBlock { Text = "変更元: （リストから選択）", Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var previewText = new TextBlock { FontSize = 11, Opacity = 0.7 };

        string? GetSelectedOldDir() =>
            folderList.SelectedIndex >= 0 && folderList.SelectedIndex < visibleFolders.Count
                ? visibleFolders[folderList.SelectedIndex]
                : null;

        folderList.SelectionChanged += (_, _) =>
        {
            var d = GetSelectedOldDir();
            if (d == null) return;
            oldDirText.Text = $"変更元: {d}";
            previewText.Text = $"このフォルダに含まれるファイル: {folderCounts[d]}件";
        };

        // ── 変更先フォルダ行 ──
        var newDirBox = new TextBox { PlaceholderText = "変更先フォルダのパス", HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(newDirBox, 0);
        var browseButton = new Button { Content = "📁 選択..." };
        Grid.SetColumn(browseButton, 1);
        browseButton.Click += async (_, _) =>
        {
            if (_filePickerOpen) return;
            _filePickerOpen = true;
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");
                // 「フォルダを開く」ボタンと同じ理由（記憶された場所に飛んでしまいSuggestedStartLocationが
                // 効かず、「選択」ボタンが押せなくなる現象）への対策。
                picker.SettingsIdentifier = Guid.NewGuid().ToString();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) newDirBox.Text = folder.Path;
            }
            finally
            {
                _filePickerOpen = false;
            }
        };
        var newDirRow = new Grid { ColumnSpacing = 4 };
        newDirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        newDirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        newDirRow.Children.Add(newDirBox);
        newDirRow.Children.Add(browseButton);

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = "変更元フォルダを選択してください（ファイル数を右に表示）",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(filterBox);
        panel.Children.Add(folderList);
        panel.Children.Add(oldDirText);
        panel.Children.Add(new TextBlock { Text = "変更先:" });
        panel.Children.Add(newDirRow);
        panel.Children.Add(previewText);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "フォルダのパスを変更",
            Content = panel,
            PrimaryButtonText = "更新実行",
            CloseButtonText = "閉じる",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        // 入力が不正な場合はダイアログを閉じずにエラーを表示して再入力させる
        // （Python版のmessagebox警告 + ダイアログ継続 相当）
        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var oldDir = GetSelectedOldDir();
            var newDir = newDirBox.Text.Trim();

            if (string.IsNullOrEmpty(oldDir))
            {
                errorText.Text = "変更元フォルダを選択してください。";
                errorText.Visibility = Visibility.Visible;
                continue;
            }
            if (string.IsNullOrEmpty(newDir))
            {
                errorText.Text = "変更先フォルダを入力してください。";
                errorText.Visibility = Visibility.Visible;
                continue;
            }

            var oldDirFull = System.IO.Path.GetFullPath(oldDir);
            var newDirFull = System.IO.Path.GetFullPath(newDir);
            if (string.Equals(oldDirFull, newDirFull, StringComparison.OrdinalIgnoreCase))
            {
                errorText.Text = "変更元と変更先が同じです。";
                errorText.Visibility = Visibility.Visible;
                continue;
            }

            var cnt = _db.UpdateDirPath(oldDirFull, newDirFull);
            RefreshFilesView();
            TagActionStatusText.Text = $"{cnt}件のファイルパスを更新しました。（{oldDirFull} → {newDirFull}）";
            return;
        }
    }

    private void ThumbGridView_Loaded(object sender, RoutedEventArgs e)
    {
        _gridScrollViewer ??= FindDescendant<ScrollViewer>(ThumbGridView);
        if (_gridScrollViewer == null) return;

        // 既定のScrollBarは非表示にしているので(XAML側でVerticalScrollBarVisibility="Hidden")、
        // ScrollViewerの状態変化に合わせて自前のつまみ(CustomVScrollThumb)の位置とサイズを更新する。
        _gridScrollViewer.ViewChanged -= GridScrollViewer_ViewChanged;
        _gridScrollViewer.ViewChanged += GridScrollViewer_ViewChanged;
        UpdateCustomVScrollThumb();
    }

    // ── 自前の縦スクロールバー（常に太い表示） ─────────────────────────
    private bool _customThumbDragging;
    private double _customThumbDragStartY;
    private double _customThumbDragStartOffset;
    // 「すべてのファイル」等、件数の多い（3000件規模の）タグでスクロールバーのつまみを
    // 素早くドラッグすると、PointerMovedのたびにChangeViewを呼んでいた従来実装では
    // 1秒間に何十回ものオフセット変更がGridViewの仮想化パネル(ItemsWrapGrid、ネイティブ側)に
    // 送られ、コンテナの実体化/再利用が追いつかずに無言のクラッシュ(WERのE_UNEXPECTED /
    // 8000ffff、管理コード側の例外にすらならないためApp.LogCrashにも残らない)を
    // 引き起こしていた。ドラッグ中はChangeViewの発行を一定間隔に間引き、最新のオフセットは
    // 常に覚えておいて指を離した瞬間に確定反映することで、見た目の追従性を保ちつつ
    // ネイティブ側への負荷を抑える。
    private readonly System.Diagnostics.Stopwatch _customThumbDragThrottle = new();
    private double _customThumbLastOffset;
    private const int CustomThumbDragThrottleMs = 40; // 約25回/秒までに制限

    private void GridScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateCustomVScrollThumb();

    /// <summary>ScrollViewerの現在のExtent/Viewport/Offsetから、つまみの高さと位置を計算して反映する。</summary>
    private void UpdateCustomVScrollThumb()
    {
        if (_gridScrollViewer == null) return;

        var trackHeight = CustomVScrollTrack.ActualHeight;
        var extent = _gridScrollViewer.ExtentHeight;
        var viewport = _gridScrollViewer.ViewportHeight;

        // スクロールの必要がない（全部表示できている）場合はつまみを隠す
        if (trackHeight <= 0 || extent <= viewport + 0.5)
        {
            CustomVScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }

        CustomVScrollThumb.Visibility = Visibility.Visible;

        var thumbHeight = Math.Max(24, trackHeight * (viewport / extent));
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var scrollableExtent = extent - viewport;
        var thumbTop = scrollableExtent > 0
            ? maxThumbTop * (_gridScrollViewer.VerticalOffset / scrollableExtent)
            : 0;

        CustomVScrollThumb.Height = thumbHeight;
        Canvas.SetTop(CustomVScrollThumb, thumbTop);
    }

    private void CustomVScrollThumb_PointerEntered(object sender, PointerRoutedEventArgs e)
        => CustomVScrollThumb.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xAA, 0x80, 0x80, 0x80));

    private void CustomVScrollThumb_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_customThumbDragging)
            CustomVScrollThumb.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x80, 0x80, 0x80));
    }

    private void CustomVScrollThumb_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_gridScrollViewer == null) return;
        _customThumbDragging = true;
        _customThumbDragStartY = e.GetCurrentPoint(CustomVScrollTrack).Position.Y;
        _customThumbDragStartOffset = _gridScrollViewer.VerticalOffset;
        _customThumbLastOffset = _customThumbDragStartOffset;
        _customThumbDragThrottle.Restart();
        CustomVScrollThumb.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void CustomVScrollThumb_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_customThumbDragging || _gridScrollViewer == null) return;

        var trackHeight = CustomVScrollTrack.ActualHeight;
        var extent = _gridScrollViewer.ExtentHeight;
        var viewport = _gridScrollViewer.ViewportHeight;
        var thumbHeight = Math.Max(24, trackHeight * (viewport / extent));
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var scrollableExtent = extent - viewport;
        if (maxThumbTop <= 0 || scrollableExtent <= 0) return;

        var currentY = e.GetCurrentPoint(CustomVScrollTrack).Position.Y;
        var deltaY = currentY - _customThumbDragStartY;
        var deltaOffset = deltaY * (scrollableExtent / maxThumbTop);
        var newOffset = Math.Clamp(_customThumbDragStartOffset + deltaOffset, 0, scrollableExtent);
        _customThumbLastOffset = newOffset;

        // 件数の多い一覧で高速ドラッグした際に、GridViewの仮想化パネルへ大量のオフセット変更を
        // 立て続けに送って無言クラッシュするのを防ぐため、実際のChangeView発行は間引く
        // （つまみの見た目位置はPointerMoved毎に即座に更新したいところだが、ここは
        // ChangeViewを起点にGridScrollViewer_ViewChanged→UpdateCustomVScrollThumbが
        // 呼ばれる作りのため、間引いた分だけつまみの追従も粗くなる。それでも指を離せば
        // 最終位置は必ず反映されるため、体感上の破綻はない）。
        if (_customThumbDragThrottle.ElapsedMilliseconds < CustomThumbDragThrottleMs) { e.Handled = true; return; }
        _customThumbDragThrottle.Restart();

        _gridScrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    private void CustomVScrollThumb_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _customThumbDragging = false;
        CustomVScrollThumb.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x80, 0x80, 0x80));
        CustomVScrollThumb.ReleasePointerCapture(e.Pointer);
        // 間引きで反映されていなかった可能性のある最終オフセットを、指を離した時点で確実に反映する。
        if (_gridScrollViewer != null && Math.Abs(_gridScrollViewer.VerticalOffset - _customThumbLastOffset) >= 0.5)
            _gridScrollViewer.ChangeView(null, _customThumbLastOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    /// <summary>トラック（つまみ以外の部分）をクリックした場合、その位置までページ送りする。</summary>
    private void CustomVScrollTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_gridScrollViewer == null) return;

        var trackHeight = CustomVScrollTrack.ActualHeight;
        var extent = _gridScrollViewer.ExtentHeight;
        var viewport = _gridScrollViewer.ViewportHeight;
        if (trackHeight <= 0 || extent <= viewport) return;

        var thumbHeight = Math.Max(24, trackHeight * (viewport / extent));
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var scrollableExtent = extent - viewport;
        if (maxThumbTop <= 0 || scrollableExtent <= 0) return;

        var clickY = e.GetCurrentPoint(CustomVScrollTrack).Position.Y;
        var targetThumbTop = Math.Clamp(clickY - thumbHeight / 2, 0, maxThumbTop);
        var newOffset = scrollableExtent * (targetThumbTop / maxThumbTop);

        _gridScrollViewer.ChangeView(null, newOffset, null, disableAnimation: false);
        e.Handled = true;
    }

    private void ThumbGridView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // タグへドラッグしてタグ付けする操作の起点になるため、まだDB未登録（Id==0）のファイルが
        // 含まれていればここで登録する。登録前のId==0のまま複数ファイルをドラッグすると、
        // IDが全部「0」で重複してしまいドロップ先で区別できなくなるため。
        var items = e.Items.OfType<FileItem>().ToList();
        var ids = items.Select(EnsureRegistered).ToList();
        if (ids.Count == 0) return;
        e.Data.SetText(FileIdsPrefix + string.Join(",", ids));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        // Python版の「+ N」ドラッグ吹き出しに相当（OSのドラッグUIに件数を表示）
        e.Data.Properties.Title = $"+{ids.Count} 件";
    }

    /// <summary>TagListViewでのドラッグ開始。選択中のタグ行のタグID一覧をDataPackageに載せる
    /// （ThumbGridView_DragItemsStartingのファイルID版に相当）。
    /// グループヘッダー行・フォルダ行はタグ付け対象にならないため、ドラッグ項目から除外する。
    /// 対象がタグ行に一つも残らない場合はドラッグ自体をキャンセルする。</summary>
    private void TagListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var tagIds = e.Items.OfType<NavRow>()
            .Where(r => !r.IsGroupHeader && !r.IsFolder)
            .Select(r => r.TagId)
            .Distinct()
            .ToList();
        if (tagIds.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText(TagIdsPrefix + string.Join(",", tagIds));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties.Title = tagIds.Count == 1
            ? "タグを追加"
            : $"タグ {tagIds.Count} 件を追加";
    }

    /// <summary>ドラッグ中のビジュアルを、既定のセルのスクリーンショット（サムネイル画像）ではなく
    /// DataPackageの内容（「+N件」のキャプション）だけの吹き出しにする。
    /// GridViewItem.DragStartingでSetContentFromDataPackage()を呼ぶことで既定のサムネイル表示を抑制できる。</summary>
    private void ThumbGridViewItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        args.DragUI.SetContentFromDataPackage();
    }

    /// <summary>タグ一覧へのドラッグオーバー（ファイルをタグへドラッグしてタグ付けする）。
    /// 内部のファイル一覧からのドラッグ（Text: ファイルID一覧）と、
    /// エクスプローラー等外部からのファイルドラッグ（StorageItems）の両方を受け付ける。
    /// 以前はTextのみ受け付けていたため、エクスプローラーから直接タグへドロップしても
    /// 何も起こらなかった（DragOverの時点でAcceptedOperationが設定されず拒否されていた）。
    /// ここでホバー中タグのチップ表示と、狭いゾーンでの自前の自動スクロールも行う
    /// （TagListView内部ScrollViewerの既定の自動スクロールは感度を公開APIから調整できないため、
    /// 完全に置き換えるのではなく、それとは別にこちらのタイマーでも操作する）。</summary>
    private void TagDropOverlay_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text) && !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            HideTagHoverChip();
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "ドロップしてタグを追加／フォルダを登録";
        e.DragUIOverride.IsGlyphVisible = false;

        var pos = e.GetPosition(null);
        _tagLastPointerPos = pos;
        UpdateTagHoverChip(pos);
        StartTagAutoScrollTimer();
    }

    private void TagDropOverlay_DragEnter(object sender, DragEventArgs e)
    {
        StartTagAutoScrollTimer();
    }

    /// <summary>タグ一覧の外へドラッグが抜けた/ドロップされた時にチップと自動スクロールを止める。</summary>
    private void TagDropOverlay_DragLeave(object sender, DragEventArgs e)
    {
        StopTagAutoScrollTimer();
        HideTagHoverChip();
    }

    /// <summary>ドロップ位置の直下にあるタグ行（NavRow）を取得する（マウスホバーしているタグを特定するため）。
    /// pos は VisualTreeHelper.FindElementsInHostCoordinates が要求するルート（ウィンドウ）座標系で渡すこと
    /// （TagListView相対座標ではない。以前はTagListView相対のe.GetPosition(TagListView)をそのまま渡していたため、
    /// TagListViewがウィンドウ原点からオフセットしている分だけ判定がずれ、常にヒットせずドロップが無反応になっていた）。</summary>
    private NavRow? GetTagRowAtPosition(Windows.Foundation.Point pos)
    {
        var container = VisualTreeHelper.FindElementsInHostCoordinates(pos, TagListView)
            .Select(el => FindAncestor<ListViewItem>(el))
            .FirstOrDefault(c => c != null);
        if (container == null) return null;
        if (TagListView.ItemFromContainer(container) is not NavRow row || row.IsGroupHeader) return null;
        return row;
    }

    /// <summary>タグへドロップ：ドラッグしていたファイル全部にそのタグを追加する（Python版 _on_drag_release 相当）。
    /// 内部ドラッグ（Text）とエクスプローラー等外部ドラッグ（StorageItems）の両方に対応。
    /// 外部からのファイルは、ライブラリに未登録なら先に追加してからタグ付けする。
    /// また、フォルダをドロップした場合はタグ付けではなく、タグリストへの「フォルダ」ショートカット
    /// 登録として扱う（既存のタグ行の上に重なっているかどうかは問わない）。</summary>
    private async void TagDropOverlay_Drop(object sender, DragEventArgs e)
    {
        StopTagAutoScrollTimer();
        HideTagHoverChip();
        TagDropOverlay.IsHitTestVisible = false;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            await HandleInternalDragTextDrop(e);
            return;
        }

        var storageItems = await e.DataView.GetStorageItemsAsync();

        // フォルダが含まれていれば、タグ付けではなくフォルダショートカットとして登録する。
        var folders = storageItems.OfType<Windows.Storage.StorageFolder>().ToList();
        if (folders.Count > 0)
        {
            var addedNames = new List<string>();
            foreach (var f in folders)
            {
                if (_db.AddFolder(f.Path) != null) addedNames.Add(f.Name);
            }
            RefreshNavList();
            TagActionStatusText.Text = addedNames.Count > 0
                ? $"{addedNames.Count} 件のフォルダを登録しました: {string.Join(", ", addedNames)}"
                : "フォルダの登録に失敗しました（既に登録済みの可能性があります）。";
            return;
        }

        var row = GetTagRowAtPosition(e.GetPosition(null));
        if (row == null || row.IsFolder) return;

        var paths = storageItems
            .OfType<Windows.Storage.StorageFile>()
            .Select(f => f.Path)
            .ToList();
        if (paths.Count == 0) return;

        var ids = new List<long>();
        foreach (var p in paths)
        {
            var fid = _db.AddFile(p);
            if (fid is not null) ids.Add(fid.Value);
        }
        if (ids.Count == 0) return;

        var records = _db.GetAllFiles();
        LoadRecordsIntoGrid(records);

        AssignTagToFiles(row, ids);
    }

    /// <summary>内部のファイル一覧からのドラッグ（Text: ファイルID一覧）によるタグ付けドロップ処理。</summary>
    private async Task HandleInternalDragTextDrop(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;

        var row = GetTagRowAtPosition(e.GetPosition(null));
        if (row == null || row.IsFolder) return;

        var text = await e.DataView.GetTextAsync();
        var ids = ParseDraggedFileIds(text);
        if (ids.Count == 0) return;

        AssignTagToFiles(row, ids);
    }

    // ── タグへのドラッグ中：ホバー中タグのチップ表示 ──────────────────
    /// <summary>マウスカーソル付近に、現在ホバーしているタグのチップを表示する。
    /// posはウィンドウ座標系（DragEventArgs.GetPosition(null)と同じ）。</summary>
    private void UpdateTagHoverChip(Windows.Foundation.Point pos)
    {
        var row = GetTagRowAtPosition(pos);
        if (row == null)
        {
            HideTagHoverChip();
            return;
        }

        TagHoverChipText.Text = row.Label;
        if (row.DotVisibility == Visibility.Visible)
        {
            TagHoverChipDot.Background = row.ColorBrush;
            TagHoverChipDot.Visibility = Visibility.Visible;
        }
        else
        {
            TagHoverChipDot.Visibility = Visibility.Collapsed;
        }

        // カーソルの少し右下に表示する。チップ自体がカーソルの下に隠れて
        // ドロップ位置の判定が見えなくならないよう、オフセットを付ける。
        const double offsetX = 16;
        const double offsetY = 20;
        Canvas.SetLeft(TagHoverChip, pos.X + offsetX);
        Canvas.SetTop(TagHoverChip, pos.Y + offsetY);
        TagHoverChip.Visibility = Visibility.Visible;
    }

    private void HideTagHoverChip()
    {
        TagHoverChip.Visibility = Visibility.Collapsed;
    }

    // ── タグへのドラッグ中：自動スクロール ────────────────────────
    // TagListView内部のScrollViewerが持つ既定の自動スクロールとは別に、こちらでも
    // 狭いスクロール開始ゾーン・低速なスクロール量のタイマーを実装している。
    private void StartTagAutoScrollTimer()
    {
        if (_tagAutoScrollTimer != null) return;
        _tagAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _tagAutoScrollTimer.Tick += TagAutoScrollTimer_Tick;
        _tagAutoScrollTimer.Start();
    }

    private void StopTagAutoScrollTimer()
    {
        if (_tagAutoScrollTimer == null) return;
        _tagAutoScrollTimer.Stop();
        _tagAutoScrollTimer.Tick -= TagAutoScrollTimer_Tick;
        _tagAutoScrollTimer = null;
    }

    private void TagAutoScrollTimer_Tick(object? sender, object e)
    {
        if (_tagLastPointerPos == null) return;
        _tagScrollViewer ??= FindDescendant<ScrollViewer>(TagListView);
        if (_tagScrollViewer == null) return;

        // 以前のTagListView既定の自動スクロールは端から~40-50px程度の広い範囲で反応していた。
        // ここではedgeMarginを狭くして、本当に端に近づいた時だけスクロールし始めるようにする。
        const double edgeMargin = 22;   // この距離だけ端に近づくとスクロールし始める（狭め）
        const double maxSpeed = 8;      // 端ぎりぎりでの最大スクロール量(px/tick)（遅め）

        var origin = TagListView.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
        var y = _tagLastPointerPos.Value.Y - origin.Y;
        var height = TagListView.ActualHeight;

        double delta;
        if (y < edgeMargin) delta = -maxSpeed * ((edgeMargin - Math.Max(0, y)) / edgeMargin);
        else if (y > height - edgeMargin) delta = maxSpeed * ((Math.Min(height, y) - (height - edgeMargin)) / edgeMargin);
        else delta = 0;

        if (delta == 0) return;

        var maxOffset = Math.Max(0, _tagScrollViewer.ExtentHeight - _tagScrollViewer.ViewportHeight);
        var newOffset = Math.Clamp(_tagScrollViewer.VerticalOffset + delta, 0, maxOffset);
        if (Math.Abs(newOffset - _tagScrollViewer.VerticalOffset) >= 0.01)
            _tagScrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
    }

    /// <summary>指定ファイルID群へタグを追加し、グリッド上の表示とタグリストの件数を更新する。</summary>
    private void AssignTagToFiles(NavRow row, List<long> ids)
    {
        var tagRecord = _db.GetAllTags().FirstOrDefault(t => t.Id == row.TagId);
        var tagName = tagRecord?.Name ?? row.Name;

        foreach (var fid in ids) _db.AddFileTag(fid, row.TagId);
        foreach (var item in _items.Where(i => ids.Contains(i.Id)))
            SetItemTags(item, _db.GetFileTags(item.Id));

        RefreshNavList();
        TagActionStatusText.Text = $"{ids.Count} 件に「{tagName}」を追加しました。";
    }

    /// <summary>グリッドへのドラッグオーバー：外部（エクスプローラー）からのファイル、
    /// または内部ドラッグしていたファイルが再びグリッドへ戻ってきた場合の両方を受け付ける。</summary>
    private void ThumbGridView_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "ファイルを追加";
            e.DragUIOverride.IsGlyphVisible = false;
        }
        else if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "ファイルの上で離してタグ付け";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    /// <summary>Python版 _on_external_drop 相当：エクスプローラーからのファイルドロップでDBへ追加。
    /// また、内部ドラッグしていたファイルがタグ以外（グリッド自身）にドロップされた場合はタグ選択ダイアログを出す
    /// （Python版 _on_drag_release の「elif self._point_in_widget(self.grid.canvas, ...)」相当）。</summary>
    private async void ThumbGridView_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var storageItemsRaw = await e.DataView.GetStorageItemsAsync();

            // フォルダがドロップされた場合は、そのフォルダの中身をグリッドに表示する
            // （「フォルダを開く」ボタンと同じ処理を使い回す）。
            // ファイルとフォルダが混在していた場合は、最初に見つかったフォルダを優先する。
            var droppedFolder = storageItemsRaw.OfType<Windows.Storage.StorageFolder>().FirstOrDefault();
            if (droppedFolder != null)
            {
                // ドラッグ＆ドロップ経由なので、この中身はサムネイルキャッシュを作らない。
                _folderCacheEnabled = false;
                OpenFolderPathIntoGrid(droppedFolder.Path);
                return;
            }

            var paths = storageItemsRaw
                .OfType<Windows.Storage.StorageFile>()
                .Select(f => f.Path)
                .ToList();
            if (paths.Count == 0) return;

            var addedIds = new List<long>();
            foreach (var p in paths)
            {
                var fid = _db.AddFile(p);
                if (fid is not null) addedIds.Add(fid.Value);
            }

            RefreshNavList();

            if (addedIds.Count == 0)
            {
                RefreshFilesView();
                return;
            }

            // 現在タグ一覧で特定のタグを開いている場合は、そのタグを自動で付与する
            // （以前はタグ選択ダイアログを毎回開いていたが、「今見ているタグへドロップ」という
            // 直感的な操作にするため、開いているタグへ直接タグ付けするように変更）。
            // タグを開いていない（起動時の「すべて」等、何も表示されていない状態を含む）
            // 場合は、ファイル追加後にタグ選択ダイアログを表示してその場でタグ付けできるようにする。
            if (_currentTagId is { } tagId)
            {
                var tagName = _db.GetAllTags().FirstOrDefault(t => t.Id == tagId)?.Name ?? "";
                foreach (var fid in addedIds) _db.AddFileTag(fid, tagId);
                RefreshNavList();
                TagActionStatusText.Text = $"{addedIds.Count} 件を追加し「{tagName}」を付与しました。";
                RefreshFilesView();
            }
            else
            {
                RefreshFilesView();
                await ShowMultiTagPickerAsync(addedIds);
            }

            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            var text = await e.DataView.GetTextAsync();

            // タグ一覧からドラッグしてきたタグをファイルへドロップ：ホバーしていたファイルだけに付与する。
            var tagIds = ParseDraggedTagIds(text);
            if (tagIds.Count > 0)
            {
                var hovered = GetFileItemAtPosition(e.GetPosition(null));
                if (hovered == null) return; // ファイルの上以外でのドロップは何もしない

                var tagNames = _db.GetAllTags().Where(t => tagIds.Contains(t.Id)).Select(t => t.Name).ToList();
                foreach (var tid in tagIds) _db.AddFileTag(hovered.Id, tid);
                SetItemTags(hovered, _db.GetFileTags(hovered.Id));

                RefreshNavList();
                TagActionStatusText.Text = $"「{hovered.DisplayName}」に「{string.Join("、", tagNames)}」を追加しました。";
                return;
            }

            var ids = ParseDraggedFileIds(text);
            if (ids.Count == 0) return;
            await ShowMultiTagPickerAsync(ids);
        }
    }

    /// <summary>ドロップ位置の直下にあるファイル項目（FileItem）を取得する
    /// （タグをファイルへドラッグ&ドロップしてタグ付けする際、ホバー中のファイルを特定するため）。
    /// posはウィンドウ座標系（DragEventArgs.GetPosition(null)と同じ）。</summary>
    private FileItem? GetFileItemAtPosition(Windows.Foundation.Point pos)
    {
        var container = VisualTreeHelper.FindElementsInHostCoordinates(pos, ThumbGridView)
            .Select(el => FindAncestor<GridViewItem>(el))
            .FirstOrDefault(c => c != null);
        if (container == null) return null;
        return ThumbGridView.ItemFromContainer(container) as FileItem;
    }

    /// <summary>複数ファイルへ一括でタグを選択・追加するダイアログ（Python版 _show_tag_picker 相当）</summary>
    private async Task ShowMultiTagPickerAsync(List<long> fileIds)
    {
        var allTags = _db.GetAllTags();
        if (allTags.Count == 0)
        {
            TagActionStatusText.Text = "先にタグを作成してください。";
            return;
        }

        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 300,
            DisplayMemberPath = "Label",
        };
        foreach (var t in allTags)
        {
            listView.Items.Add(new TagDisplayItem
            {
                Id = t.Id,
                Label = t.Name,
                ColorBrush = new SolidColorBrush(TryParseColor(t.Color) ?? Colors.Gray),
            });
        }

        var dialog = new ContentDialog
        {
            Title = $"{fileIds.Count} 件にタグを追加",
            Content = listView,
            PrimaryButtonText = "追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var selectedTags = listView.SelectedItems.OfType<TagDisplayItem>().ToList();
        if (selectedTags.Count == 0) return;

        foreach (var fid in fileIds)
            foreach (var tag in selectedTags)
                _db.AddFileTag(fid, tag.Id);

        foreach (var item in _items.Where(i => fileIds.Contains(i.Id)))
            SetItemTags(item, _db.GetFileTags(item.Id));

        RefreshNavList();
        TagActionStatusText.Text = $"{fileIds.Count} 件に {selectedTags.Count} 件のタグを追加しました。";
    }

    // ── ファイル検索（Python版 _on_search_change / _refresh_files の検索部分相当） ──

    /// <summary>検索ボックスに文字があれば全体を横断検索、なければ現在の
    /// フィルタ/タグ/フォルダ一覧（_currentViewQuery）を再表示する。</summary>
    private void RefreshFilesView()
    {
        if (_db == null) return;
        var query = SearchTextBox?.Text?.Trim() ?? "";
        List<FileRecord> records;
        string label;
        if (!string.IsNullOrEmpty(query))
        {
            if (_currentFolderPath != null)
            {
                // フォルダを開いている間は、DB全体を横断検索する_db.SearchFilesではなく、
                // 今開いているフォルダの中身（_currentViewQuery＝BuildFolderRecords。
                // DB未登録のファイルも含む）だけをファイル名でその場絞り込みする。
                // 以前はフォルダ表示中でも常に_db.SearchFilesを使っていたため、
                // DB未登録（タグ付け等を一度もしていない）ファイルは検索結果に
                // 一切出てこず、「フォルダを開くと検索できない」ように見えていた。
                var keywords = query.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                records = _currentViewQuery()
                    .Where(r => !r.IsFolder && keywords.All(k =>
                        r.Filename.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                        r.Comment.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            else
            {
                records = _db.SearchFiles(query);
            }
            label = $"検索: \"{query}\"";
        }
        else
        {
            records = _currentViewQuery();
            label = _currentViewLabel;
        }

        LoadRecordsIntoGrid(records);
        StatusText.Text = $"{records.Count} 件 ({label})";
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFilesView();
    }

    /// <summary>タグ切り替え専用: 即座に切り替えると、まだキャッシュされていないサムネが
    /// 1件ずつ非同期に読み込まれる間、一瞬コマ抜け（未読込のセル）が見えてしまう。
    /// これを避けるため、実際にグリッドを切り替える前に裏で先頭側のサムネ生成を進めて
    /// キャッシュへ温めておき、準備ができてから切り替える（固定の待ち時間は入れない）。
    /// その間にさらに別のタグへ切り替えられた場合は、世代カウンタで古い方の処理を破棄する。</summary>
    private async void RefreshFilesViewWithPrefetch()
    {
        var myGeneration = ++_viewSwitchGeneration;

        // 検索中は対象外（検索語入力のたびに遅延が入るとタイプ操作がもたつくため、
        // こちらは従来通り即座に切り替える）。
        var query = SearchTextBox?.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(query))
        {
            RefreshFilesView();
            return;
        }

        try
        {
            var records = _currentViewQuery();
            // 表示順（並替設定を適用した後の順序）の先頭からでないと、
            // 実際に画面へ最初に現れる範囲と先読み対象がずれてしまう
            // （例: 名前順/追加日順などaccessed以外の並替時）。
            var displayOrder = ApplySort(records);
            // 先読みが終わるまで無制限に待つと、未キャッシュのファイルばかりのタブでは
            // シェルAPI呼び出し（1件ずつ）が積み重なってタブ切り替えが数秒単位で
            // ブロックされてしまう。そこで上限時間を設け、間に合わなければ
            // 「先読みが済んだ分だけキャッシュに乗った状態」で見切り発車して切り替える
            // （残りは従来通りLoadThumbnailAsync側が表示時に個別で読み込む）。
            const int prefetchTimeoutMs = 150;
            var prefetchTask = PrefetchThumbnailsAsync(displayOrder, myGeneration);
            await Task.WhenAny(prefetchTask, Task.Delay(prefetchTimeoutMs));
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        if (myGeneration != _viewSwitchGeneration) return; // 先読み中に別の切替が発生した

        RefreshFilesView();
    }

    /// <summary>切替先の一覧のうち、最初に画面へ現れるであろう先頭側の一定件数だけ、
    /// サムネイル画像を先にキャッシュへ生成しておく（実際のGridView表示・読み込みは行わない）。
    /// 全件を先読みすると件数の多いタグで待ち時間が伸びてしまうため件数に上限を設けている。</summary>
    private async Task PrefetchThumbnailsAsync(List<FileRecord> records, int myGeneration)
    {
        // キャッシュを作らないフォルダ（ボタン/ドラッグ＆ドロップで開いたフォルダ）は
        // 先読みしてもディスクキャッシュへ乗らず何の得にもならないため、素通りする
        // （実際の表示はLoadThumbnailAsync側が表示時にその場で生成する）。
        if (!ThumbnailCacheAllowed) return;

        const int prefetchCount = 30;
        const int maxConcurrency = 4;

        var bgHex = $"{_bgColor.R:X2}{_bgColor.G:X2}{_bgColor.B:X2}";
        var (targetW, targetH) = GetThumbTargetSize();
        var targets = records.Take(prefetchCount).ToList();

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = targets.Select(async rec =>
        {
            if (myGeneration != _viewSwitchGeneration) return;
            await semaphore.WaitAsync();
            try
            {
                if (myGeneration != _viewSwitchGeneration) return;
                var key = ThumbnailCache.MakeKey(rec.Path, targetW, targetH, bgHex, DurationCacheExtra);
                if (_thumbCache.Get(key) != null) return; // 既にキャッシュ済みなら何もしない

                var data = await ThumbnailGenerator.GenerateAsync(rec.Path, targetW, targetH, _bgColor, default, _config.ShowVideoDuration);
                if (data != null && myGeneration == _viewSwitchGeneration)
                    _thumbCache.Set(key, data);
            }
            catch
            {
                // 先読み失敗は無視する（実際の表示時にLoadThumbnailAsyncが改めて読み込みを試みる）
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private void LoadRecordsIntoGrid(List<FileRecord> records)
    {
        _lastRecords = records;
        var sorted = ApplySort(records);
        // 通常は並替ドロップダウンで「開いた回数」を選んだ時だけバッジを出すが、
        // 左のフィルタ一覧で「よく使うファイル」を開いている間は、並替設定によらず
        // 開いた回数がひと目でわかるようバッジを表示する。
        // 設定で「常に表示」が選ばれている場合は、これらの条件によらず常時バッジを出す。
        UiSettings.Instance.ShowOpenCountBadge =
            _config.OpenCountBadgeMode == "always" ||
            _config.SortKey == "open_count" || _currentFilterKey == "most_opened";

        // 「すべてのファイル」（大量件数、GridViewが仮想化パネルに数千件分のコンテナ管理情報を
        // 保持している状態）から、件数が大きく異なるタグ表示等へ切り替えると、
        // ItemsSourceにぶら下げたままClear()→Add()を連打する形になり、
        // GridViewの仮想化パネル（ネイティブ側）がその変更通知の連打に耐えられず、
        // 管理コードの例外にすらならない形（crash.logにも残らない、無言の強制終了）で
        // 落ちることがある。これを避けるため、差し替え中は一旦ItemsSourceを外し、
        // 中身を入れ替え終えてから改めて付け直す。
        // ItemsSourceをまとめて外す際、GridViewが個々のコンテナに対して
        // ContainerContentChanging(InRecycleQueue)を律儀に呼んでくれるとは限らないため、
        // 残っている読み込みトークンはここで明示的にキャンセルしておく（サムネ読み込みの
        // 完了時に、既に破棄されたコンテナへアクセスしようとする不整合を防ぐため）。
        foreach (var cts in _loadTokens.Values) cts.Cancel();
        _loadTokens.Clear();
        _dragStartingHooked.Clear();

        ThumbGridView.ItemsSource = null;
        try
        {
            _items.Clear();
            var tagsMap = _db.GetTagsForFiles(sorted.Select(r => r.Id).ToList());
            _itemTagsCache = tagsMap;

            foreach (var rec in sorted)
            {
                var tags = tagsMap.TryGetValue(rec.Id, out var t) ? t : new List<TagRecord>();
                var item = new FileItem
                {
                    Id = rec.Id,
                    Path = rec.Path,
                    DisplayName = rec.Filename,
                    Extension = Path.GetExtension(rec.Path),
                    Star = rec.Star,
                    OpenCount = rec.OpenCount,
                    IsFolder = rec.IsFolder,
                };
                SetItemTags(item, tags);
                _items.Add(item);
            }
        }
        finally
        {
            // 入れ替え途中で例外が起きても、必ずItemsSourceを再アタッチしてグリッドが
            // 空のまま操作不能になるのを防ぐ。
            ThumbGridView.ItemsSource = _items;
        }
    }

    /// <summary>Python版 _refresh_files のソート部分の移植。"accessed"はDBのデフォルト順のまま。</summary>
    private List<FileRecord> ApplySort(List<FileRecord> records)
    {
        return _config.SortKey switch
        {
            "name" => records.OrderBy(f => f.Filename, StringComparer.OrdinalIgnoreCase).ToList(),
            "name_desc" => records.OrderByDescending(f => f.Filename, StringComparer.OrdinalIgnoreCase).ToList(),
            "star" => records.OrderByDescending(f => f.Star).ToList(),
            "added" => records.OrderByDescending(f => f.AddedAt, StringComparer.Ordinal).ToList(),
            "open_count" => records.OrderByDescending(f => f.OpenCount).ToList(),
            "win_mtime" => records.OrderByDescending(f => GetMtimeSafe(f.Path)).ToList(),
            "size" => records.OrderByDescending(f => GetFileSizeSafe(f.Path)).ToList(),
            "duration" => records.OrderByDescending(f => GetDurationSafe(f.Path)).ToList(),
            _ => records, // "accessed": DBのデフォルト順そのまま
        };
    }

    private static DateTime GetMtimeSafe(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static long GetFileSizeSafe(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>動画の再生時間を並替用に取得する。同じセッション中は結果をキャッシュし、
    /// 一度取得したファイルの2回目以降のソートでシェルAPIを呼び直さないようにする
    /// （再生時間の取得自体は動画1本ずつ同期的に行うため、初回のソートは件数が多い・
    /// 動画が多いフォルダほど時間がかかることがある）。動画以外のファイルは常に0扱いになる。</summary>
    private static readonly Dictionary<string, TimeSpan> _durationSortCache = new(StringComparer.OrdinalIgnoreCase);

    private static TimeSpan GetDurationSafe(string path)
    {
        if (_durationSortCache.TryGetValue(path, out var cached)) return cached;

        var result = TimeSpan.Zero;
        try
        {
            if (Services.ThumbnailGenerator.VideoExts.Contains(System.IO.Path.GetExtension(path)))
            {
                var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                var props = file.Properties.GetVideoPropertiesAsync().AsTask().GetAwaiter().GetResult();
                result = props.Duration;
            }
        }
        catch
        {
            // 取得失敗（未対応コーデック等）は0扱いにする
        }
        _durationSortCache[path] = result;
        return result;
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortComboBox.SelectedItem is not string label) return;
        var key = SortOptions.FirstOrDefault(o => o.Label == label).Key ?? "accessed";
        if (key == _config.SortKey) return;
        _config.SortKey = key;
        _config.Save();
        LoadRecordsIntoGrid(_lastRecords);
    }

    /// <summary>トップバーの「表示サイズ」スライダー変更時。UiSettings.ThumbCellSizeへの反映自体は
    /// XAML側のTwoWayバインディングでライブに行われる（グリッドのセルサイズが即座に変わる）ので、
    /// ここでは設定ファイルへの保存に加えて、セル幅が変わったことでタグチップの表示可能数も
    /// 変わるため、少し待ってから（ドラッグ中の連続発火をまとめて）チップを再計算する。</summary>
    private void ThumbGridSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_config.ThumbGridCellSize != e.NewValue)
        {
            _config.ThumbGridCellSize = e.NewValue;
            _config.Save();
        }

        _tagChipResizeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _tagChipResizeTimer.Stop();
        _tagChipResizeTimer.Tick -= TagChipResizeTimer_Tick;
        _tagChipResizeTimer.Tick += TagChipResizeTimer_Tick;
        _tagChipResizeTimer.Start();
    }

    private void TagChipResizeTimer_Tick(object? sender, object e)
    {
        _tagChipResizeTimer?.Stop();
        RefreshAllTagChipWidths();
    }

    /// <summary>表示中の全アイテムについて、現在のセル幅に基づきタグチップの表示数を再選定する
    /// （DBへは問い合わせず、_itemTagsCache に保持済みの完全なタグ一覧から間引き直すだけ）。</summary>
    private void RefreshAllTagChipWidths()
    {
        foreach (var item in _items)
        {
            var tags = _itemTagsCache.TryGetValue(item.Id, out var t) ? t : new List<TagRecord>();
            SetItemTags(item, tags);
        }
    }

    /// <summary>Python版と同じく、タグ表記の下地をタグの色で塗る。前景色は背景輝度から白/黒を自動選択。
    /// タグが多いとセル幅に収まりきらないため、重ねずに左詰めで収まる件数だけを表示し、
    /// あふれる分は追加しない（中途半端に見切れたり重なったりするチップを出さないため）。
    /// タグチップサイズ設定が「小」の時はTagChipTwoRowsがtrueになり、1行目に収まらなかった分を
    /// 2行目（item.Tags2）にも収まるだけ詰める。</summary>
    /// <summary>「…」チップは他のチップより間隔を詰め、表示領域を節約する。</summary>
    private const double EllipsisGap = 1;

    private static void SetItemTags(FileItem item, List<TagRecord> tags)
    {
        item.Tags.Clear();
        item.Tags2.Clear();
        var floorChars = ResolveTagChipMaxChars();
        // タグが1件しか付いていない場合は省略せず全文字表示する
        var singleFull = tags.Count == 1;
        var maxLines = UiSettings.Instance.TagChipTwoRows ? 2 : 1;

        // タグが3件以上ある場合は、まずフルネームの幅で詰め込んでみる
        // （＝ SelectFittingTagLines に singleFull:true を渡す）。
        // それで全部収まるならそのままフル表示、収まらない場合は「…」チップを足す前に
        // floorChars（4文字）制限で計算し直し、1件あたりの幅を切り詰めることで
        // できるだけ多くのタグを詰め込んでから、残りを「…」にまとめる。
        var tryFullNames = singleFull || tags.Count >= 3;

        List<List<TagRecord>> lines;
        int maxChars;
        bool useFullNames;
        if (tryFullNames)
        {
            var fullLines = SelectFittingTagLines(tags, int.MaxValue, true, maxLines);
            var fullShown = fullLines.Sum(l => l.Count);
            if (singleFull || fullShown >= tags.Count)
            {
                lines = fullLines;
                maxChars = int.MaxValue;
                useFullNames = true;
            }
            else
            {
                // 表示領域に余裕がない場合は4文字制限で計算し直し、より多くのタグを詰め込む。
                maxChars = floorChars;
                lines = SelectFittingTagLines(tags, maxChars, false, maxLines);
                useFullNames = false;
            }
        }
        else
        {
            // タグが2件以下の場合は従来通り、余裕があれば文字数を伸ばす方式を維持する。
            maxChars = ResolveDynamicMaxChars(tags, floorChars, maxLines);
            lines = SelectFittingTagLines(tags, maxChars, false, maxLines);
            useFullNames = false;
        }

        var shownCount = lines.Sum(l => l.Count);
        var truncated = shownCount < tags.Count;

        // あふれた分がある場合、最後の行の末尾に「…」チップを表示できるよう、
        // 収まらなくなるまで最後の行の末尾タグを間引いて場所を空ける。
        if (truncated && lines.Count > 0)
        {
            TrimLastLineForEllipsis(lines[^1], maxChars, useFullNames);
        }

        void Fill(ObservableCollection<TagChip> target, List<TagRecord> lineTags, bool isLastLine)
        {
            for (int i = 0; i < lineTags.Count; i++)
            {
                var t = lineTags[i];
                var bg = TryParseColor(t.Color) ?? Colors.Gray;
                var luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                var fg = luminance < 0.55 ? Colors.White : Colors.Black;
                var display = useFullNames || t.Name.Length <= maxChars ? t.Name : t.Name[..maxChars] + "…";
                target.Add(new TagChip
                {
                    Name = t.Name,
                    DisplayName = display,
                    Background = new SolidColorBrush(bg),
                    Foreground = new SolidColorBrush(fg),
                    Margin = new Thickness(i == 0 ? 0 : ChipGap, 0, 0, 0),
                });
            }

            if (isLastLine && truncated)
            {
                target.Add(new TagChip
                {
                    Name = "…",
                    DisplayName = "…",
                    Background = new SolidColorBrush(Colors.Transparent),
                    Foreground = new SolidColorBrush(Colors.Gray),
                    Margin = new Thickness(lineTags.Count == 0 ? 0 : EllipsisGap, 0, 0, 0),
                });
            }
        }

        if (lines.Count > 0) Fill(item.Tags, lines[0], isLastLine: lines.Count == 1);
        if (lines.Count > 1) Fill(item.Tags2, lines[1], isLastLine: true);
    }

    /// <summary>末尾に「…」チップ1個ぶんの幅が収まるよう、行の末尾からタグを間引く。
    /// 幅の見積もりは SelectFittingTagLines/ChipWidth と同じ切り詰めルール（maxChars, useFullNames）を
    /// 使う必要がある。ここでフルネームの文字数を使ってしまうと、実際の表示幅より過大に見積もり、
    /// 収まるはずのタグまで間引かれてしまう（表示領域に余裕があるのに「…」になる原因）。</summary>
    private static void TrimLastLineForEllipsis(List<TagRecord> lastLine, int maxChars, bool useFullNames)
    {
        const double horizontalPadding = 10; // Border Padding="5,0" の左右合計
        var availableWidth = UiSettings.Instance.ThumbCellWidth - 10;
        var fontSize = UiSettings.Instance.FileListTagsFontSize;
        var ellipsisWidth = fontSize * CharWidthFactor * 1 + horizontalPadding;

        double ChipDisplayLength(TagRecord t) =>
            useFullNames ? t.Name.Length : Math.Min(t.Name.Length, maxChars);

        double LineWidth(List<TagRecord> line) =>
            line.Count == 0
                ? 0
                : line.Select((t, i) => (i > 0 ? ChipGap : 0) + fontSize * CharWidthFactor * ChipDisplayLength(t) + horizontalPadding).Sum();

        while (lastLine.Count > 0 &&
               LineWidth(lastLine) + (lastLine.Count > 0 ? EllipsisGap : 0) + ellipsisWidth > availableWidth)
        {
            lastLine.RemoveAt(lastLine.Count - 1);
        }
    }

    /// <summary>タグチップサイズ設定に応じた表示文字数の最低ライン（＝あふれる時のフォールバック値）。
    /// 「小」は7文字、「中」「大」は今まで通り4文字。実際の値はUiSettings.Instance.TagChipMaxCharsに
    /// 保持されており（SettingsButton_Click保存時・起動時に更新）、静的メソッドから参照できるようにしている。</summary>
    private static int ResolveTagChipMaxChars() => UiSettings.Instance.TagChipMaxChars;

    /// <summary>タグ数が少なく表示領域に余裕がある場合、floorChars（設定上の最低文字数）より
    /// 表示文字数を増やせるだけ増やす。全タグ名のうち最長のものまで1文字ずつ試し、
    /// 全タグを表示しきれる（あふれて非表示になるタグが出ない）最大の文字数を返す。
    /// 1文字でも増やすと1件でもあふれてしまう場合はfloorCharsのまま返す
    /// （＝「タグが多く表示しきれない時は今と同じ4文字（小の場合7文字）」の挙動）。</summary>
    private static int ResolveDynamicMaxChars(List<TagRecord> tags, int floorChars, int maxLines)
    {
        if (tags.Count == 0) return floorChars;
        var maxNameLength = tags.Max(t => t.Name.Length);
        if (maxNameLength <= floorChars) return floorChars;

        var best = floorChars;
        for (var chars = floorChars + 1; chars <= maxNameLength; chars++)
        {
            var shown = SelectFittingTagLines(tags, chars, false, maxLines).Sum(l => l.Count);
            // 文字数を増やすほど1件あたりの幅が広がり収まる件数は単調非増加になるため、
            // 一度でも全件収まらなくなった時点で打ち切ってよい。
            if (shown < tags.Count) break;
            best = chars;
        }
        return best;
    }

    /// <summary>チップ同士の固定の隙間（左詰め・重なりなし）。</summary>
    private const double ChipGap = 4;

    /// <summary>「フォントサイズ×文字数」だけだと実際のグリフ幅（特に半角英数字）より
    /// かなり大きく見積もってしまい、本来まだ入るはずのタグまで「…」に回されてしまう
    /// （＝「…」が表示領域の右端まで来ない）原因になっていた。実測に近づけるための補正係数。
    /// 1文字あたりの実効幅 ≒ fontSize * CharWidthFactor として、ChipWidth/LineWidth/ellipsisWidthの
    /// 全ての幅見積もりで統一して使う。</summary>
    private const double CharWidthFactor = 0.85;

    /// <summary>セル幅（現在の表示サイズ設定 UiSettings.ThumbCellSize から安全マージン10pxを引いた分）に、
    /// チップを左詰め・重なりなしで並べたとき、実際の文字数から見積もった幅で収まる分だけを、
    /// 行ごと（最大maxLines行）に分けて選ぶ。1行に収まらなくなったチップは次の行の先頭に回す。
    /// maxDisplayChars はタグチップサイズ設定により変わる（小=7文字／中・大=4文字）。
    /// singleFull が true（タグが1件のみ）の場合は切り詰めずフルネームの幅で見積もる。</summary>
    private static List<List<TagRecord>> SelectFittingTagLines(
        List<TagRecord> tags, int maxDisplayChars, bool singleFull, int maxLines)
    {
        const double horizontalPadding = 10; // Border Padding="5,0" の左右合計
        var availableWidth = UiSettings.Instance.ThumbCellWidth - 10;
        var fontSize = UiSettings.Instance.FileListTagsFontSize;

        double ChipWidth(string name) =>
            fontSize * CharWidthFactor * (singleFull ? name.Length : Math.Min(name.Length, maxDisplayChars)) + horizontalPadding;

        var lines = new List<List<TagRecord>>();
        var current = new List<TagRecord>();
        double usedWidth = 0;

        foreach (var t in tags)
        {
            var w = ChipWidth(t.Name);
            var needed = usedWidth + (current.Count > 0 ? ChipGap : 0) + w;
            if (needed > availableWidth)
            {
                // すでに最後に許された行にいる場合、このタグだけ幅が足りなくても行を打ち切らず、
                // 後続にもっと短い（幅の狭い）タグがあれば引き続き試す。こうしないと、
                // 1件だけたまたま長いタグに当たった時点で行が終わってしまい、後ろにまだ
                // 入るはずのタグが残っているのに表示領域が余ってしまう。
                if (lines.Count == maxLines - 1)
                {
                    continue;
                }

                if (current.Count == 0)
                {
                    // 1個も入らない極端な大フォント時の保険として、その行には最低1個だけ表示する
                    current.Add(t);
                    lines.Add(current);
                    current = new List<TagRecord>();
                    usedWidth = 0;
                    if (lines.Count >= maxLines) break;
                    continue;
                }

                lines.Add(current);
                if (lines.Count >= maxLines) return lines;

                // 収まらなかった分は次の行の先頭として測り直す
                current = new List<TagRecord> { t };
                usedWidth = ChipWidth(t.Name);
                continue;
            }

            current.Add(t);
            usedWidth = needed;
        }

        if (current.Count > 0) lines.Add(current);

        // 保険: 1個も入らない極端な大フォント等で結果が空になった場合、
        // 何も表示されないと不自然なので、最も幅が狭い1件だけは強制的に表示する。
        if (lines.Count == 0 && tags.Count > 0)
        {
            var smallest = tags.OrderBy(t => ChipWidth(t.Name)).First();
            lines.Add(new List<TagRecord> { smallest });
        }

        return lines;
    }

    /// <summary>Python版のタグナビゲーション構築（グループヘッダー→グループ内タグ→未分類タグの順）を移植。</summary>
    /// <summary>タグ・フォルダの使い方説明（TagListView内オーバーレイ）を表示するかどうか。
    /// NavRowsが1件でもあれば（タグ・フォルダのどちらかが登録済みなら）非表示にする。
    /// x:Bindのメソッド呼び出しバインディングで、NavRows.Countの変化に合わせて自動的に再評価される。</summary>
    private Visibility EmptyNavHintVisibility(int navRowCount) =>
        navRowCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshNavList()
    {
        NavRows.Clear();

        var allFolders = _db.GetAllFolders();
        var allTags = _db.GetAllTags(_config.TagSortKey); // 件数順/名前順/追加順（_config.TagSortKeyで切替）
        var allGroups = _db.GetAllTagGroups(_config.GroupSortKey); // グループの並びはタグとは独立に切替可能

        // フォルダもタグと同じくグループへ分類できるようにする。
        var groupedFolders = allGroups.ToDictionary(g => g.Id, _ => new List<FolderRecord>());
        var ungroupedFolders = new List<FolderRecord>();
        foreach (var f in allFolders)
        {
            if (f.GroupId.HasValue && groupedFolders.TryGetValue(f.GroupId.Value, out var flist)) flist.Add(f);
            else ungroupedFolders.Add(f);
        }

        // グループ未設定のフォルダショートカット（従来どおり）は一覧の先頭に並べる。
        foreach (var f in ungroupedFolders)
            NavRows.Add(MakeFolderRow(f));

        var grouped = allGroups.ToDictionary(g => g.Id, _ => new List<TagRecord>());
        var ungrouped = new List<TagRecord>();
        foreach (var t in allTags)
        {
            if (t.GroupId.HasValue && grouped.TryGetValue(t.GroupId.Value, out var list)) list.Add(t);
            else ungrouped.Add(t);
        }

        foreach (var g in allGroups)
        {
            NavRows.Add(new NavRow
            {
                IsGroupHeader = true,
                GroupId = g.Id,
                Label = g.Name,
                CollapseGlyph = g.Collapsed ? "▶" : "▼",
                GlyphVisibility = Visibility.Visible,
                DotVisibility = Visibility.Collapsed,
                LabelWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (!g.Collapsed)
            {
                foreach (var f in groupedFolders[g.Id]) NavRows.Add(MakeFolderRow(f, indent: 8));
                foreach (var t in grouped[g.Id]) NavRows.Add(MakeTagRow(t, indent: 8));
            }
        }

        if (ungrouped.Count > 0)
        {
            if (allGroups.Count > 0)
            {
                NavRows.Add(new NavRow
                {
                    IsGroupHeader = true,
                    IsUngroupedHeader = true,
                    Label = "（未分類）",
                    GlyphVisibility = Visibility.Collapsed,
                    DotVisibility = Visibility.Collapsed,
                    LabelWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
            }
            foreach (var t in ungrouped)
                NavRows.Add(MakeTagRow(t, indent: allGroups.Count > 0 ? 8 : 0));
        }

        // NavRows.CountをEmptyNavHintVisibility()で参照するx:Bindは、ObservableCollectionの
        // CollectionChanged経由の暗黙の再評価に頼っており、環境によっては即座に反映されない
        // ことがあるため、ここで明示的にBindings.Update()を呼んで確実に再評価させる。
        Bindings.Update();
    }

    private static NavRow MakeFolderRow(FolderRecord f, double indent = 0) => new()
    {
        IsGroupHeader = false,
        IsFolder = true,
        FolderId = f.Id,
        FolderPath = f.Path,
        Name = f.Name,
        Label = f.Name,
        Indent = new Thickness(indent, 0, 0, 0),
        GlyphVisibility = Visibility.Collapsed,
        DotVisibility = Visibility.Collapsed,
        FolderGlyphVisibility = Visibility.Visible,
        LabelWeight = UiSettings.Instance.TagFontWeight,
    };

    private static NavRow MakeTagRow(TagRecord t, double indent)
    {
        var color = TryParseColor(t.Color) ?? Colors.Gray;
        return new NavRow
        {
            IsGroupHeader = false,
            TagId = t.Id,
            Name = t.Name,
            GridShape = t.GridShape,
            Label = t.Name,
            CountText = t.FileCount.ToString(),
            CountVisibility = Visibility.Visible,
            ColorBrush = new SolidColorBrush(color),
            Indent = new Thickness(indent, 0, 0, 0),
            GlyphVisibility = Visibility.Collapsed,
            DotVisibility = Visibility.Visible,
            LabelWeight = UiSettings.Instance.TagFontWeight,
        };
    }

    private static Windows.UI.Color? TryParseColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return null;
            byte r = Convert.ToByte(hex[..2], 16);
            byte g = Convert.ToByte(hex[2..4], 16);
            byte b = Convert.ToByte(hex[4..6], 16);
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }
        catch { return null; }
    }

    // ── 選択中アイテムへのタグ付け / お気に入り切替 ─────────────────────────
    private void AddTagButton_Click(object sender, RoutedEventArgs e)
    {
        // グループ名入力欄が開いていた場合は閉じておく（同時に両方開かないようにする）
        NewGroupTextBox.Text = "";
        NewGroupTextBox.Visibility = Visibility.Collapsed;

        // 1回目のクリック：入力欄を表示するだけ（まだタグは作成しない）
        if (NewTagTextBox.Visibility != Visibility.Visible)
        {
            NewTagTextBox.Visibility = Visibility.Visible;
            NewTagTextBox.Focus(FocusState.Programmatic);
            return;
        }

        SubmitNewTag();
    }

    /// <summary>「グループ+」ボタン：1回目のクリックで入力欄を表示するだけ（AddTagButton_Clickと同じ方式）</summary>
    private async void AddGroupButton_Click(object sender, RoutedEventArgs e)
    {
        // タグ名入力欄が開いていた場合は閉じておく（同時に両方開かないようにする）
        NewTagTextBox.Text = "";
        NewTagTextBox.Visibility = Visibility.Collapsed;

        if (NewGroupTextBox.Visibility != Visibility.Visible)
        {
            NewGroupTextBox.Visibility = Visibility.Visible;
            NewGroupTextBox.Focus(FocusState.Programmatic);
            return;
        }

        await SubmitNewGroupAsync();
    }

    /// <summary>ウィンドウ内へドラッグが入った時に、タグエリアの受け皿（TagDropOverlay）を
    /// 有効化する。普段はIsHitTestVisible=Falseにしてあるのでクリック・右クリックの邪魔をしないが、
    /// ドラッグ中だけ受け付けられるようにし、TagListView内部ScrollViewerの既定の自動スクロール
    /// （感度調整不可）ではなく、こちらの自前の自動スクロールが使われるようにする。</summary>
    private void RootGrid_DragEnter(object sender, DragEventArgs e)
    {
        TagDropOverlay.IsHitTestVisible = true;
    }

    /// <summary>ドラッグがウィンドウの外へ出た時に受け皿を無効化する。
    /// （TagDropOverlay内でのDragLeave/Dropでも無効化しているが、ウィンドウ外へ出て
    /// ドラッグ自体が終わるケースの保険として、ここでも止める。）</summary>
    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        TagDropOverlay.IsHitTestVisible = false;
        StopTagAutoScrollTimer();
        HideTagHoverChip();
    }

    /// <summary>タグ名入力欄・グループ名入力欄が表示されている状態で、それぞれの入力欄・
    /// 対応するボタン以外の場所がクリックされたら自動的に非表示にする（内容は破棄する）。</summary>
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;

        if (NewTagTextBox.Visibility == Visibility.Visible &&
            !IsWithin(originalSource, NewTagTextBox) && !IsWithin(originalSource, AddTagButton))
        {
            NewTagTextBox.Text = "";
            NewTagTextBox.Visibility = Visibility.Collapsed;
        }

        if (NewGroupTextBox.Visibility == Visibility.Visible &&
            !IsWithin(originalSource, NewGroupTextBox) && !IsWithin(originalSource, AddGroupButton))
        {
            NewGroupTextBox.Text = "";
            NewGroupTextBox.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>指定した要素が、あるルート要素の子孫（またはルート自身）かどうかを調べる</summary>
    private static bool IsWithin(DependencyObject? element, DependencyObject root)
    {
        while (element != null)
        {
            if (ReferenceEquals(element, root)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>タグ名入力欄でEnterキーを押した場合も送信できるようにする</summary>

    private void NewTagTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SubmitNewTag();
            e.Handled = true;
        }
    }

    /// <summary>入力欄のタグ名で新規タグを作成し、選択中のファイルに追加する。
    /// 完了後は入力欄を再び非表示にする（Grid.Row=3のスペースを普段は取らないようにするため）。</summary>
    private void SubmitNewTag()
    {
        var name = NewTagTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            TagActionStatusText.Text = "タグ名を入力してください。";
            return;
        }

        var selectedFiles = ThumbGridView.SelectedItems.OfType<FileItem>().ToList();
        if (selectedFiles.Count == 0)
        {
            TagActionStatusText.Text = "先にグリッドでファイルを選択してください（タグ自体は作成されました）。";
        }

        var tagId = _db.AddTag(name);
        if (tagId == null)
        {
            TagActionStatusText.Text = "タグの作成に失敗しました。";
            return;
        }

        foreach (var item in selectedFiles)
        {
            EnsureRegistered(item);
            _db.AddFileTag(item.Id, tagId.Value);
            var tags = _db.GetFileTags(item.Id);
            SetItemTags(item, tags);
        }

        if (selectedFiles.Count > 0)
        {
            TagActionStatusText.Text = $"{selectedFiles.Count} 件のファイルに「{name}」を追加しました。";
        }

        NewTagTextBox.Text = "";
        NewTagTextBox.Visibility = Visibility.Collapsed;
        RefreshNavList();
    }

    /// <summary>グループ名入力欄でEnterキーを押した場合も送信できるようにする</summary>
    private async void NewGroupTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await SubmitNewGroupAsync();
            e.Handled = true;
        }
    }

    /// <summary>入力欄のグループ名で新規グループを作成する。
    /// 完了後は入力欄を再び非表示にする（タグ+ボタンのSubmitNewTagと同じ方式）。</summary>
    private async Task SubmitNewGroupAsync()
    {
        var name = NewGroupTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            TagActionStatusText.Text = "グループ名を入力してください。";
            return;
        }

        _db.AddTagGroup(name);
        NewGroupTextBox.Text = "";
        NewGroupTextBox.Visibility = Visibility.Collapsed;
        RefreshNavList();
        await Task.CompletedTask;
    }

    // ── サムネイル仮想化ロード（前回までと同じ） ─────────────────────────
    private void ThumbGridView_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = (GridViewItem)args.ItemContainer;

        if (_dragStartingHooked.Add(container))
            container.DragStarting += ThumbGridViewItem_DragStarting;

        if (args.InRecycleQueue)
        {
            if (_loadTokens.TryGetValue(container, out var oldCts))
            {
                oldCts.Cancel();
                _loadTokens.Remove(container);
            }
            if (FindThumbImage(container) is { } img0)
                img0.Source = null;
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(ThumbGridView_ContainerContentChanging);
            return;
        }

        if (args.Item is not FileItem item) return;
        var image = FindThumbImage(container);
        if (image == null) return;

        var cts = new CancellationTokenSource();
        _loadTokens[container] = cts;
        _ = LoadThumbnailAsync(item, image, cts.Token);
    }

    private static XamlImage? FindThumbImage(GridViewItem container)
    {
        return container.ContentTemplateRoot is FrameworkElement root
            ? root.FindName("ThumbImage") as XamlImage
            : null;
    }

    /// <summary>現在のグリッド形状（正方形/縦長/横長）に合わせて、サムネイル生成の目標
    /// 幅・高さを決める。_thumbSize（設定の「サムネイル解像度」）を長辺の基準として、
    /// UiSettings.GridShapeRatios の比率で短辺を縮める（UiSettings.ThumbCellWidth/Heightと同じ考え方）。
    /// こうすることで、サムネイル画像自体が最初からセルの縦横比に合わせて生成され、
    /// 正方形パディングによる余白でセルいっぱいに表示されない問題を避けられる。</summary>
    private (int Width, int Height) GetThumbTargetSize()
    {
        var shape = UiSettings.Instance.GridShape;
        var (w, h) = UiSettings.GridShapeRatios.TryGetValue(shape, out var r) ? r : (1.0, 1.0);
        var width = w >= h ? _thumbSize : (int)Math.Round(_thumbSize * (w / h));
        var height = h >= w ? _thumbSize : (int)Math.Round(_thumbSize * (h / w));
        return (Math.Max(1, width), Math.Max(1, height));
    }

    // スクロールバーを掴んで一気にドラッグすると、コンテナが数ms単位で生成→即リサイクルを
    // 繰り返す。以前は「生成を開始してからキャンセルする」実装だったため、開始〜キャンセルの
    // 間のわずかな時間にシェルAPI(GetThumbnailAsync)の呼び出しそのものは発生してしまっており、
    // これが無言のネイティブクラッシュの引き金になっていた（キャンセル処理を丁寧にしても、
    // 「一瞬でも呼んでしまう」こと自体は防げていなかった）。
    // そこで、キャッシュに無い（＝実際にシェルAPI呼び出しが必要な）ファイルについては、
    // コンテナがこの時間だけ画面に留まり続けて初めて生成を開始するようにする。高速ドラッグ中は
    // どのコンテナもこの時間より先にリサイクルされるため、シェルAPI自体が一切呼ばれなくなる。
    private const int ThumbGenerationSettleDelayMs = 20;

    /// <summary>サムネイルキャッシュキーへ混ぜる追加識別子。動画の再生時間表示オン/オフを
    /// 切り替えた際、古い見た目（表示あり/なし）のキャッシュがそのままヒットし続けて
    /// 設定を変えても反映されない、という事態を防ぐために使う。</summary>
    private string DurationCacheExtra => _config.ShowVideoDuration ? "dur1" : "dur0";

    /// <summary>Task.Delay(ms, token)の代わり。キャンセルされても例外を投げず、falseを返すだけにする。
    /// タグ/フォルダ切替時にコンテナが大量リサイクルされ、その分だけ本当に例外が発生してしまうのを避けるため
    /// （デバッガのファーストチャンス例外通知が積み重なり、切替が固まったように見える原因になっていた）。</summary>
    private static async Task<bool> DelayWithoutThrowingAsync(int millisecondsDelay, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        // Task.Delayに直接tokenを渡すと、キャンセル時にTaskCanceledExceptionを投げて完了する。
        // ここではtoken無しでDelayさせ、完了後に改めてキャンセル状態を確認することで例外化を避ける。
        await Task.Delay(millisecondsDelay);
        return !token.IsCancellationRequested;
    }

    /// <summary>SemaphoreSlim.WaitAsync(token)の代わり。短い間隔でポーリングし、キャンセルされても
    /// 例外を投げずfalseを返す（理由はDelayWithoutThrowingAsyncと同じ）。取得できた場合のみtrueを返し、
    /// 呼び出し側は必ずtry/finallyでReleaseすること。</summary>
    private static async Task<bool> WaitSemaphoreWithoutThrowingAsync(SemaphoreSlim semaphore, CancellationToken token, int pollMs = 20)
    {
        while (!token.IsCancellationRequested)
        {
            if (await semaphore.WaitAsync(pollMs)) return true;
        }
        return false;
    }

    private async Task LoadThumbnailAsync(FileItem item, XamlImage imageControl, CancellationToken token)
    {
        try
        {
            var bgHex = $"{_bgColor.R:X2}{_bgColor.G:X2}{_bgColor.B:X2}";
            var (targetW, targetH) = GetThumbTargetSize();
            var key = ThumbnailCache.MakeKey(item.Path, targetW, targetH, bgHex, DurationCacheExtra);
            var cacheAllowed = ThumbnailCacheAllowed;

            byte[]? data = cacheAllowed ? _thumbCache.Get(key) : null;
            if (data == null)
            {
                // キャッシュに無い場合のみ、コンテナが一定時間留まったのを確認してから生成を開始する
                // （キャッシュ済みの表示自体は従来通り即座に行われるため、通常のスクロール時の
                //   体感速度には影響しない）。
                // ★ Task.Delay(ms, token) / SemaphoreSlim.WaitAsync(token) はキャンセルされた瞬間に
                //   OperationCanceledExceptionを「投げる」実装になっている。タグ/フォルダ切替時は
                //   仮想化GridViewのコンテナが数百個単位で一瞬にリサイクルされるため、この方式だと
                //   実際に数百個の例外が発生してしまう（catchしていても投げられること自体は変わらない）。
                //   デバッガ接続時は「スローされた」ファーストチャンス例外の通知だけでも重く、
                //   タグ切替が数秒〜十数秒フリーズしたように見える一因になっていた。
                //   そこで、キャンセルされやすいこの2箇所だけ例外を発生させないポーリング方式に置き換える。
                if (!await DelayWithoutThrowingAsync(ThumbGenerationSettleDelayMs, token)) return;

                if (!await WaitSemaphoreWithoutThrowingAsync(_thumbLoadSemaphore, token)) return;
                try
                {
                    if (token.IsCancellationRequested) return;
                    data = await ThumbnailGenerator.GenerateAsync(item.Path, targetW, targetH, _bgColor, token, _config.ShowVideoDuration);
                }
                finally
                {
                    _thumbLoadSemaphore.Release();
                }
                if (token.IsCancellationRequested) return;
                if (data != null && cacheAllowed) _thumbCache.Set(key, data);
            }
            if (data == null || token.IsCancellationRequested) return;

            var bitmap = new BitmapImage();
            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(data.AsBuffer());
            ras.Seek(0);

            if (token.IsCancellationRequested) return;
            await bitmap.SetSourceAsync(ras);

            if (!token.IsCancellationRequested)
                imageControl.Source = bitmap;
        }
        catch (OperationCanceledException)
        {
            // コンテナがリサイクルされて読み込みがキャンセルされただけ。無視してよい。
        }
        catch (Exception ex)
        {
            // サムネ生成中の例外でアプリ全体が落ちないよう、ここで必ず捕まえてログにだけ残す
            // （コンテナが再利用/破棄された後に非同期処理が完了した場合のUI要素アクセス例外や、
            //   キャンセル済みトークンの状態で発生したその他の例外も含め、無条件にここで止める。
            //   以前は `when (!token.IsCancellationRequested)` というフィルターを付けていたが、
            //   キャンセル後に例外が起きるとフィルターが不成立になり例外がそのまま外へ漏れて
            //   投げっぱなしタスクの未処理例外としてアプリごとクラッシュする不具合があった）。
            App.LogCrash(ex);
        }
    }

    // ── ファイル操作（ダブルクリックで開く／右クリックメニュー） ─────────────────────────

    /// <summary>DependencyObject の祖先を辿って対象の型を探す（GridViewItemコンテナ特定用）</summary>
    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ── スクロール速度を2倍にする ─────────────────────────
    private ScrollViewer? _gridScrollViewer;

    /// <summary>DependencyObjectの子孫を辿って対象の型を探す（VisualTreeHelperの子方向探索）</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ── サイドバー幅のドラッグリサイズ ──────────────────────
    private double? _splitterDragStartX;
    private double _splitterDragStartWidth;

    private void SidebarSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);
        if (!point.Properties.IsLeftButtonPressed) return;

        _splitterDragStartX = point.Position.X;
        _splitterDragStartWidth = SidebarColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void SidebarSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_splitterDragStartX == null) return;

        var x = e.GetCurrentPoint(RootGrid).Position.X;
        var newWidth = _splitterDragStartWidth + (x - _splitterDragStartX.Value);

        // 最小幅・最大幅でクランプ（狭すぎ/広すぎを防止）
        const double minWidth = 140;
        var maxWidth = Math.Max(minWidth, RootGrid.ActualWidth - 300);
        newWidth = Math.Clamp(newWidth, minWidth, maxWidth);

        SidebarColumn.Width = new GridLength(newWidth);
    }

    private void SidebarSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_splitterDragStartX == null) return;
        try { ((UIElement)sender).ReleasePointerCapture(e.Pointer); } catch { /* 既に解放済みでも無視 */ }
        _splitterDragStartX = null;
    }

    // 高速にマウスホイールを回した際、ホイールイベントのたびにChangeViewを呼んでいた
    // 従来実装では、スクロールバーのつまみを高速ドラッグした場合(_customThumbDragThrottle参照)
    // と全く同じ理屈で、GridViewの仮想化パネル(ネイティブ側)へ大量のオフセット変更が
    // 立て続けに送られ、無言クラッシュ(WERのE_UNEXPECTED等、crash.logにも残らない)を
    // 引き起こしていた。ホイールにも同様の間引きを適用する。ドラッグと違い「指を離す」に
    // 相当する確定タイミングが無いが、ホイールは連続してイベントが来る間は間引かれた分も
    // 次のイベントで自然に追いつくため、つまみドラッグのような明示的な最終確定は不要。
    private readonly System.Diagnostics.Stopwatch _wheelThrottle = new();
    private const int WheelThrottleMs = 40; // 約25回/秒までに制限
    private double? _wheelPendingOffset;

    private void ThumbGridView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _gridScrollViewer ??= FindDescendant<ScrollViewer>(ThumbGridView);
        if (_gridScrollViewer == null) return;

        var delta = e.GetCurrentPoint(ThumbGridView).Properties.MouseWheelDelta;
        // 既定のホイール1ノッチ(120)あたりの移動量を通常の約3.5倍に設定
        const double pixelsPerNotchDoubled = 420;

        // 間引かれて実際にはChangeViewが発行されていない間も、狙っているオフセット自体は
        // 毎回のイベントで積み上げておく（そうしないと間引いた分の移動量が失われてしまう）。
        var baseOffset = _wheelPendingOffset ?? _gridScrollViewer.VerticalOffset;
        var maxOffset = Math.Max(0, _gridScrollViewer.ExtentHeight - _gridScrollViewer.ViewportHeight);
        var newOffset = Math.Clamp(baseOffset - (delta / 120.0) * pixelsPerNotchDoubled, 0, maxOffset);
        _wheelPendingOffset = newOffset;
        e.Handled = true; // 既定の(遅い)スクロールと二重に効かないようにする

        if (_wheelThrottle.IsRunning && _wheelThrottle.ElapsedMilliseconds < WheelThrottleMs) return;
        _wheelThrottle.Restart();

        _gridScrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
    }

    /// <summary>ドラッグ開始点を「現在のスクロール位置」でのビューポート座標に補正して返す。
    /// _rbStartViewportはPointerPressed時点の画面上のピクセル位置を保持しているだけなので、
    /// その後オートスクロールでコンテンツが動くと、開始点に対応していた実際のファイルの
    /// 画面上の位置は変わってしまう。ここではドラッグ開始時からのスクロール量の差分だけ
    /// Y座標を補正することで、開始点が常に「同じファイル（コンテンツ位置）」を指すようにする。</summary>
    private Windows.Foundation.Point GetAdjustedRbStart()
    {
        var start = _rbStartViewport!.Value;
        var currentOffset = _gridScrollViewer?.VerticalOffset ?? _rbStartScrollOffset;
        var scrolled = currentOffset - _rbStartScrollOffset; // 下にスクロールした量（正の値）
        return new Windows.Foundation.Point(start.X, start.Y - scrolled);
    }

    // ── ラバーバンド選択（Python版 GridCanvas._on_click / _on_drag / _on_release 相当） ──
    // GridViewが素の状態では「空白からのドラッグで矩形選択」をサポートしていないため、
    // オーバーレイCanvas(RubberBandCanvas/RubberBandRect)を重ねて自前で矩形とヒットテストを実装する。

    private void ThumbGridView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ThumbGridView);
        if (!point.Properties.IsLeftButtonPressed) return;

        // アイテム自体をクリックした場合はGridView標準の選択（Ctrl/Shiftクリック等）に任せる
        var src = e.OriginalSource as DependencyObject;
        if (src != null && FindAncestor<GridViewItem>(src) != null) return;

        _gridScrollViewer ??= FindDescendant<ScrollViewer>(ThumbGridView);
        var pos = point.Position;

        _rbStartViewport = pos;
        _rbStartScrollOffset = _gridScrollViewer?.VerticalOffset ?? 0;
        _rbActive = false;

        // Ctrlを押していなければ空白クリックでいったん選択解除（Python版 _on_click の self._sel.clear() 相当）
        var ctrl = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        if (!ctrl) ThumbGridView.SelectedItems.Clear();

        ThumbGridView.CapturePointer(e.Pointer);
    }

    private void ThumbGridView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rbStartViewport == null) return;
        var pos = e.GetCurrentPoint(ThumbGridView).Position;
        _rbLastPointerPos = pos; // 端で静止したままでも自動スクロールを続けられるよう毎回記録

        if (!_rbActive)
        {
            // 微小な移動ではまだドラッグと判定しない（誤選択防止。Python版と同じく5px閾値）
            if (Math.Abs(pos.X - _rbStartViewport.Value.X) < 5 &&
                Math.Abs(pos.Y - _rbStartViewport.Value.Y) < 5)
                return;
            _rbActive = true;
            RubberBandRect.Visibility = Visibility.Visible;
            StartRbAutoScrollTimer();
        }

        // 見た目の矩形はビューポート座標。ただし開始点はオートスクロール分のズレを補正した位置を使う
        // （そうしないと、スクロール後は開始点に対応する実際のファイルが画面上で押し出され、
        //  矩形の見た目上端と実際の当たり判定がズレて選択から外れてしまう）。
        var start = GetAdjustedRbStart();
        var vx0 = Math.Min(start.X, pos.X);
        var vy0 = Math.Min(start.Y, pos.Y);
        var vx1 = Math.Max(start.X, pos.X);
        var vy1 = Math.Max(start.Y, pos.Y);
        Canvas.SetLeft(RubberBandRect, vx0);
        Canvas.SetTop(RubberBandRect, vy0);
        RubberBandRect.Width = vx1 - vx0;
        RubberBandRect.Height = vy1 - vy0;

        // 当たり判定もビューポート座標で行う。
        // SelectItemsInRect側はContainerFromIndex().TransformToVisual(ThumbGridView)で
        // セルの実座標を取得しているが、これはScrollViewerのクリップ変換を経た
        // 「現在のスクロール位置を反映済みのビューポート座標」になる。
        // そのため、ここでスクロール量を加算したコンテンツ座標を渡すと座標系が食い違い、
        // スクロールした状態でドラッグ選択すると選択範囲がずれる不具合の原因になっていた。
        // 見た目の矩形(vx0..vy1)と同じビューポート座標をそのまま当たり判定にも使う。
        SelectItemsInRect(vx0, vy0, vx1, vy1);
    }

    private void ThumbGridView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rbStartViewport == null) return;

        // ★ 離した瞬間にもう一度だけ判定をやり直す。タイマー(30ms間隔)とPointerMovedの
        // 隙間で実体化が完了したセルを最後に拾うための保険。
        if (_rbActive && _rbLastPointerPos != null)
        {
            var pos = _rbLastPointerPos.Value;
            var start = GetAdjustedRbStart();
            var vx0 = Math.Min(start.X, pos.X);
            var vy0 = Math.Min(start.Y, pos.Y);
            var vx1 = Math.Max(start.X, pos.X);
            var vy1 = Math.Max(start.Y, pos.Y);
            SelectItemsInRect(vx0, vy0, vx1, vy1);
        }

        try { ThumbGridView.ReleasePointerCapture(e.Pointer); } catch { /* 既に解放済みでも無視 */ }
        RubberBandRect.Visibility = Visibility.Collapsed;
        _rbStartViewport = null;
        _rbActive = false;
        _rbLastPointerPos = null;
        StopRbAutoScrollTimer();
    }

    // ── ドラッグ選択中の自動スクロール ──────────────────────────────
    // ファイルリスト上部/下部の端（さらには画面外）にポインタが近づいた/出た状態で
    // 静止していても、PointerMovedは発火しないためタイマーで定期的にスクロールを進める。
    private void StartRbAutoScrollTimer()
    {
        if (_rbAutoScrollTimer != null) return;
        _rbAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _rbAutoScrollTimer.Tick += RbAutoScrollTimer_Tick;
        _rbAutoScrollTimer.Start();
    }

    private void StopRbAutoScrollTimer()
    {
        if (_rbAutoScrollTimer == null) return;
        _rbAutoScrollTimer.Stop();
        _rbAutoScrollTimer.Tick -= RbAutoScrollTimer_Tick;
        _rbAutoScrollTimer = null;
    }

    private void RbAutoScrollTimer_Tick(object? sender, object e)
    {
        if (!_rbActive || _rbStartViewport == null || _rbLastPointerPos == null) return;
        _gridScrollViewer ??= FindDescendant<ScrollViewer>(ThumbGridView);
        if (_gridScrollViewer == null) return;

        const double edgeMargin = 48;   // この距離だけ端に近づくとスクロールし始める
        const double maxSpeed = 18;     // 端ぎりぎり/画面内での最大スクロール量(px/tick)
        const double outsideSpeed = 36; // 画面外に出た場合はさらに速く

        var y = _rbLastPointerPos.Value.Y;
        var height = ThumbGridView.ActualHeight;

        double delta;
        if (y < 0) delta = -outsideSpeed;
        else if (y < edgeMargin) delta = -maxSpeed * ((edgeMargin - y) / edgeMargin);
        else if (y > height) delta = outsideSpeed;
        else if (y > height - edgeMargin) delta = maxSpeed * ((y - (height - edgeMargin)) / edgeMargin);
        else delta = 0;

        // 端に近くスクロールが必要な場合のみ、実際にビューを動かす。
        if (delta != 0)
        {
            var maxOffset = Math.Max(0, _gridScrollViewer.ExtentHeight - _gridScrollViewer.ViewportHeight);
            var newOffset = Math.Clamp(_gridScrollViewer.VerticalOffset + delta, 0, maxOffset);
            if (Math.Abs(newOffset - _gridScrollViewer.VerticalOffset) >= 0.01)
                _gridScrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
        }

        // ★ 重要: delta==0（=端に近づいておらず、ポインタが静止している）場合でも、
        // 選択判定は毎tick再実行する。サムネイルの非同期デコードや仮想化パネル
        // (ItemsWrapGrid)によるセルの実体化(ContainerFromIndex)はPointerMoved発火と
        // 同期して完了するとは限らず、「矩形内に入っているのにまだ実体化されていない」
        // セルはSelectItemsInRect側でスキップ（選択状態を維持=無視）される。
        // 以前はdelta==0のとき即returnしてSelectItemsInRectを呼んでおらず、
        // ドラッグ中に指を止めている間に遅れて実体化したセルが再チェックされず、
        // 「矩形内なのに一部のファイルしか選択されない」不具合の原因になっていた。
        var pos = _rbLastPointerPos.Value;
        var start = GetAdjustedRbStart();
        var vx0 = Math.Min(start.X, pos.X);
        var vy0 = Math.Min(start.Y, pos.Y);
        var vx1 = Math.Max(start.X, pos.X);
        var vy1 = Math.Max(start.Y, pos.Y);
        Canvas.SetLeft(RubberBandRect, vx0);
        Canvas.SetTop(RubberBandRect, vy0);
        RubberBandRect.Width = vx1 - vx0;
        RubberBandRect.Height = vy1 - vy0;
        SelectItemsInRect(vx0, vy0, vx1, vy1);
    }

    /// <summary>矩形(コンテンツ座標)と交差するセルを選択状態に反映する。
    /// Python版 GridCanvas._on_drag の「矩形内のファイルを選択」部分に相当。
    /// 以前は列数をActualWidthから逆算し、インデックスからセル位置を理論値で計算していたが、
    /// GridViewの実際のパディング/マージン/スクロールバー幅とズレるとヒット判定が全体的にずれ、
    /// 「関係ないファイルまで選択される」不具合の原因になっていた。
    /// ここではContainerFromIndex + TransformToVisualで実際に描画されているセルの座標を取得し、
    /// ポインタ座標系（ThumbGridView基準）と同じ座標系で厳密に交差判定する。</summary>
    private void SelectItemsInRect(double rx0, double ry0, double rx1, double ry1)
    {
        // 仮想化により未実現（画面外）のセルはbounds自体が取得できないため、
        // 以前は毎回「今見えているセルだけで選択集合を作り直す」実装になっており、
        // ドラッグ中にオートスクロールで画面外へ出たファイルの選択が消えてしまう不具合があった。
        // 未実現セルは判定不能として"選択状態を変更しない"（維持する）ことで、
        // 一度矩形に入って選択されたファイルはスクロールしても選択されたままになる。
        var selectedIds = new HashSet<long>(ThumbGridView.SelectedItems.OfType<FileItem>().Select(i => i.Id));
        var toAdd = new List<FileItem>();
        var toRemove = new List<FileItem>();

        for (int idx = 0; idx < _items.Count; idx++)
        {
            if (ThumbGridView.ContainerFromIndex(idx) is not GridViewItem container)
                continue; // 未実現セルは選択状態を維持（変更しない）

            Windows.Foundation.Rect bounds;
            try
            {
                var transform = container.TransformToVisual(ThumbGridView);
                bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch (Exception)
            {
                continue; // レイアウト未確定などで変換に失敗した場合も選択状態を維持
            }

            var item = _items[idx];
            var intersects = bounds.Left < rx1 && bounds.Right > rx0 && bounds.Top < ry1 && bounds.Bottom > ry0;
            var isSelected = selectedIds.Contains(item.Id);

            if (intersects && !isSelected) toAdd.Add(item);
            else if (!intersects && isSelected) toRemove.Add(item);
        }

        foreach (var it in toRemove) ThumbGridView.SelectedItems.Remove(it);
        foreach (var it in toAdd) ThumbGridView.SelectedItems.Add(it);
    }

    private FileItem? GetFileItemFromEventSource(object originalSource)
    {
        if (originalSource is not DependencyObject d) return null;
        var container = FindAncestor<GridViewItem>(d);
        if (container == null) return null;
        return ThumbGridView.ItemFromContainer(container) as FileItem;
    }

    /// <summary>設定「ファイルを開く操作」がシングルクリックの場合のみ、タップ即座にファイルを開く。
    /// ダブルクリック設定時はここでは何もせず、通常どおり選択のみ行われる（開くのはDoubleTapped側）。</summary>
    private void ThumbGridView_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_config.OpenClickMode != "single") return;
        var item = GetFileItemFromEventSource(e.OriginalSource);
        if (item != null) OpenFile(item);
    }

    private void ThumbGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // シングルクリック設定時はTapped側で既に開いているため、二重に開かないようここではスキップする。
        if (_config.OpenClickMode == "single") return;
        var item = GetFileItemFromEventSource(e.OriginalSource);
        if (item != null) OpenFile(item);
    }

    private void ThumbGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = GetFileItemFromEventSource(e.OriginalSource);
        if (item == null) return;

        var container = FindAncestor<GridViewItem>((DependencyObject)e.OriginalSource);
        if (container == null) return;

        // フォルダアイコン（「フォルダを開く」表示中のサブフォルダ行）を右クリックした場合は、
        // ファイルへのタグ付けメニューではなく専用のフォルダ操作メニューを出す。
        if (item.IsFolder)
        {
            if (!ThumbGridView.SelectedItems.Contains(item))
                ThumbGridView.SelectedItem = item;

            var folderFlyout = BuildFolderContextMenu(item);
            folderFlyout.ShowAt(container, new FlyoutShowOptions
            {
                Position = e.GetPosition(container)
            });
            return;
        }

        // 右クリックしたセルが既に複数選択に含まれている場合は選択状態を維持する
        // （Explorer等と同じ挙動。以前は無条件でSelectedItem=itemとしていたため、
        // 複数選択した状態で右クリックすると選択が1件に解除されてしまっていた）
        if (!ThumbGridView.SelectedItems.Contains(item))
            ThumbGridView.SelectedItem = item;

        // 複数選択中に右クリックした場合は選択中の全ファイルを対象にする
        var targets = ThumbGridView.SelectedItems.OfType<FileItem>().Where(f => !f.IsFolder).ToList();
        if (targets.Count == 0) targets = new List<FileItem> { item };

        var flyout = BuildContextMenu(item, targets);
        flyout.ShowAt(container, new FlyoutShowOptions
        {
            Position = e.GetPosition(container)
        });
    }

    /// <summary>ファイルエリアに表示されているフォルダアイコンを右クリックしたときのメニュー。
    /// 「開く」に加え、①フォルダ直下のファイルへ既存タグ名との一致で自動タグ付けする機能、
    /// ②該当単語でタグ付けする機能、③そのフォルダ自体を左のタグエリア（タグリスト）へ
    /// ショートカットとして登録する機能を提供する。</summary>
    private MenuFlyout BuildFolderContextMenu(FileItem folderItem)
    {
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "開く" };
        openItem.Click += (_, _) => OpenFolderPathIntoGrid(folderItem.Path);
        flyout.Items.Add(openItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var autoTagItem = new MenuFlyoutItem { Text = "🏷 フォルダ内のファイルを自動タグ付け..." };
        autoTagItem.Click += async (_, _) => await AutoTagFolderContentsAsync(folderItem.Path);
        flyout.Items.Add(autoTagItem);

        var tagByWordItem = new MenuFlyoutItem { Text = "🏷 フォルダ内のファイルを該当単語でタグ付け..." };
        tagByWordItem.Click += async (_, _) => await TagFolderContentsByWordAsync(folderItem.Path);
        flyout.Items.Add(tagByWordItem);

        var registerItem = new MenuFlyoutItem { Text = "📌 タグエリアに登録" };
        registerItem.Click += (_, _) => RegisterFolderToTagArea(folderItem.Path);
        flyout.Items.Add(registerItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var explorerItem = new MenuFlyoutItem { Text = "エクスプローラーで表示" };
        explorerItem.Click += (_, _) => ShowInExplorer(folderItem);
        flyout.Items.Add(explorerItem);

        return flyout;
    }

    /// <summary>指定フォルダ配下のファイルパスをFileItemのリストに変換する共通ヘルパー。
    /// 既にDB登録済みのファイルはそのIdとタグを引き継ぐ（AutoTagFolderContentsAsync /
    /// TagFolderContentsByWordAsync 共通で使用）。</summary>
    private List<FileItem> CollectFolderTargets(string folderPath, bool recursive)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        List<string> filePaths;
        try
        {
            filePaths = Directory.EnumerateFiles(folderPath, "*", searchOption).ToList();
        }
        catch (Exception ex)
        {
            TagActionStatusText.Text = $"フォルダを読み込めませんでした: {ex.Message}";
            return new List<FileItem>();
        }

        var existing = _db.GetAllFiles()
            .Where(r => filePaths.Contains(r.Path, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

        return filePaths.Select(p => existing.TryGetValue(p, out var rec)
            ? new FileItem { Id = rec.Id, Path = rec.Path, Extension = Path.GetExtension(rec.Path) }
            : new FileItem { Id = 0, Path = p, Extension = Path.GetExtension(p) }
        ).ToList();
    }

    /// <summary>指定フォルダ直下のファイルを対象に、ファイル名から自動タグ付けダイアログ
    /// （ShowAutoTagFromFilenameAsync）を開く。フォルダを「開く」で表示していなくても実行できるよう、
    /// 対象ファイルはグリッドの表示状態に依存せず、その場でパスから作成する
    /// （既にDB登録済みのファイルはそのIdとタグを引き継ぐ）。
    /// 「サブフォルダにも適用」「大文字・小文字を区別する」「ひらがな・カタカナを区別しない」を
    /// 1つのダイアログにまとめて選択できるようにしている。</summary>
    private async Task AutoTagFolderContentsAsync(string folderPath)
    {
        var recursiveCheck = new CheckBox
        {
            Content = "サブフォルダにも適用",
            IsChecked = false, // 既定はオフ（直下のファイルのみ）
        };
        var caseSensitiveCheck = new CheckBox
        {
            Content = "大文字と小文字を区別する",
            IsChecked = false, // 既定はオフ（区別しない）
        };
        var kanaInsensitiveCheck = new CheckBox
        {
            Content = "ひらがな・カタカナを区別しない",
            IsChecked = true, // 既定はオン（「さくら」タグが「サクラ.jpg」にも一致する）
        };

        var confirmPanel = new StackPanel { Spacing = 10 };
        confirmPanel.Children.Add(new TextBlock
        {
            Text = "フォルダ内のファイルを対象に、ファイル名から自動タグ付けを行います。",
            TextWrapping = TextWrapping.Wrap,
        });
        confirmPanel.Children.Add(recursiveCheck);
        confirmPanel.Children.Add(caseSensitiveCheck);
        confirmPanel.Children.Add(kanaInsensitiveCheck);

        var confirmDialog = new ContentDialog
        {
            Title = "フォルダ内のファイルを自動タグ付け",
            Content = confirmPanel,
            PrimaryButtonText = "次へ",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var confirmResult = await confirmDialog.ShowAsync();
        if (confirmResult != ContentDialogResult.Primary) return;

        var targets = CollectFolderTargets(folderPath, recursiveCheck.IsChecked == true);
        if (targets.Count == 0)
        {
            TagActionStatusText.Text = "このフォルダにはファイルがありません。";
            return;
        }

        await ShowAutoTagFromFilenameAsync(targets, caseSensitiveCheck.IsChecked == true, kanaInsensitiveCheck.IsChecked == true);
    }

    /// <summary>指定フォルダ配下のファイルを対象に、「該当単語でタグ付け」ダイアログ
    /// （ShowTagByWordAsync）を開く。「サブフォルダにも適用」はダイアログ内の他のオプションと
    /// 同じ場所にまとめて表示され、確認だけの前段ダイアログは挟まない（1段階で完結する）。</summary>
    private async Task TagFolderContentsByWordAsync(string folderPath)
    {
        await ShowTagByWordAsync(new List<FileItem>(), folderPath);
    }

    /// <summary>フォルダを左のタグエリア（タグリスト）へショートカットとして登録する
    /// （エクスプローラー等からフォルダをタグリストへドラッグ&ドロップした場合と同じ処理）。</summary>
    private void RegisterFolderToTagArea(string folderPath)
    {
        var folderId = _db.AddFolder(folderPath);
        RefreshNavList();
        TagActionStatusText.Text = folderId != null
            ? $"「{System.IO.Path.GetFileName(folderPath)}」をタグエリアに登録しました。"
            : "フォルダの登録に失敗しました（既に登録済みの可能性があります）。";
    }

    /// <summary>Python版 _open_file 相当: 既定アプリで開き、アクセス日時/開いた回数/最近開いたに記録</summary>
    private void OpenFile(FileItem item)
    {
        if (item.IsFolder)
        {
            OpenFolderPathIntoGrid(item.Path);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 以前は例外メッセージ(ex.Message)をそのまま表示していたが、Win32例外の生の文言は
            // 分かりにくいため、固定の簡潔なメッセージに変更した。原因調査用にログには残す。
            App.LogCrash(ex);
            TagActionStatusText.Text = "指定されたファイルが見つかりません";
            return;
        }
        // 未登録（Id<=0）のファイルだと、この後のrecentテーブル書き込みが
        // 外部キー制約違反で例外になりアプリごと落ちるため、先に登録しておく。
        EnsureRegistered(item);
        _db.UpdateAccessed(item.Id);
        _db.AddRecent(item.Id);
    }

    /// <summary>設定画面の「対象のファイル種類」（画像／圧縮ファイル／動画ファイル）チェックに基づき、
    /// このファイルが「既定のソフト以外で開く」の対象かどうかを判定する。
    /// 3種のいずれにも該当しない拡張子（テキストファイル等）は対象外。</summary>
    private bool IsExternalAppTargetType(string path)
    {
        if (_config.ExternalAppForImage && Services.ThumbnailGenerator.IsImage(path)) return true;
        if (_config.ExternalAppForArchive && Services.ThumbnailGenerator.IsArchive(path)) return true;
        if (_config.ExternalAppForVideo && Services.ThumbnailGenerator.IsVideo(path)) return true;
        return false;
    }

    /// <summary>右クリックメニュー「既定のソフト以外で開く」用。設定画面（ExternalAppPath）で
    /// 登録した外部アプリの引数にファイルパスを渡して起動する。フォルダは対象外
    /// （フォルダはOpenFile同様グリッドで開く方が自然なため、ここでは扱わない）。
    /// 未設定・実行ファイルが見つからない場合はエラーを出さずステータス欄に案内を出すだけにする。</summary>
    private void OpenFileWithExternalApp(FileItem item)
    {
        if (item.IsFolder) return;

        var appPath = _config.ExternalAppPath;
        if (string.IsNullOrWhiteSpace(appPath))
        {
            TagActionStatusText.Text = "「既定のソフト以外で開く」用のソフトが設定画面で未設定です。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(appPath)
            {
                ArgumentList = { item.Path },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            TagActionStatusText.Text = "指定した外部ソフトを起動できませんでした（設定画面のパスをご確認ください）";
            return;
        }
        // フォルダを開いただけでまだDB未登録（Id<=0）のファイルもあるため、
        // アクセス日時・最近開いた記録を書き込む前に必ず登録しておく（未登録IDのままだと
        // recentテーブルの外部キー制約違反で例外→アプリごと落ちるため）。
        EnsureRegistered(item);
        _db.UpdateAccessed(item.Id);
        _db.AddRecent(item.Id);
    }

    /// <summary>Python版 _show_in_explorer 相当</summary>
    private void ShowInExplorer(FileItem item)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch (Exception ex)
        {
            TagActionStatusText.Text = $"エクスプローラーを開けませんでした: {ex.Message}";
        }
    }

    /// <summary>Python版 _show_context_menu 相当をMenuFlyoutで再現。
    /// <paramref name="item"/>は右クリックされたセル（開く/エクスプローラー表示/コメント編集/削除の対象）、
    /// <paramref name="targets"/>はスター評価・タグ付けの対象（複数選択中はその全件、単一選択時は item のみ）。</summary>
    private MenuFlyout BuildContextMenu(FileItem item, List<FileItem> targets)
    {
        var flyout = new MenuFlyout();
        var isMulti = targets.Count > 1;

        var openItem = new MenuFlyoutItem { Text = "開く" };
        openItem.Click += (_, _) => OpenFile(item);
        flyout.Items.Add(openItem);

        // 既定のソフト以外で開く：設定画面（ExternalAppPath）で登録したアプリで開く。
        // フォルダには対応しないため非表示にする。また設定画面の「対象のファイル種類」
        // （画像／圧縮ファイル／動画ファイル）チェックに一致しないファイルにはメニュー自体を出さない。
        if (!item.IsFolder && IsExternalAppTargetType(item.Path))
        {
            var openExternalItem = new MenuFlyoutItem { Text = "既定のソフト以外で開く" };
            openExternalItem.Click += (_, _) => OpenFileWithExternalApp(item);
            flyout.Items.Add(openExternalItem);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());

        // ⭐ スター評価（0〜5、Python版と同じ）。複数選択時は選択中の全ファイルに同じ評価を一括適用する。
        var starSub = new MenuFlyoutSubItem { Text = isMulti ? $"⭐ スター評価（{targets.Count}件）" : "⭐ スター評価" };
        for (int i = 0; i <= 5; i++)
        {
            var label = i == 0 ? "☆ なし" : new string('★', i) + new string('☆', 5 - i);
            var mi = new MenuFlyoutItem { Text = label };
            int v = i;
            mi.Click += (_, _) =>
            {
                foreach (var t in targets)
                {
                    EnsureRegistered(t);
                    _db.UpdateStar(t.Id, v);
                    t.Star = v;
                }
                RefreshNavList();
            };
            starSub.Items.Add(mi);
        }
        flyout.Items.Add(starSub);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // 🏷 タグ付け：以前はサブメニューでのON/OFFトグル方式だったが、
        // ドラッグ&ドロップでファイルをグリッドへ落とした際に表示される
        // 「タグを選択」ダイアログ（ShowTagAssignPickerAsync、白いContentDialog）と
        // 見た目・操作感を統一するため、右クリックからも同じダイアログを開く方式に変更。
        var tagAssignItem = new MenuFlyoutItem
        {
            Text = isMulti ? $"🏷 タグを付ける（{targets.Count}件）..." : "🏷 タグを付ける...",
        };
        tagAssignItem.Click += async (_, _) => await ShowTagAssignPickerAsync(targets);
        flyout.Items.Add(tagAssignItem);

        // 🏷 ファイル名から自動タグ付け：既存タグ名と一致する単語がファイル名に含まれていれば、
        // そのタグを自動的に付与する。フォルダを開いて未タグ付けのファイルが並んでいるときに、
        // まとめて大まかなタグ付けを済ませたいという要望から追加。
        var autoTagItem = new MenuFlyoutItem
        {
            Text = isMulti ? $"🏷 ファイル名から自動タグ付け（{targets.Count}件）..." : "🏷 ファイル名から自動タグ付け...",
        };
        autoTagItem.Click += async (_, _) => await ShowAutoTagFromFilenameAsync(targets);
        flyout.Items.Add(autoTagItem);

        // 🏷 該当単語でタグ付け：既存タグ名との自動一致(ShowAutoTagFromFilenameAsync)とは逆で、
        // ユーザーが自由に入力した単語がファイル名に含まれるものだけを、選んだタグへまとめて
        // タグ付けする。フォルダ内で「◯◯という単語を含むファイルだけ、このタグを付けたい」
        // という用途向け。
        var tagByWordItem = new MenuFlyoutItem
        {
            Text = isMulti ? $"🏷 該当単語でタグ付け（{targets.Count}件）..." : "🏷 該当単語でタグ付け...",
        };
        tagByWordItem.Click += async (_, _) => await ShowTagByWordAsync(targets);
        flyout.Items.Add(tagByWordItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var commentItem = new MenuFlyoutItem { Text = "コメントを編集..." };
        commentItem.Click += async (_, _) => await EditCommentAsync(item);
        flyout.Items.Add(commentItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // 名前の変更。file_tagsはファイルID紐付けのためリネームしてもタグは保持される。
        // 複数選択中は「どのファイル名にするか」が一意に決まらないため禁止する。
        var renameItem = new MenuFlyoutItem { Text = "名前を変更...", IsEnabled = !isMulti };
        renameItem.Click += async (_, _) => await RenameFileAsync(item);
        flyout.Items.Add(renameItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var explorerItem = new MenuFlyoutItem { Text = "エクスプローラーで表示" };
        explorerItem.Click += (_, _) => ShowInExplorer(item);
        flyout.Items.Add(explorerItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = isMulti ? $"リストから削除（{targets.Count}件）" : "リストから削除" };
        deleteItem.Click += async (_, _) => await DeleteFileAsync(targets);
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    /// <summary>指定ファイルがまだ持っていない場合のみタグを追加する（複数選択への一括タグ付け用）</summary>
    private void AddTagIfMissing(FileItem item, long tagId)
    {
        var current = item.Id <= 0 ? new HashSet<long>() : _db.GetFileTags(item.Id).Select(t => t.Id).ToHashSet();
        if (!current.Contains(tagId))
        {
            EnsureRegistered(item);
            _db.AddFileTag(item.Id, tagId);
        }
        var tags = _db.GetFileTags(item.Id);
        SetItemTags(item, tags);
    }

    /// <summary>指定ファイルが持っている場合のみタグを外す（複数選択への一括タグ外し用）</summary>
    private void RemoveTagIfPresent(FileItem item, long tagId)
    {
        if (item.Id <= 0) return; // 未登録ファイルはタグを持ち得ないので何もしない
        var current = _db.GetFileTags(item.Id).Select(t => t.Id).ToHashSet();
        if (current.Contains(tagId))
            _db.RemoveFileTag(item.Id, tagId);

        var tags = _db.GetFileTags(item.Id);
        SetItemTags(item, tags);
    }

    private void ToggleTag(FileItem item, long tagId)
    {
        var current = item.Id <= 0 ? new HashSet<long>() : _db.GetFileTags(item.Id).Select(t => t.Id).ToHashSet();
        if (current.Contains(tagId))
        {
            _db.RemoveFileTag(item.Id, tagId);
        }
        else
        {
            EnsureRegistered(item);
            _db.AddFileTag(item.Id, tagId);
        }

        var tags = item.Id <= 0 ? new List<TagRecord>() : _db.GetFileTags(item.Id);
        SetItemTags(item, tags);
        RefreshNavList();
    }

    /// <summary>右クリックメニュー「🏷 タグを付ける...」から開く、タグ設定用ダイアログ。
    /// ドラッグ&ドロップ時に表示される <see cref="ShowMultiTagPickerAsync"/> と同じ
    /// ContentDialog（白いポップアップウィンドウ）方式に統一したもの。
    /// 単一選択時はチェックボックスのON/OFFで付与/解除を、複数選択時は三態
    /// （✓=全員が持っている／未チェック=誰も持っていない／－=一部だけ持っている）で表示し、
    /// 「－」のまま操作しなければ現状を維持、明示的にチェック/解除すれば全対象へ一括反映する。
    /// ダイアログ内から新規タグの作成も行える。</summary>
    private async Task ShowTagAssignPickerAsync(List<FileItem> targets)
    {
        if (targets.Count == 0) return;
        var isMulti = targets.Count > 1;

        List<HashSet<long>> TagIdSets() => targets
            .Select(t => t.Id <= 0 ? new HashSet<long>() : _db.GetFileTags(t.Id).Select(x => x.Id).ToHashSet())
            .ToList();
        var targetTagIdSets = TagIdSets();

        var panel = new StackPanel { Spacing = 6 };
        var checkBoxes = new List<(long TagId, CheckBox Box)>();

        void AddTagRow(TagRecord tag)
        {
            var allHave = targetTagIdSets.All(s => s.Contains(tag.Id));
            var noneHave = targetTagIdSets.All(s => !s.Contains(tag.Id));
            var dot = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(TryParseColor(tag.Color) ?? Colors.Gray),
                Margin = new Thickness(0, 0, 6, 0),
            };
            var cb = new CheckBox { IsThreeState = isMulti };
            cb.IsChecked = isMulti ? (allHave ? true : noneHave ? false : (bool?)null) : allHave;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = tag.Name, VerticalAlignment = VerticalAlignment.Center });
            cb.Content = row;
            panel.Children.Add(cb);
            checkBoxes.Add((tag.Id, cb));
        }

        foreach (var tag in _db.GetAllTags()) AddTagRow(tag);

        var scroll = new ScrollViewer
        {
            Content = panel, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var newTagBox = new TextBox { PlaceholderText = "新しいタグ名を入力（Enterまたは「OK」で追加）" };

        // 新規タグ名入力欄のテキストからタグを作成し、対象ファイルへ即座に付与する。
        // Enterキー押下時だけでなく、Enterを押さずに直接「OK」をクリックした場合にも
        // 同じ処理を通す必要があるため、共通の関数として切り出している
        // （以前はEnterのKeyDownハンドラ内にしかこの処理が無く、文字を入力しただけで
        // Enterを押さずにOKを押すと、入力欄の中身が一度も見られないまま無視されていた）。
        void CommitNewTagIfAny()
        {
            var name = newTagBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var tagId = _db.AddTag(name);
            if (tagId == null) return;
            targetTagIdSets = TagIdSets(); // 新規タグなので変化なしだが念のため最新化
            var newTag = _db.GetAllTags().FirstOrDefault(t => t.Id == tagId.Value);
            if (newTag != null && checkBoxes.All(c => c.TagId != newTag.Id))
            {
                AddTagRow(newTag);
                checkBoxes[^1].Box.IsChecked = true; // 作ったタグはそのまま付与対象にする
            }
            foreach (var t in targets)
            {
                EnsureRegistered(t);
                AddTagIfMissing(t, tagId.Value);
            }
            newTagBox.Text = "";
        }

        newTagBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            // ContentDialogはEnterキーを既定ボタンの実行として横取りすることがあるため、
            // 念のため伝播を止めておく（DefaultButton=Noneにしているので通常は無関係）。
            e.Handled = true;
            CommitNewTagIfAny();
            RefreshNavList();
        };

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(scroll);
        root.Children.Add(newTagBox);

        var dialog = new ContentDialog
        {
            Title = isMulti ? $"{targets.Count} 件のタグを設定" : "タグを設定",
            Content = root,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Enterを押さずに文字だけ入力して直接「OK」を押したケースもここで拾う。
        CommitNewTagIfAny();

        foreach (var t in targets) EnsureRegistered(t);
        foreach (var (tagId, box) in checkBoxes)
        {
            if (box.IsChecked == true)
                foreach (var t in targets) AddTagIfMissing(t, tagId);
            else if (box.IsChecked == false)
                foreach (var t in targets) RemoveTagIfPresent(t, tagId);
            // null（一部のみ・未操作）はそのまま現状維持
        }

        RefreshNavList();
        TagActionStatusText.Text = isMulti ? $"{targets.Count} 件のタグを更新しました。" : "タグを更新しました。";
    }

    /// <summary>右クリックメニュー「🏷 ファイル名から自動タグ付け...」から開くダイアログ。
    /// 既存タグの名前と一致する単語（部分一致）がファイル名（拡張子込み）に含まれていれば、
    /// そのタグを自動付与する。「大文字・小文字を区別する」「ひらがな・カタカナを区別しない」を
    /// それぞれチェックボックスで選択できるようにし、判定方法を組み合わせられるようにしている。</summary>
    private async Task ShowAutoTagFromFilenameAsync(List<FileItem> targets, bool? presetCaseSensitive = null, bool? presetKanaInsensitive = null, string? folderPath = null)
    {
        if (targets.Count == 0 && folderPath == null) return;
        var isMulti = targets.Count > 1;
        // フォルダ右クリック経由（AutoTagFolderContentsAsync）では、確認ダイアログの時点で
        // 大文字小文字・かなのオプションを既に選択済みのため、ここでは再度尋ねずそのまま使う。
        var usePreset = presetCaseSensitive.HasValue && presetKanaInsensitive.HasValue;

        var caseSensitiveCheck = new CheckBox
        {
            Content = "大文字と小文字を区別する",
            IsChecked = presetCaseSensitive ?? false, // 既定はオフ（区別しない）
        };
        var kanaInsensitiveCheck = new CheckBox
        {
            Content = "ひらがな・カタカナを区別しない",
            IsChecked = presetKanaInsensitive ?? true, // 既定はオン（「さくら」タグが「サクラ.jpg」にも一致する）
        };
        // ファイルエリア上部のタグ付けボタンからフォルダを開いた状態で呼ばれた場合のみ表示する。
        // 既定はオフ（現在表示中の直下のファイルのみ）で、他のオプションと同じ場所に並べる。
        CheckBox? recursiveCheck = folderPath != null
            ? new CheckBox { Content = "サブフォルダにも適用", IsChecked = false }
            : null;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = folderPath != null
                ? "フォルダ内のファイルを対象に、ファイル名に含まれる単語が既存タグ名と一致した場合、そのタグを自動的に付けます。"
                : isMulti
                    ? $"ファイル名に含まれる単語が既存タグ名と一致した場合、そのタグを自動的に付けます（対象 {targets.Count} 件）。"
                    : "ファイル名に含まれる単語が既存タグ名と一致した場合、そのタグを自動的に付けます。",
            TextWrapping = TextWrapping.Wrap,
        });
        if (!usePreset)
        {
            if (recursiveCheck != null) panel.Children.Add(recursiveCheck);
            panel.Children.Add(caseSensitiveCheck);
            panel.Children.Add(kanaInsensitiveCheck);
        }

        var dialog = new ContentDialog
        {
            Title = "ファイル名から自動タグ付け",
            Content = panel,
            PrimaryButtonText = "実行",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (folderPath != null)
        {
            targets = CollectFolderTargets(folderPath, recursiveCheck!.IsChecked == true);
            if (targets.Count == 0)
            {
                TagActionStatusText.Text = "このフォルダにはファイルがありません。";
                return;
            }
        }

        var caseSensitive = presetCaseSensitive ?? (caseSensitiveCheck.IsChecked == true);
        var kanaInsensitive = presetKanaInsensitive ?? (kanaInsensitiveCheck.IsChecked == true);

        // 比較用に正規化した (元のタグ, 正規化後の名前) の一覧を先に作っておく。
        var allTags = _db.GetAllTags();
        var normalizedTags = allTags
            .Select(t => (Tag: t, Key: NormalizeForMatch(t.Name, caseSensitive, kanaInsensitive)))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToList();

        int taggedFileCount = 0, addedTagCount = 0;
        foreach (var item in targets)
        {
            // 拡張子もタグ付け対象に含めるため、ファイル名全体（拡張子込み）を対象にする。
            var nameKey = NormalizeForMatch(item.DisplayName, caseSensitive, kanaInsensitive);
            if (string.IsNullOrEmpty(nameKey)) continue;

            var matchedTagIds = normalizedTags
                .Where(x => nameKey.Contains(x.Key, StringComparison.Ordinal))
                .Select(x => x.Tag.Id)
                .ToList();
            if (matchedTagIds.Count == 0) continue;

            EnsureRegistered(item);
            var current = _db.GetFileTags(item.Id).Select(t => t.Id).ToHashSet();
            var newlyAdded = false;
            foreach (var tagId in matchedTagIds)
            {
                if (current.Contains(tagId)) continue;
                _db.AddFileTag(item.Id, tagId);
                addedTagCount++;
                newlyAdded = true;
            }
            if (newlyAdded)
            {
                taggedFileCount++;
                SetItemTags(item, _db.GetFileTags(item.Id));
            }
        }

        RefreshNavList();
        TagActionStatusText.Text = addedTagCount == 0
            ? "ファイル名に一致するタグは見つかりませんでした。"
            : $"{taggedFileCount} 件のファイルに合計 {addedTagCount} 件のタグを自動で付けました。";
    }

    /// <summary>右クリックメニュー「🏷 該当単語でタグ付け...」から開くダイアログ。
    /// ShowAutoTagFromFilenameAsync（既存タグ名との自動一致）とは逆に、ユーザーが自由入力した
    /// 単語がファイル名（拡張子込み）に含まれるファイルだけを対象に、選択したタグをまとめて付与する。
    /// タグ選択欄はShowTagAssignPickerAsyncと同様に既存タグ一覧＋その場での新規タグ追加に対応する。</summary>
    /// <summary>該当単語でタグ付けダイアログを表示する。folderPathを指定した場合は、
    /// フォルダ右クリックからの呼び出しとして扱い、「サブフォルダにも適用」チェックボックスを
    /// 他のオプション（大文字・小文字の区別など）と同じ場所にまとめて表示する。
    /// 対象ファイルは、ダイアログで実行が確定した後にfolderPathとサブフォルダ適用の有無から
    /// 収集するため、確認だけの前段ダイアログを挟まない（1段階で完結する）。</summary>
    private async Task ShowTagByWordAsync(List<FileItem> targets, string? folderPath = null)
    {
        if (folderPath == null && targets.Count == 0) return;
        var isMulti = targets.Count > 1;

        var wordBox = new TextBox { PlaceholderText = "検索する単語を入力（例: 猫）" };

        CheckBox? recursiveCheck = folderPath != null
            ? new CheckBox { Content = "サブフォルダにも適用", IsChecked = false }
            : null;

        var caseSensitiveCheck = new CheckBox
        {
            Content = "大文字と小文字を区別する",
            IsChecked = false, // 既定はオフ（区別しない）
        };
        var kanaInsensitiveCheck = new CheckBox
        {
            Content = "ひらがな・カタカナを区別しない",
            IsChecked = true, // 既定はオン（「さくら」で「サクラ.jpg」にも一致する）
        };

        var tagPanel = new StackPanel { Spacing = 6 };
        var checkBoxes = new List<(long TagId, CheckBox Box)>();

        void AddTagRow(TagRecord tag)
        {
            var dot = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(TryParseColor(tag.Color) ?? Colors.Gray),
                Margin = new Thickness(0, 0, 6, 0),
            };
            var cb = new CheckBox { IsChecked = false };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = tag.Name, VerticalAlignment = VerticalAlignment.Center });
            cb.Content = row;
            tagPanel.Children.Add(cb);
            checkBoxes.Add((tag.Id, cb));
        }

        foreach (var tag in _db.GetAllTags()) AddTagRow(tag);

        var tagScroll = new ScrollViewer
        {
            Content = tagPanel, MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var newTagBox = new TextBox { PlaceholderText = "新しいタグ名を入力（Enterまたは「実行」で追加）" };

        // Enterを押さずに文字だけ入力して直接「実行」を押した場合にも拾えるよう、
        // タグ作成処理を共通関数として切り出す（Enterハンドラ内だけに処理があると、
        // Enterを押さずにOKへ進んだ場合に入力欄の中身が無視されてしまうため）。
        void CommitNewTagIfAny()
        {
            var name = newTagBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var tagId = _db.AddTag(name);
            if (tagId == null) return;
            var newTag = _db.GetAllTags().FirstOrDefault(t => t.Id == tagId.Value);
            if (newTag != null && checkBoxes.All(c => c.TagId != newTag.Id))
            {
                AddTagRow(newTag);
                checkBoxes[^1].Box.IsChecked = true; // 作ったタグはそのままタグ付け対象にする
            }
            newTagBox.Text = "";
        }

        newTagBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            CommitNewTagIfAny();
            RefreshNavList();
        };

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = folderPath != null
                ? "フォルダ内のファイルを対象に、入力した単語がファイル名に含まれる場合のみ、チェックしたタグを付けます。"
                : isMulti
                    ? $"入力した単語がファイル名に含まれるファイルだけに、チェックしたタグを付けます（対象 {targets.Count} 件）。"
                    : "入力した単語がファイル名に含まれる場合のみ、チェックしたタグを付けます。",
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(wordBox);
        if (recursiveCheck != null) root.Children.Add(recursiveCheck);
        root.Children.Add(caseSensitiveCheck);
        root.Children.Add(kanaInsensitiveCheck);
        root.Children.Add(tagScroll);
        root.Children.Add(newTagBox);

        var dialog = new ContentDialog
        {
            Title = "該当単語でタグ付け",
            Content = root,
            PrimaryButtonText = "実行",
            CloseButtonText = "キャンセル",
            // 新規タグ入力欄でEnterを押した際に、ContentDialogのアクセラレータが
            // TextBoxのKeyDownより先にOK(実行)を起動してしまうのを防ぐ。
            DefaultButton = ContentDialogButton.None,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Enterを押さずに文字だけ入力して直接「実行」を押したケースもここで拾う。
        CommitNewTagIfAny();

        var word = wordBox.Text?.Trim();
        var selectedTagIds = checkBoxes.Where(x => x.Box.IsChecked == true).Select(x => x.TagId).ToList();
        if (string.IsNullOrEmpty(word) || selectedTagIds.Count == 0)
        {
            TagActionStatusText.Text = "単語の入力とタグの選択の両方が必要です。";
            return;
        }

        if (folderPath != null)
        {
            targets = CollectFolderTargets(folderPath, recursiveCheck!.IsChecked == true);
            if (targets.Count == 0)
            {
                TagActionStatusText.Text = "このフォルダにはファイルがありません。";
                return;
            }
        }

        var caseSensitive = caseSensitiveCheck.IsChecked == true;
        var kanaInsensitive = kanaInsensitiveCheck.IsChecked == true;
        var wordKey = NormalizeForMatch(word, caseSensitive, kanaInsensitive);

        int matchedFileCount = 0, addedTagCount2 = 0;
        foreach (var item in targets)
        {
            // 拡張子もタグ付け対象に含めるため、ファイル名全体（拡張子込み）を対象にする。
            var nameKey = NormalizeForMatch(item.DisplayName, caseSensitive, kanaInsensitive);
            if (string.IsNullOrEmpty(nameKey) || !nameKey.Contains(wordKey, StringComparison.Ordinal)) continue;

            EnsureRegistered(item);
            var current = _db.GetFileTags(item.Id).Select(t => t.Id).ToHashSet();
            var newlyAdded = false;
            foreach (var tagId in selectedTagIds)
            {
                if (current.Contains(tagId)) continue;
                _db.AddFileTag(item.Id, tagId);
                addedTagCount2++;
                newlyAdded = true;
            }
            matchedFileCount++;
            if (newlyAdded) SetItemTags(item, _db.GetFileTags(item.Id));
        }

        RefreshNavList();
        TagActionStatusText.Text = matchedFileCount == 0
            ? $"「{word}」を含むファイル名は見つかりませんでした。"
            : $"「{word}」を含む {matchedFileCount} 件のファイルに合計 {addedTagCount2} 件のタグを付けました。";
    }

    /// <summary>自動タグ付けの一致判定用に文字列を正規化する。
    /// caseSensitive=false のときは大文字小文字を区別しない（Invariant小文字化）。
    /// kanaInsensitive=true のときはカタカナをひらがなに寄せることで、
    /// ひらがな/カタカナの表記ゆれを区別せず一致させる。</summary>
    private static string NormalizeForMatch(string s, bool caseSensitive, bool kanaInsensitive)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var result = caseSensitive ? s : s.ToLowerInvariant();
        if (kanaInsensitive) result = KatakanaToHiragana(result);
        return result;
    }

    /// <summary>全角カタカナ（U+30A1〜U+30F6）をひらがな（U+3041〜U+3096）へ変換する。
    /// 半角カタカナ・記号（ー・「・」など）はひらがな側に対応する文字が無いためそのまま残す。</summary>
    private static string KatakanaToHiragana(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '\u30A1' and <= '\u30F6')
                chars[i] = (char)(c - 0x60);
        }
        return new string(chars);
    }

    private async Task NewTagAndAssignAsync(List<FileItem> targets)
    {
        var textBox = new TextBox { PlaceholderText = "タグ名" };
        var dialog = new ContentDialog
        {
            Title = "新規タグを作成",
            Content = textBox,
            PrimaryButtonText = "作成して追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = textBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var tagId = _db.AddTag(name);
        if (tagId == null) return;
        foreach (var item in targets)
        {
            EnsureRegistered(item);
            _db.AddFileTag(item.Id, tagId.Value);
            SetItemTags(item, _db.GetFileTags(item.Id));
        }
        RefreshNavList();
    }

    private async Task EditCommentAsync(FileItem item)
    {
        var rec = item.Id <= 0 ? null : _db.GetFileById(item.Id);
        var textBox = new TextBox
        {
            Text = rec?.Comment ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
        };
        var dialog = new ContentDialog
        {
            Title = "コメントを編集",
            Content = textBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        EnsureRegistered(item);
        _db.UpdateComment(item.Id, textBox.Text ?? "");
    }

    /// <summary>ファイル名を変更する。タグ（file_tags）はファイルIDに紐づいており、
    /// リネームではIDもタグも一切変更しないため、名前を変えてもタグ付けはそのまま保持される。
    /// 複数選択中はこのメソッド自体を呼び出さない（ThumbGridView_RightTapped/BuildContextMenu側で
    /// isMultiの時はメニュー項目を無効化して禁止している）。</summary>
    private async Task RenameFileAsync(FileItem item)
    {
        var currentName = item.DisplayName;
        var textBox = new TextBox { Text = currentName };
        // 拡張子を除いた部分だけを選択状態にする（エクスプローラーの名前変更と同じ挙動）。
        var baseNameLength = currentName.Length - Path.GetExtension(currentName).Length;
        textBox.Loaded += (_, _) => textBox.Select(0, baseNameLength > 0 ? baseNameLength : currentName.Length);

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(textBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "名前を変更",
            Content = panel,
            PrimaryButtonText = "変更",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        // 不正なファイル名や、既に同名のファイルが存在する場合はダイアログを閉じずにエラーを出す。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var newName = textBox.Text?.Trim() ?? "";
            string? error = null;
            if (newName.Length == 0)
            {
                error = "ファイル名を入力してください。";
            }
            else if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "ファイル名に使用できない文字が含まれています。";
            }
            else if (newName != currentName)
            {
                var dir = Path.GetDirectoryName(item.Path) ?? "";
                var candidate = Path.Combine(dir, newName);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    error = "同じ名前のファイルまたはフォルダが既に存在します。";
            }

            if (error != null)
            {
                errorText.Text = error;
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var finalName = textBox.Text!.Trim();
        if (finalName == currentName) return; // 変更なし

        var oldPath = item.Path;
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", finalName);

        // 移動前（まだoldPathにファイルが実在する段階）でDB登録しておく。
        // file_tagsはこのID（fid）に紐づくため、この後path/filenameだけを書き換えても
        // 既存のタグ付けはそのまま保持される。
        EnsureRegistered(item);

        try
        {
            File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            TagActionStatusText.Text = "名前を変更できませんでした。";
            return;
        }

        _db.UpdatePath(item.Id, newPath);

        item.Path = newPath;
        item.DisplayName = finalName;
        item.Extension = Path.GetExtension(newPath);
    }

    private async Task DeleteFileAsync(List<FileItem> targets)
    {
        var title = "リストから削除";
        var content = targets.Count == 1
            ? $"「{targets[0].DisplayName}」をリストから削除しますか？\n（ファイル自体は削除されません。タグ情報も失われます）"
            : $"選択中の{targets.Count}件をリストから削除しますか？\n（ファイル自体は削除されません。タグ情報も失われます）";
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        foreach (var item in targets)
        {
            _db.DeleteFile(item.Id);
            _items.Remove(item);
        }
        RefreshNavList();
    }

    // ── テーマ・サムネイズ設定 ─────────────────────────

    /// <summary>Python版 _apply_theme 相当。名前付き要素の色を直接書き換えて即時反映する。</summary>
    /// <summary>RootGrid.Resourcesの指定キーのSolidColorBrushへ、baseColorのRGBに指定アルファ値を
    /// 乗せた色を反映する（ApplyTheme内の左サイドバー選択色/ホバー色更新で使う）。
    /// このファイルは`using System.Drawing;`があるため無印のColorはSystem.Drawing.Colorを指してしまう。
    /// SolidColorBrush.Colorの型はWindows.UI.Colorのため、ここでは完全修飾名で明示している。</summary>
    private void SetSelBrushAlpha(string resourceKey, Windows.UI.Color baseColor, byte alpha)
    {
        if (RootGrid.Resources[resourceKey] is SolidColorBrush brush)
            brush.Color = Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    private void ApplyTheme(string themeName)
    {
        var p = AppTheme.Get(themeName);

        RootGrid.Background = new SolidColorBrush(p.Bg);
        SidebarGrid.Background = new SolidColorBrush(p.Bg2);
        ThumbGridView.Background = new SolidColorBrush(p.Bg);

        var fgBrush = new SolidColorBrush(p.Fg);
        StatusText.Foreground = fgBrush;
        FilterListView.Foreground = fgBrush;
        TagListView.Foreground = fgBrush;
        ThumbGridView.Foreground = fgBrush;

        // サムネイル画像が読み込み中/生成失敗で表示できない場合に見えるセルの背景。
        // 固定色のままだとどのテーマでも黒っぽく浮いて見えるため、テーマのThumb色に追従させる。
        if (RootGrid.Resources["ThumbCellBackgroundBrush"] is SolidColorBrush thumbCellBrush)
            thumbCellBrush.Color = p.Thumb;

        // タグリストのタグ数チップの背景。以前は固定で白の20%透過(#33FFFFFF)だったため、
        // 明るいテーマでは背景に馴染んで見えにくかった。テーマの文字色(Fg)を基準にした
        // 薄い塗りへ都度更新することで、テーマによらず文字とのコントラストを保って視認できるようにする。
        if (RootGrid.Resources["TagCountChipBackgroundBrush"] is SolidColorBrush chipBrush)
            chipBrush.Color = Windows.UI.Color.FromArgb(0x0A, p.Fg.R, p.Fg.G, p.Fg.B);

        // 左サイドバー（FilterListView・TagListView）の選択色/ホバー色を、テーマのSel色を基準に
        // エクスプローラーの左ツリーのような薄い塗り（低い不透明度のオーバーレイ）で更新する。
        // 「選択＋ホバー」「選択」「ホバーのみ」の順で少しずつ不透明度を下げ、素のホバーが
        // 一番薄くなるようにしている。
        SetSelBrushAlpha("ListViewItemBackgroundSelectedPointerOver", p.Sel, 0x50);
        SetSelBrushAlpha("ListViewItemBackgroundSelectedPressed", p.Sel, 0x60);
        SetSelBrushAlpha("ListViewItemBackgroundSelected", p.Sel, 0x33);
        SetSelBrushAlpha("ListViewItemBackgroundPointerOver", p.Sel, 0x1A);
        SetSelBrushAlpha("ListViewItemBackgroundPressed", p.Sel, 0x2A);

        // サムネイル一覧（ThumbGridView）のホバー/選択色。左サイドバーと同じキー名だが
        // GridView.Resourcesでスコープを分けているため、ここだけ別の不透明度にできる。
        // ダークテーマは背景・サムネ共に暗く、左サイドバーと同じ薄さ(0x1A)だと
        // ほぼ見えなくなってしまうため、暗い背景ほど不透明度を上げて明るく目立たせる。
        var bgLuminance = (0.299 * p.Bg.R + 0.587 * p.Bg.G + 0.114 * p.Bg.B) / 255.0;
        byte gridHoverAlpha = bgLuminance < 0.5 ? (byte)0x66 : (byte)0x30;
        byte gridSelPointerOverAlpha = bgLuminance < 0.5 ? (byte)0x90 : (byte)0x55;
        byte gridSelAlpha = bgLuminance < 0.5 ? (byte)0x70 : (byte)0x40;
        byte gridPressedAlpha = bgLuminance < 0.5 ? (byte)0x80 : (byte)0x48;
        if (ThumbGridView.Resources["ListViewItemBackgroundPointerOver"] is SolidColorBrush gridHoverBrush)
            gridHoverBrush.Color = Windows.UI.Color.FromArgb(gridHoverAlpha, p.Sel.R, p.Sel.G, p.Sel.B);
        if (ThumbGridView.Resources["ListViewItemBackgroundSelectedPointerOver"] is SolidColorBrush gridSelPointerOverBrush)
            gridSelPointerOverBrush.Color = Windows.UI.Color.FromArgb(gridSelPointerOverAlpha, p.Sel.R, p.Sel.G, p.Sel.B);
        if (ThumbGridView.Resources["ListViewItemBackgroundSelected"] is SolidColorBrush gridSelBrush)
            gridSelBrush.Color = Windows.UI.Color.FromArgb(gridSelAlpha, p.Sel.R, p.Sel.G, p.Sel.B);
        if (ThumbGridView.Resources["ListViewItemBackgroundPressed"] is SolidColorBrush gridPressedBrush)
            gridPressedBrush.Color = Windows.UI.Color.FromArgb(gridPressedAlpha, p.Sel.R, p.Sel.G, p.Sel.B);

        // 標準コントロール（Button/ListView項目/TextBox等）の既定文字色は
        // ThemeResource（システムのライト/ダーク）に連動しているため、
        // 個別にForegroundを塗るだけでは対応しきれない（ダーク背景×黒文字で
        // 見えなくなる原因）。ウィンドウ全体のElementThemeを背景の明るさから
        // 判定して切り替えることで、標準コントロールの文字色もまとめて追従させる。
        var luminance = (0.299 * p.Bg.R + 0.587 * p.Bg.G + 0.114 * p.Bg.B) / 255.0;
        var elementTheme = luminance < 0.5 ? ElementTheme.Dark : ElementTheme.Light;
        if (Content is FrameworkElement rootElement)
            rootElement.RequestedTheme = elementTheme;

        // タイトルバー枠自体（DWM非クライアント領域）をダーク/ライトに切り替える。
        // AppWindow.TitleBarの色指定（下記）だけでは反映されない環境があるための保険。
        SetTitleBarDarkMode(elementTheme == ElementTheme.Dark);

        // タイトルバー（キャプションボタン含む）の色もテーマの背景/文字色に合わせる。
        // 既定のままだとダークテーマ時でもタイトルバーだけ白いままになってしまうため。
        var titleBar = AppWindow.TitleBar;
        if (titleBar != null)
        {
            titleBar.BackgroundColor = p.Bg;
            titleBar.InactiveBackgroundColor = p.Bg;
            titleBar.ForegroundColor = p.Fg;
            titleBar.InactiveForegroundColor = p.Fg;
            titleBar.ButtonBackgroundColor = p.Bg;
            titleBar.ButtonInactiveBackgroundColor = p.Bg;
            titleBar.ButtonForegroundColor = p.Fg;
            titleBar.ButtonHoverBackgroundColor = p.Sel;
            titleBar.ButtonHoverForegroundColor = p.Fg;
            titleBar.ButtonPressedBackgroundColor = p.Sel;
            titleBar.ButtonPressedForegroundColor = p.Fg;
        }

        // サムネイル正方形の余白合成色。キャッシュキーに含めているので、
        // 変更後は自動的に新しい色で再生成される（ItemsSource張り直しで即時反映）。
        var newBg = Color.FromArgb(255, p.Thumb.R, p.Thumb.G, p.Thumb.B);
        if (newBg != _bgColor)
        {
            _bgColor = newBg;
            if (ThumbGridView.ItemsSource != null)
            {
                var current = ThumbGridView.ItemsSource;
                ThumbGridView.ItemsSource = null;
                ThumbGridView.ItemsSource = current;
            }
        }
    }

    /// <summary>タグ名・ファイル名だけに適用するフォントを更新する（UIクロームには適用しない）。
    /// 源真ゴシックPのように1つのファミリー名の中に複数の太さ(ExtraLight〜Black)を持つフォントは、
    /// FontFamily名の指定だけでは常にRegular相当の見た目になってしまうため、
    /// weightで明示的に太さ（usWeightClass相当、100〜900）も指定する。</summary>
    private void ApplyNameFont(string fontName, int weight)
    {
        try { UiSettings.Instance.NameFontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontName); }
        catch { /* 不正なフォント名でも落とさない */ }
        UiSettings.Instance.NameFontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)weight };
    }

    /// <summary>タグ名（サイドバーのタグ一覧・ファイル下のタグチップ）だけに適用するフォントを更新する。
    /// ApplyNameFont（ファイル名用）とは別に、独立して切り替えられるようにしている。</summary>
    private void ApplyTagFont(string fontName, int weight)
    {
        try { UiSettings.Instance.TagFontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontName); }
        catch { /* 不正なフォント名でも落とさない */ }
        UiSettings.Instance.TagFontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)weight };
    }

    /// <summary>Windowsのフォントフォルダにインストールされている全フォントファミリー名を取得する。
    /// Python版がtkinterのfont.families()相当で一覧していたのと同じく、実機にインストール
    /// されているフォントをそのまま列挙する（固定リストではない）。</summary>
    /// <summary>System.Drawing.Text.InstalledFontCollectionでのフォント列挙はやや重く、
    /// これを設定ダイアログを開くたびに毎回UIスレッド上で同期的に実行していたため、
    /// 「設定ボタンを押してからダイアログが表示されるまで時間がかかる」原因になっていた。
    /// インストール済みフォントは実行中に変わらない前提で、初回の結果をプロセス内でキャッシュする。</summary>
    private static List<string>? _installedFontFamilyNamesCache;

    private static List<string> GetInstalledFontFamilyNames()
    {
        if (_installedFontFamilyNamesCache != null) return _installedFontFamilyNamesCache;
        try
        {
            using var collection = new System.Drawing.Text.InstalledFontCollection();
            _installedFontFamilyNamesCache = collection.Families
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            _installedFontFamilyNamesCache = AppConfig.FontChoices.ToList(); // 取得失敗時のフォールバック
        }
        return _installedFontFamilyNamesCache;
    }

    /// <summary>「小・中・大」ラベルを実際のpx値に変換する（未知のラベル・旧設定ファイルからの
    /// 読み込み失敗時は「中」相当にフォールバック）。</summary>
    private static double ResolveTagChipFontSize(string label)
    {
        foreach (var (l, size) in AppConfig.TagChipSizeOptions)
            if (l == label) return size;
        return 11;
    }

    /// <summary>「小」は7文字、「中」「大」は4文字。</summary>
    private static int ResolveTagChipMaxCharsFromLabel(string label) => label == "小" ? 7 : 4;

    /// <summary>タグチップサイズ「小」の時、または「2列表示」オプションがオンの時に2行表示にする。
    /// 「小」は文字が小さく1行だと下に余白が余ってしまうため、「2列表示」はユーザーが明示的に
    /// 選んだ場合。</summary>
    private static bool ResolveTagChipTwoRows(string label, bool twoLineMode) => label == "小" || twoLineMode;

    /// <summary>タグチップ表示エリアの確保高さ（px）。2行表示の時は2行分、それ以外は1行分。
    /// 「2列表示」オプション時は「小」より文字が大きいぶん2行分の高さも大きくなる
    /// （実際の見た目の高さのみで、セルの行の高さは別途Marginで相殺するため増えない）。</summary>
    private static double ResolveTagsAreaHeight(string label, bool twoLineMode)
    {
        if (twoLineMode) return label switch { "小" => 30, "中" => 35, _ => 40 };
        return label == "小" ? 30 : 24;
    }

    /// <summary>以前は「2列表示」オプション時に2行目の高さぶんをサムネイル側へ重ねて表示するための
    /// 負のマージンを返していたが、タグチップ表示位置をファイル名の下（Grid.Row=2、最終行）に
    /// 変更したことで重ねる必要がなくなったため、常にマージン0を返す。2行分の高さは
    /// ResolveTagsAreaHeightがそのままGrid.Row=2の確保高さとして返し、Grid側がAutoなので
    /// セルごと下方向に拡大される。</summary>
    private static Microsoft.UI.Xaml.Thickness ResolveTagsAreaOverlapMargin(string label, bool twoLineMode)
    {
        return new Microsoft.UI.Xaml.Thickness(0);
    }

    /// <summary>タグチップの2行表示関連（TagChipTwoRows/TagsAreaHeight/TagsAreaOverlapMargin）を
    /// まとめて_configから算出してUiSettingsへ反映する。呼び出し箇所が複数（初期化・設定保存・
    /// タグチップサイズ変更）あるため共通化した。</summary>
    private void ApplyTagChipRowSettings()
    {
        UiSettings.Instance.TagChipTwoRows = ResolveTagChipTwoRows(_config.TagChipSize, _config.TagChipTwoLineMode);
        UiSettings.Instance.TagsAreaHeight = ResolveTagsAreaHeight(_config.TagChipSize, _config.TagChipTwoLineMode);
        UiSettings.Instance.TagsAreaOverlapMargin = ResolveTagsAreaOverlapMargin(_config.TagChipSize, _config.TagChipTwoLineMode);
    }

    /// <summary>「広い・普通・狭い」ラベルをグリッドのセルMargin(px)に変換する（未知のラベル・
    /// 旧設定ファイルからの読み込み失敗時は「普通」相当にフォールバック）。</summary>
    private static double ResolveGridSpacingMargin(string label)
    {
        foreach (var (l, margin) in AppConfig.GridSpacingOptions)
            if (l == label) return margin;
        return 4;
    }

    /// <summary>「フォルダのパスを変更」「ファイルパスの更新」の2操作は
    /// 独立したツールバーボタン＋専用ダイアログだったが、設定画面へ統合したため
    /// このメソッドは廃止（各ボタンのClickハンドラ自体はSettingsButton_Click内に移設）。</summary>

    /// <summary>「ファイルパスの更新」：Python版 _manual_repath の移植。
    /// DB登録済みだが実体が見つからないファイルを一覧表示し、1件ずつ削除／手動でパスを再指定、
    /// または重複（同名の実在ファイル・同名フォルダ）や全件をまとめて削除できる。</summary>
    private async Task ShowMissingFilesDialogAsync()
    {
        var allFiles = _db.GetAllFiles();
        var missing = allFiles
            .Where(f => !System.IO.File.Exists(f.Path) && !System.IO.Directory.Exists(f.Path))
            .ToList();

        if (missing.Count == 0)
        {
            var info = new ContentDialog
            {
                Title = "ファイルパスの更新",
                Content = "見つからないファイルはありません。",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await info.ShowAsync();
            return;
        }

        var missingIdSet = missing.Select(f => f.Id).ToHashSet();
        // 実在する（見つからないリストに含まれない）ファイルのファイル名集合
        var existingNames = allFiles
            .Where(f => !missingIdSet.Contains(f.Id))
            .Select(f => System.IO.Path.GetFileName(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 実在するファイルの拡張子除きファイル名（stem）集合
        var existingStems = allFiles
            .Where(f => !missingIdSet.Contains(f.Id))
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 見つからないファイルのうち、同名の実在ファイルが別途登録されているもの＝重複扱い
        var dupIds = missing
            .Where(f => existingNames.Contains(System.IO.Path.GetFileName(f.Path)))
            .Select(f => f.Id).ToHashSet();
        // 見つからないもののうち拡張子なし（＝フォルダとみなす）で、
        // 実在ファイルの拡張子除き名と一致するものを重複フォルダとして抽出
        var dupFolderIds = missing
            .Where(f => System.IO.Path.GetExtension(f.Path) == ""
                        && existingStems.Contains(System.IO.Path.GetFileName(f.Path)))
            .Select(f => f.Id).ToHashSet();

        var items = new ObservableCollection<FileRecord>(missing);

        var listView = new ListView
        {
            Height = 260,
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = items,
            DisplayMemberPath = "Path",
        };

        var countText = new TextBlock { FontSize = 12, Opacity = 0.8 };
        var dupCountText = new TextBlock { FontSize = 12, Opacity = 0.8 };
        var dupFolderCountText = new TextBlock { FontSize = 12, Opacity = 0.8 };
        void RefreshCounts()
        {
            countText.Text = $"残り {items.Count} 件";
            dupCountText.Text = $"うち重複: {items.Count(f => dupIds.Contains(f.Id))} 件";
            dupFolderCountText.Text = $"うち同名フォルダ: {items.Count(f => dupFolderIds.Contains(f.Id))} 件";
        }
        RefreshCounts();

        var deleteOneButton = new Button { Content = "選択した1件を削除" };
        var deleteDupButton = new Button
        {
            Content = "重複を削除",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xB7, 0x79, 0x1F)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        var deleteAllButton = new Button
        {
            Content = "全件を削除",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC0, 0x39, 0x2B)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        var manualOneButton = new Button { Content = "1件を手動で再指定..." };

        var buttonRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttonRow1.Children.Add(deleteOneButton);
        buttonRow1.Children.Add(manualOneButton);
        var buttonRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttonRow2.Children.Add(deleteDupButton);
        buttonRow2.Children.Add(deleteAllButton);
        var buttonPanel = new StackPanel { Spacing = 6 };
        buttonPanel.Children.Add(buttonRow1);
        buttonPanel.Children.Add(buttonRow2);

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = $"見つからないファイル: {missing.Count}件",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(listView);
        panel.Children.Add(countText);
        panel.Children.Add(dupCountText);
        panel.Children.Add(dupFolderCountText);
        panel.Children.Add(buttonPanel);

        var dialog = new ContentDialog
        {
            Title = $"見つからないファイル ({missing.Count}件)",
            Content = panel,
            CloseButtonText = "閉じる",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        void RemoveEntry(FileRecord rec)
        {
            _db.DeleteFile(rec.Id);
            items.Remove(rec);
            dupIds.Remove(rec.Id);
            dupFolderIds.Remove(rec.Id);
        }

        deleteOneButton.Click += async (_, _) =>
        {
            if (listView.SelectedItem is not FileRecord rec) return;
            var confirm = new ContentDialog
            {
                Title = "削除確認",
                Content = $"このファイルをリストから削除しますか？\n（ファイル本体は削除されません）\n\n{rec.Path}",
                PrimaryButtonText = "削除",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            RemoveEntry(rec);
            RefreshCounts();
            RefreshFilesView();
            RefreshNavList();
        };

        deleteDupButton.Click += async (_, _) =>
        {
            var targets = items.Where(f => dupIds.Contains(f.Id)).ToList();
            if (targets.Count == 0)
            {
                var infoDlg = new ContentDialog
                {
                    Title = "重複なし",
                    Content = "同名の実在ファイルを持つ重複はありません。",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await infoDlg.ShowAsync();
                return;
            }
            var confirm = new ContentDialog
            {
                Title = "重複削除確認",
                Content = $"同名の実在ファイルが別途登録されている {targets.Count} 件を\n" +
                          "見つからないファイルのリストから削除しますか？\n（ファイル本体は削除されません）",
                PrimaryButtonText = "削除",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            foreach (var rec in targets) RemoveEntry(rec);
            RefreshCounts();
            RefreshFilesView();
            RefreshNavList();
        };

        deleteAllButton.Click += async (_, _) =>
        {
            if (items.Count == 0) return;
            var confirm = new ContentDialog
            {
                Title = "全件削除確認",
                Content = $"見つからないファイル {items.Count} 件を全てリストから削除しますか？\n（ファイル本体は削除されません）",
                PrimaryButtonText = "削除",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            foreach (var rec in items.ToList()) RemoveEntry(rec);
            RefreshCounts();
            RefreshFilesView();
            RefreshNavList();

            var doneDlg = new ContentDialog
            {
                Title = "削除完了",
                Content = "見つからないファイルをすべて削除しました。",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await doneDlg.ShowAsync();
            dialog.Hide();
        };

        manualOneButton.Click += async (_, _) =>
        {
            if (listView.SelectedItem is not FileRecord rec) return;
            if (_filePickerOpen) return;
            _filePickerOpen = true;
            Windows.Storage.StorageFile? file;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");
                file = await picker.PickSingleFileAsync();
            }
            finally
            {
                _filePickerOpen = false;
            }
            if (file == null) return;

            _db.UpdatePath(rec.Id, file.Path);
            RemoveEntry(rec);
            RefreshCounts();
            RefreshFilesView();
        };

        await dialog.ShowAsync();
    }

    /// <summary>「設定を初期化」の本体。アップデート後の不具合対策用に、タグ一覧・タグの色・
    /// グリッド関連設定（サムネイル解像度/セル表示サイズ）・各ファイルのアクセス日/追加日/
    /// 開いた回数/スターはそのまま保持し、それ以外の見た目・操作系の設定を初期値に戻す。
    /// あわせてサムネイルキャッシュも全削除する（設定ファイルの中身が変わることで
    /// キャッシュキーの前提が変わるわけではないが、「不具合対策としてまっさらにしたい」
    /// という目的に合わせて明示的に消す）。
    /// 実行前に必ず警告ダイアログで確認を取る（元に戻せない操作のため）。</summary>
    private async Task ResetSettingsWithConfirmationAsync()
    {
        var confirmDialog = new ContentDialog
        {
            Title = "設定を初期化しますか？",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "テーマ・フォント・文字サイズ・並替・ファイルを開く操作方法などの設定が" +
                       "初期値に戻り、サムネイルキャッシュもすべて削除されます（次回表示時に再生成されます）。" +
                       "\n\nタグ一覧・タグの色・グリッドのセル表示サイズ・サムネイル解像度、" +
                       "および各ファイルのアクセス日・追加日・開いた回数・スターは保持されます。" +
                       "\n\nこの操作は元に戻せません。実行しますか？",
            },
            PrimaryButtonText = "初期化する",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        var confirmResult = await confirmDialog.ShowAsync();
        if (confirmResult != ContentDialogResult.Primary) return;

        try
        {
            _thumbCache.Clear();
            _config = AppConfig.ResetKeepingGridSettings(_config);
            _thumbSize = _config.ThumbSize;

            // 新しい既定値を画面へ即座に反映する（保存ボタンを押した時と同じ手順）。
            ApplyTheme(_config.Theme);
            ApplyNameFont(_config.UiFont, _config.UiFontWeight);
            ApplyTagFont(_config.TagFont, _config.TagFontWeight);
            RefreshNavList();
            UiSettings.Instance.TagListFontSize = _config.TagListFontSize;
            UiSettings.Instance.FileListFontSize = _config.FileListFontSize;
            UiSettings.Instance.FileListTagsFontSize = ResolveTagChipFontSize(_config.TagChipSize);
            UiSettings.Instance.TagChipMaxChars = ResolveTagChipMaxCharsFromLabel(_config.TagChipSize);
            ApplyTagChipRowSettings();
            UiSettings.Instance.GridItemMargin = new Microsoft.UI.Xaml.Thickness(ResolveGridSpacingMargin(_config.GridSpacing));
            UiSettings.Instance.ShowStar = _config.ShowStar;
            UiSettings.Instance.ShowOpenCountBadge =
                _config.OpenCountBadgeMode == "always" ||
                _config.SortKey == "open_count" || _currentFilterKey == "most_opened";

            foreach (var item in _items)
                SetItemTags(item, _db.GetFileTags(item.Id));

            // サムネイルキャッシュを消したので、現在表示中のグリッドも全コンテナ再生成させて
            // シェルAPI呼び出しからやり直させる。
            var current = ThumbGridView.ItemsSource;
            ThumbGridView.ItemsSource = null;
            ThumbGridView.ItemsSource = current;

            TagActionStatusText.Text = "設定を初期化しました（タグ・スター等の情報は保持されています）。";
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            TagActionStatusText.Text = $"設定の初期化に失敗しました（ログに記録しました）: {ex.Message}";
        }
    }


    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // ダイアログ構築中（フォント列挙等）にボタンを連打されると、前の呼び出しがまだ
        // ShowAsync()中のうちに次の呼び出しがまたShowAsync()しようとして
        // 「Only a single ContentDialog can be open at any time」で未処理例外になり
        // アプリごと落ちていた。二重起動をここで弾く。
        if (_settingsDialogOpen) return;
        _settingsDialogOpen = true;
        try
        {
            await ShowSettingsDialogAsync();
        }
        finally
        {
            _settingsDialogOpen = false;
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        var themeCombo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var key in AppTheme.Themes.Keys) themeCombo.Items.Add(AppTheme.GetDisplayName(key));
        themeCombo.SelectedItem = AppTheme.GetDisplayName(_config.Theme);

        var installedFontNames = GetInstalledFontFamilyNames();

        var fontCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var f in installedFontNames) fontCombo.Items.Add(f);
        fontCombo.SelectedItem = _config.UiFont;
        if (fontCombo.SelectedItem == null && fontCombo.Items.Count > 0) fontCombo.SelectedIndex = 0;

        // フォントの太さ。源真ゴシックPのように1つのファミリー名の中に複数の太さを持つフォントは、
        // ファミリー名の指定だけでは太さの違いが反映されないため、太さを独立して選べるようにする。
        var fontWeightCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (label, _) in AppConfig.FontWeightOptions) fontWeightCombo.Items.Add(label);
        var currentFontWeightLabel = AppConfig.FontWeightOptions
            .FirstOrDefault(o => o.Weight == _config.UiFontWeight).Label ?? AppConfig.FontWeightOptions[3].Label;
        fontWeightCombo.SelectedItem = currentFontWeightLabel;
        if (fontWeightCombo.SelectedItem == null) fontWeightCombo.SelectedIndex = 3; // 既定「標準 (Regular)」

        // タグ名（サイドバーのタグ一覧・ファイル下のタグチップ）用のフォント。
        // ファイル名のフォントとは独立に指定できる。
        var tagFontCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var f in installedFontNames) tagFontCombo.Items.Add(f);
        tagFontCombo.SelectedItem = _config.TagFont;
        if (tagFontCombo.SelectedItem == null && tagFontCombo.Items.Count > 0) tagFontCombo.SelectedIndex = 0;

        var tagFontWeightCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (label, _) in AppConfig.FontWeightOptions) tagFontWeightCombo.Items.Add(label);
        var currentTagFontWeightLabel = AppConfig.FontWeightOptions
            .FirstOrDefault(o => o.Weight == _config.TagFontWeight).Label ?? AppConfig.FontWeightOptions[3].Label;
        tagFontWeightCombo.SelectedItem = currentTagFontWeightLabel;
        if (tagFontWeightCombo.SelectedItem == null) tagFontWeightCombo.SelectedIndex = 3; // 既定「標準 (Regular)」

        // ── 各スライダー: ヘッダーは使わず、値込みのラベルをバーの真上に1つだけ置く ──
        var tagFontLabel = new TextBlock { Text = $"タグリストの文字サイズ: {(int)_config.TagListFontSize}px" };
        var tagFontSlider = new Slider
        {
            Minimum = 8, Maximum = 24, StepFrequency = 1, Value = _config.TagListFontSize,
        };
        tagFontSlider.ValueChanged += (_, args) =>
            tagFontLabel.Text = $"タグリストの文字サイズ: {(int)args.NewValue}px";

        var fileFontLabel = new TextBlock { Text = $"ファイルリストの文字サイズ: {(int)_config.FileListFontSize}px" };
        var fileFontSlider = new Slider
        {
            Minimum = 8, Maximum = 24, StepFrequency = 1, Value = _config.FileListFontSize,
        };
        fileFontSlider.ValueChanged += (_, args) =>
            fileFontLabel.Text = $"ファイルリストの文字サイズ: {(int)args.NewValue}px";

        // ファイル下のタグチップ文字サイズはファイル名と切り離して独立に調整する。
        // 細かいpx指定は不要なため、スライダーではなく「小・中・大」の3択にしている。
        var tagChipSizeCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (label, _) in AppConfig.TagChipSizeOptions) tagChipSizeCombo.Items.Add(label);
        tagChipSizeCombo.SelectedItem = _config.TagChipSize;
        if (tagChipSizeCombo.SelectedItem == null) tagChipSizeCombo.SelectedIndex = 1; // 既定「中」

        // タグチップを常に2列（2行）表示にするオプション。オンの時、1行に収まらないぶんの
        // タグは2行目に表示される。
        var tagChipTwoLineCheck = new CheckBox
        {
            Content = "タグチップを2列表示にする",
            IsChecked = _config.TagChipTwoLineMode,
        };

        // グリッドのセルとセルの間隔。細かいpx指定は不要なため、サムネイル解像度やタグチップサイズと
        // 同様にスライダーではなく「広い・普通・狭い」の3択にしている。
        var gridSpacingCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (label, _) in AppConfig.GridSpacingOptions) gridSpacingCombo.Items.Add(label);
        gridSpacingCombo.SelectedItem = _config.GridSpacing;
        if (gridSpacingCombo.SelectedItem == null) gridSpacingCombo.SelectedIndex = 1; // 既定「普通」

        // 以前はスライダー(80〜320px)だったが、選択肢を絞ってわかりやすくするため
        // 「高・中・低」の3択に変更（AppConfig.ThumbSizeOptions参照）。
        var sizeCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (label, _) in AppConfig.ThumbSizeOptions) sizeCombo.Items.Add(label);
        var currentThumbSizeLabel = AppConfig.ThumbSizeOptions
            .FirstOrDefault(o => o.Size == _config.ThumbSize).Label ?? AppConfig.ThumbSizeOptions[^1].Label;
        sizeCombo.SelectedItem = currentThumbSizeLabel;
        if (sizeCombo.SelectedItem == null) sizeCombo.SelectedIndex = AppConfig.ThumbSizeOptions.Length - 1; // 既定「高」

        // ファイルを開く操作方式（ダブルクリック／シングルクリック）。
        // 以前はプルダウンだったが、選択肢が2つしかなく分かりにくいためチェックボックスに変更。
        // 既定はシングルクリックで開く。
        var openClickCheck = new CheckBox
        {
            Content = "シングルクリックで開く",
            IsChecked = _config.OpenClickMode == "single",
        };

        // 「開いた回数」バッジの表示。以前はプルダウン（自動／常に表示）だったが、
        // チェックボックスに変更。オフのときは従来の「自動」と同じ挙動
        // （並替が「開いた回数」または「よく使うファイル」表示中だけバッジを出す）を維持し、
        // オンのときは常時バッジを表示する。既定はオフ（自動＝通常は非表示）。
        var openCountBadgeCheck = new CheckBox
        {
            Content = "開いた回数バッジを常に表示する",
            IsChecked = _config.OpenCountBadgeMode == "always",
        };

        // 動画サムネイルへの再生時間表示オン/オフ
        var showDurationCheck = new CheckBox
        {
            Content = "動画サムネイルに再生時間を表示する",
            IsChecked = _config.ShowVideoDuration,
        };

        // サムネ右上のスター評価を常に表示するかどうか。既定はオン。
        var showStarCheck = new CheckBox
        {
            Content = "スターを常に表示する",
            IsChecked = _config.ShowStar,
        };

        // ラベルとスライダーは対になっているため、間隔を詰めて1組に見えるようにする。
        var tagFontGroup = new StackPanel { Spacing = 2 };
        tagFontGroup.Children.Add(tagFontLabel);
        tagFontGroup.Children.Add(tagFontSlider);

        var fileFontGroup = new StackPanel { Spacing = 2 };
        fileFontGroup.Children.Add(fileFontLabel);
        fileFontGroup.Children.Add(fileFontSlider);

        // 4つ並ぶチェックボックス項目はお互いの間隔を詰める。
        var checkGroup = new StackPanel { Spacing = 4 };
        checkGroup.Children.Add(openClickCheck);
        checkGroup.Children.Add(openCountBadgeCheck);
        checkGroup.Children.Add(showDurationCheck);
        checkGroup.Children.Add(showStarCheck);

        // 「既定のソフト以外で開く」用に登録する外部アプリ（.exe）。テキストボックスは
        // パス確認用の表示専用にし、実際の指定は「参照...」ボタンからのファイル選択で行う
        // （手入力による誤字・存在しないパスの指定を避けるため）。
        var externalAppBox = new TextBox
        {
            Text = _config.ExternalAppPath,
            IsReadOnly = true,
            PlaceholderText = "未設定",
            Width = 220,
        };
        // 長いパスだとテキストボックスが伸びてボタンが右に押し出され、押せなくなる。
        // Widthを固定した上で、全文はツールチップで確認できるようにする。
        ToolTipService.SetToolTip(externalAppBox, externalAppBox.Text);
        externalAppBox.TextChanged += (_, _) => ToolTipService.SetToolTip(externalAppBox, externalAppBox.Text);
        var externalAppBrowseButton = new Button { Content = "参照..." };
        var externalAppClearButton = new Button { Content = "クリア" };
        externalAppBrowseButton.Click += async (_, _) =>
        {
            if (_filePickerOpen) return;
            _filePickerOpen = true;
            Windows.Storage.StorageFile? file;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".exe");
                file = await picker.PickSingleFileAsync();
            }
            finally
            {
                _filePickerOpen = false;
            }
            if (file == null) return;
            externalAppBox.Text = file.Path;
        };
        externalAppClearButton.Click += (_, _) => externalAppBox.Text = "";
        var externalAppRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        externalAppRow.Children.Add(externalAppBox);
        externalAppRow.Children.Add(externalAppBrowseButton);
        externalAppRow.Children.Add(externalAppClearButton);

        // 「既定のソフト以外で開く」を右クリックメニューに出す対象のファイル種別。
        // 画像・圧縮ファイル・動画ファイルの3種類から個別にON/OFFできる（既定は全部オン）。
        var externalAppImageCheck = new CheckBox { Content = "画像ファイル", IsChecked = _config.ExternalAppForImage };
        var externalAppArchiveCheck = new CheckBox { Content = "圧縮ファイル", IsChecked = _config.ExternalAppForArchive };
        var externalAppVideoCheck = new CheckBox { Content = "動画ファイル", IsChecked = _config.ExternalAppForVideo };
        var externalAppTypeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        externalAppTypeRow.Children.Add(externalAppImageCheck);
        externalAppTypeRow.Children.Add(externalAppArchiveCheck);
        externalAppTypeRow.Children.Add(externalAppVideoCheck);

        // ラベルを左、プルダウンを右に並べた行を作るヘルパー。
        // ラベル幅を揃えることでプルダウンの開始位置が縦に揃い、見やすくなる。
        static StackPanel MakeComboRow(string label, ComboBox combo)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
            };
            row.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 160,
            });
            row.Children.Add(combo);
            return row;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 320, Padding = new Thickness(0, 0, 28, 0) };
        panel.Children.Add(MakeComboRow("テーマ", themeCombo));
        panel.Children.Add(MakeComboRow("ファイル名のフォント", fontCombo));
        panel.Children.Add(MakeComboRow("ファイル名フォントの太さ", fontWeightCombo));
        panel.Children.Add(MakeComboRow("タグ名のフォント", tagFontCombo));
        panel.Children.Add(MakeComboRow("タグ名フォントの太さ", tagFontWeightCombo));
        panel.Children.Add(tagFontGroup);
        panel.Children.Add(fileFontGroup);
        panel.Children.Add(tagChipTwoLineCheck);
        panel.Children.Add(checkGroup);
        panel.Children.Add(MakeComboRow("タグチップのサイズ", tagChipSizeCombo));
        panel.Children.Add(MakeComboRow("グリッドの間隔", gridSpacingCombo));
        panel.Children.Add(MakeComboRow("サムネイル解像度", sizeCombo));
        panel.Children.Add(new TextBlock
        {
            Text = "※ グリッドのセル表示サイズはツールバー上部の「表示サイズ」スライダーで変更できます。" +
                   "ここの「サムネイル解像度」はデコード品質・キャッシュキーにのみ影響し、見た目の大きさには影響しません。",
            FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
        });

        // ── 既定のソフト以外で開く ─────────────────────────────
        panel.Children.Add(new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.3 },
            Margin = new Thickness(0, 4, 0, 4),
        });
        panel.Children.Add(new TextBlock { Text = "既定のソフト以外で開く", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "ここで指定したアプリを、右クリックメニューの「既定のソフト以外で開く」から起動できます。",
            FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(externalAppRow);
        panel.Children.Add(new TextBlock
        {
            Text = "対象のファイル種類",
            FontSize = 12, Opacity = 0.8,
        });
        panel.Children.Add(externalAppTypeRow);

        // ── データ管理（以前は独立した「データ管理」ボタン＋専用ダイアログだったが、
        // ツールバーを整理するため設定画面に統合した）──────────────────────
        panel.Children.Add(new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.3 },
            Margin = new Thickness(0, 4, 0, 4),
        });
        panel.Children.Add(new TextBlock { Text = "データ管理", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var renameFolderButton = new Button { Content = "フォルダのパスを変更...", HorizontalAlignment = HorizontalAlignment.Stretch };
        var missingFilesButton = new Button { Content = "ファイルパスの更新...", HorizontalAlignment = HorizontalAlignment.Stretch };
        var resetSettingsButton = new Button
        {
            Content = "設定を初期化...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        panel.Children.Add(renameFolderButton);
        panel.Children.Add(missingFilesButton);
        panel.Children.Add(resetSettingsButton);

        // ── バージョン表示（設定の一番下）────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "TanukiTag v1.0.1",
            FontSize = 11,
            Opacity = 0.5,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var dialog = new ContentDialog
        {
            Title = "設定",
            // 設定項目が増えて縦に長くなったため、画面が小さい環境でも全項目に到達できるよう
            // ScrollViewerで包む。MaxHeightは画面の高さから余白を引いた分に収まるよう、
            // XamlRoot（ウィンドウ）のサイズから動的に決める（極端に小さい画面でも
            // ダイアログ自体がはみ出さないようにするための保険として下限480pxも設ける）。
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(480, Content.XamlRoot.Size.Height - 240),
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        // データ管理系のボタンは内部で独自のContentDialog/FileOpenPickerを開くため、
        // 設定ダイアログを閉じてから起動する（ContentDialogは同時に1つしか表示できない）。
        // Hide()はキャンセル扱いになりShowAsync()の戻り値がNoneになるため、以降の保存処理は
        // 走らない（設定変更を保存せずにデータ管理操作へ抜ける形になる。従来の「データ管理」
        // ボタン単体のダイアログでも同様の挙動だったため、動作としては変わらない）。
        renameFolderButton.Click += (_, _) =>
        {
            dialog.Hide();
            RenameFolderButton_Click(renameFolderButton, new RoutedEventArgs());
        };
        missingFilesButton.Click += (_, _) =>
        {
            dialog.Hide();
            _ = ShowMissingFilesDialogAsync();
        };
        resetSettingsButton.Click += (_, _) =>
        {
            dialog.Hide();
            _ = ResetSettingsWithConfirmationAsync();
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _config.Theme = themeCombo.SelectedItem is string selectedThemeName
            ? AppTheme.GetKeyFromDisplayName(selectedThemeName)
            : _config.Theme;
        _config.UiFont = fontCombo.SelectedItem as string ?? _config.UiFont;
        var selectedFontWeightLabel = fontWeightCombo.SelectedItem as string;
        var matchedFontWeight = AppConfig.FontWeightOptions.FirstOrDefault(o => o.Label == selectedFontWeightLabel);
        _config.UiFontWeight = matchedFontWeight.Label != null ? matchedFontWeight.Weight : _config.UiFontWeight;
        _config.TagFont = tagFontCombo.SelectedItem as string ?? _config.TagFont;
        var selectedTagFontWeightLabel = tagFontWeightCombo.SelectedItem as string;
        var matchedTagFontWeight = AppConfig.FontWeightOptions.FirstOrDefault(o => o.Label == selectedTagFontWeightLabel);
        _config.TagFontWeight = matchedTagFontWeight.Label != null ? matchedTagFontWeight.Weight : _config.TagFontWeight;
        _config.TagListFontSize = tagFontSlider.Value;
        _config.FileListFontSize = fileFontSlider.Value;
        _config.TagChipSize = tagChipSizeCombo.SelectedItem as string ?? _config.TagChipSize;
        _config.TagChipTwoLineMode = tagChipTwoLineCheck.IsChecked ?? false;
        _config.GridSpacing = gridSpacingCombo.SelectedItem as string ?? _config.GridSpacing;
        _config.OpenClickMode = (openClickCheck.IsChecked ?? true) ? "single" : "double";
        _config.OpenCountBadgeMode = (openCountBadgeCheck.IsChecked ?? false) ? "always" : "auto";
        var showDurationChanged = (showDurationCheck.IsChecked ?? true) != _config.ShowVideoDuration;
        _config.ShowVideoDuration = showDurationCheck.IsChecked ?? true;
        _config.ShowStar = showStarCheck.IsChecked ?? true;
        var selectedThumbSizeLabel = sizeCombo.SelectedItem as string;
        var matchedThumbSize = AppConfig.ThumbSizeOptions.FirstOrDefault(o => o.Label == selectedThumbSizeLabel);
        var newSize = matchedThumbSize.Label != null ? matchedThumbSize.Size : _config.ThumbSize;
        var sizeChanged = newSize != _config.ThumbSize;
        _config.ThumbSize = newSize;
        _config.ExternalAppPath = externalAppBox.Text?.Trim() ?? "";
        _config.ExternalAppForImage = externalAppImageCheck.IsChecked ?? true;
        _config.ExternalAppForArchive = externalAppArchiveCheck.IsChecked ?? true;
        _config.ExternalAppForVideo = externalAppVideoCheck.IsChecked ?? true;
        var saveOk = _config.Save();

        ApplyTheme(_config.Theme);
        ApplyNameFont(_config.UiFont, _config.UiFontWeight);
        ApplyTagFont(_config.TagFont, _config.TagFontWeight);
        // フォントの太さ(NameFontWeight/TagFontWeight)はNavRow生成時に値をコピーする方式のため、
        // 既存の行には反映されない。設定変更後は一覧を作り直して即座に反映させる。
        RefreshNavList();
        // UiSettings のプロパティ変更が INotifyPropertyChanged 経由で
        // x:Bind に伝わり、ItemsSource の張り直し無しに即座に反映される
        UiSettings.Instance.TagListFontSize = _config.TagListFontSize;
        UiSettings.Instance.FileListFontSize = _config.FileListFontSize;
        UiSettings.Instance.FileListTagsFontSize = ResolveTagChipFontSize(_config.TagChipSize);
        UiSettings.Instance.TagChipMaxChars = ResolveTagChipMaxCharsFromLabel(_config.TagChipSize);
        ApplyTagChipRowSettings();
        UiSettings.Instance.GridItemMargin = new Microsoft.UI.Xaml.Thickness(ResolveGridSpacingMargin(_config.GridSpacing));
        UiSettings.Instance.ShowStar = _config.ShowStar;
        // 「常に表示」⇔条件付き表示の切替を、フィルタや並替を変更し直さなくても即座に反映する
        UiSettings.Instance.ShowOpenCountBadge =
            _config.OpenCountBadgeMode == "always" ||
            _config.SortKey == "open_count" || _currentFilterKey == "most_opened";

        // タグチップの文字サイズが変わると1セルに収まる件数（SelectFittingTags）も変わるため、
        // 現在表示中のファイル一覧のタグ表示を再計算し直す。
        foreach (var item in _items)
            SetItemTags(item, _db.GetFileTags(item.Id));

        if (sizeChanged || showDurationChanged)
        {
            _thumbSize = newSize;
            if (sizeChanged)
            {
                // 解像度キーはSHA256ハッシュ化されているため、旧解像度分のエントリだけを
                // 選んで削除することができない。古い解像度のサムネイルはキャッシュキーが
                // 変わって二度とヒットしなくなり、放置するとDBが肥大化し続けるだけなので、
                // 解像度変更のタイミングでキャッシュを丸ごと削除して掃除する
                // （「設定を初期化」ボタンのCleanup処理と同じ ThumbnailCache.Clear() を使う）。
                _thumbCache.Clear();
            }
            // ItemsSource を張り直してGridViewの全コンテナを再生成させ、
            // 新しい解像度・再生時間表示設定でサムネイルを再読込させる
            // （キャッシュキーにも設定を反映済みなので、古い見た目のキャッシュがヒットすることはない）。
            var current = ThumbGridView.ItemsSource;
            ThumbGridView.ItemsSource = null;
            ThumbGridView.ItemsSource = current;
        }

        TagActionStatusText.Text = saveOk
            ? "設定を保存しました。"
            : $"設定の保存に失敗しました: {AppConfig.LastError?.Message}";
    }
}
