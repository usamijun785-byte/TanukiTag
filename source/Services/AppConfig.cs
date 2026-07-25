using System.Text.Json;
using System.Linq;

namespace TanukiTag.Services;

/// <summary>
/// Python版 DEFAULT_CONFIG / tagfiler_config.json に相当。
/// 今回のプロトタイプでは thumb_size / theme / ui_font のみ扱う。
/// </summary>
public class AppConfig
{
    /// <summary>サムネイル解像度（デコード幅の基準px）。スライダーではなく「高・中・低」の
    /// 3段階のみ（ThumbSizeOptions参照）。既定は"高"=320px。</summary>
    public int ThumbSize { get; set; } = 320;
    /// <summary>サムネイルグリッドのセル表示サイズ（px）。ThumbSize（デコード解像度）とは独立。
    /// トップバーのスライダーで変更し、次回起動時も同じ表示サイズを復元する。</summary>
    public double ThumbGridCellSize { get; set; } = 160;
    public string Theme { get; set; } = "dark";
    /// <summary>ファイル名の表示フォント。</summary>
    public string UiFont { get; set; } = "Yu Gothic UI";
    /// <summary>ファイル名フォントの太さ（CSS/OS2のusWeightClassと同じ100〜900の数値）。
    /// 源真ゴシックPのように同じファミリー名で複数の太さ(ExtraLight〜Black)を持つフォントの場合、
    /// FontFamily名の指定だけでは常にRegular相当の見た目になってしまうため、
    /// FontWeightを別途明示的に指定できるようにしている。</summary>
    public int UiFontWeight { get; set; } = 400;

    /// <summary>タグ名（サイドバーのタグ一覧・ファイル下のタグチップ）の表示フォント。
    /// ファイル名フォント(UiFont)とは独立に指定できる。</summary>
    public string TagFont { get; set; } = "Yu Gothic UI";
    /// <summary>タグ名フォントの太さ。UiFontWeightと同じ考え方（usWeightClass、100〜900）。</summary>
    public int TagFontWeight { get; set; } = 400;

    /// <summary>設定画面の「太さ」ドロップダウン用の選択肢（表示ラベル, usWeightClass値）。</summary>
    public static readonly (string Label, int Weight)[] FontWeightOptions =
    {
        ("極細 (Thin)", 100),
        ("エクストラライト (ExtraLight)", 200),
        ("ライト (Light)", 300),
        ("標準 (Regular)", 400),
        ("ミディアム (Medium)", 500),
        ("セミボールド (SemiBold)", 600),
        ("ボールド (Bold)", 700),
        ("エクストラボールド (ExtraBold)", 800),
        ("ブラック (Black)", 900),
    };
    public double TagListFontSize { get; set; } = 13;
    public double FileListFontSize { get; set; } = 13;
    /// <summary>ファイルグリッドのタグチップ文字サイズ。ファイル名の文字サイズとは独立。
    /// スライダーではなく「小・中・大」の3段階のみ（TagChipSizeOptions参照）。既定は"中"=11px。</summary>
    public string TagChipSize { get; set; } = "中";

    /// <summary>ファイル下のタグチップを常に2列（2行）表示にするかどうか。既定はオン。
    /// オンの場合、タグチップサイズ設定（TagChipSize）に関係なく2行分のタグを表示する。</summary>
    public bool TagChipTwoLineMode { get; set; } = true;
    public string SortKey { get; set; } = "accessed";

    /// <summary>左サイドバーのタグ一覧（グループ内のタグ、および未分類のタグ）の並び順。
    /// "count_desc"=件数の多い順（既定・従来の挙動）、"name_asc"/"name_desc"=名前順、
    /// "added_asc"/"added_desc"=追加順（タグID順）。グループの並び順（GroupSortKey）とは独立に指定できる。</summary>
    public string TagSortKey { get; set; } = "count_desc";

    /// <summary>左サイドバーのグループヘッダーの並び順。"name_asc"=名前順（既定）、
    /// "added_asc"/"added_desc"=追加順（グループID順）。タグの並び順（TagSortKey）とは独立。</summary>
    public string GroupSortKey { get; set; } = "name_asc";

    /// <summary>タグ並び替えメニュー用の選択肢（表示ラベル, 内部キー）。</summary>
    public static readonly (string Label, string Key)[] TagSortKeyOptions =
    {
        ("件数の多い順", "count_desc"),
        ("名前順 (A→Z)", "name_asc"),
        ("名前順 (Z→A)", "name_desc"),
        ("追加順 (古い→新しい)", "added_asc"),
        ("追加順 (新しい→古い)", "added_desc"),
    };

    /// <summary>グループ並び替えメニュー用の選択肢（表示ラベル, 内部キー）。</summary>
    public static readonly (string Label, string Key)[] GroupSortKeyOptions =
    {
        ("名前順 (A→Z)", "name_asc"),
        ("名前順 (Z→A)", "name_desc"),
        ("追加順 (古い→新しい)", "added_asc"),
        ("追加順 (新しい→古い)", "added_desc"),
    };

    /// <summary>動画サムネイルに再生時間（例: 3:07）を表示するかどうか。</summary>
    public bool ShowVideoDuration { get; set; } = true;

    /// <summary>サムネ右上のスター評価を常に表示するかどうか。既定はオン（常に表示）。</summary>
    public bool ShowStar { get; set; } = true;

    /// <summary>ファイルを開く操作方式。"single"=シングルクリックで開く（既定）、"double"=ダブルクリックで開く。</summary>
    public string OpenClickMode { get; set; } = "single";

    /// <summary>設定画面のドロップダウン用の選択肢（表示ラベル, 内部キー）。</summary>
    public static readonly (string Label, string Key)[] OpenClickModeOptions =
    {
        ("ダブルクリックで開く", "double"),
        ("シングルクリックで開く", "single"),
    };

    /// <summary>サムネ右上の「開いた回数」バッジの表示条件。
    /// "auto"=並替が「開いた回数」の時、または左メニューの「よく使うファイル」表示中のみ表示（既定）。
    /// "always"=常に表示する。</summary>
    public string OpenCountBadgeMode { get; set; } = "auto";

    public static readonly (string Label, string Key)[] OpenCountBadgeModeOptions =
    {
        ("並替が「開いた回数」／よく使うファイルの時だけ表示", "auto"),
        ("常に表示", "always"),
    };

    /// <summary>タグごとのファイルグリッド形状の選択肢（表示ラベル, 内部キー）。
    /// キーはUiSettings.GridShape / TagRecord.GridShapeで使う値と一致させる。</summary>
    public static readonly (string Label, string Key)[] GridShapeOptions =
    {
        ("正方形", "square"),
        ("縦長（コミック向け）", "portrait"),
        ("横長（動画向け）", "landscape"),
    };

    /// <summary>タグチップ文字サイズの「小・中・大」選択肢とその実際のpx値。</summary>
    public static readonly (string Label, double Size)[] TagChipSizeOptions =
    {
        ("小", 9),
        ("中", 11),
        ("大", 14),
    };

    /// <summary>サムネイル解像度の「高・中・低」選択肢とその実際のpx値（デコード幅の基準）。
    /// 以前はスライダー(80〜320px、8px刻み)だったが、選択肢を絞って分かりやすくするため
    /// 3段階に変更した。既定は"高"=320px。</summary>
    public static readonly (string Label, int Size)[] ThumbSizeOptions =
    {
        ("低", 120),
        ("中", 200),
        ("高", 320),
    };

    /// <summary>グリッドのセルとセルの間隔。スライダーではなく「広い・普通・狭い」の3段階のみ
    /// （GridSpacingOptions参照）。既定は"普通"＝これまで固定だった4px。</summary>
    public string GridSpacing { get; set; } = "普通";

    /// <summary>グリッド間隔の「広い・普通・狭い」選択肢とその実際のpx値（セルのMargin、全辺同値）。
    /// セル同士の実際の隙間はこの値の2倍（両側のMarginが合わさるため）になる。
    /// "普通"=4pxは、この設定を導入する以前から固定で使われていた値をそのまま基準にしている。</summary>
    public static readonly (string Label, double Margin)[] GridSpacingOptions =
    {
        ("狭い", 1),
        ("普通", 4),
        ("広い", 8),
    };
    public static readonly string[] FontChoices =
    {
        "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo",
        "MS UI Gothic", "MS Gothic", "MS PGothic",
        "BIZ UDGothic", "BIZ UDPGothic", "UD デジタル 教科書体 N-R",
        "Segoe UI", "Segoe UI Variable", "Arial", "Tahoma", "Verdana",
        "Calibri", "Consolas", "Courier New", "Times New Roman",
        "Yu Mincho", "Yu Mincho Light", "MS Mincho", "MS PMincho",
        "游ゴシック", "游明朝", "メイリオ", "HGP創英角ゴシックUB",
    };

    /// <summary>「既定のソフト以外で開く」用に登録した外部アプリの実行ファイルパス（.exe）。
    /// 未設定（空文字）の場合、右クリックメニューの「既定のソフト以外で開く」は
    /// 未設定である旨をステータスに表示するだけで何も起動しない。</summary>
    public string ExternalAppPath { get; set; } = "";

    /// <summary>「既定のソフト以外で開く」の対象に画像ファイルを含めるかどうか。既定はオン。</summary>
    public bool ExternalAppForImage { get; set; } = true;
    /// <summary>「既定のソフト以外で開く」の対象に圧縮ファイル（zip/cbz/cbr/7z/rar）を含めるかどうか。既定はオン。</summary>
    public bool ExternalAppForArchive { get; set; } = true;
    /// <summary>「既定のソフト以外で開く」の対象に動画ファイルを含めるかどうか。既定はオン。</summary>
    public bool ExternalAppForVideo { get; set; } = true;

    /// <summary>設定ファイルの保存先。以前は%LocalAppData%\TanukiTag配下だったが、
    /// USBメモリ等に入れて持ち運ぶポータブル運用をしやすくするため、exe（実行ファイル）と
    /// 同じ場所に"Settings"フォルダを作成してそこに設置するよう変更した。
    /// AppContext.BaseDirectoryはexeが置かれているディレクトリを指す。</summary>
    private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "Settings");

    private static string ConfigPath => Path.Combine(ConfigDir, "tagfiler_config.json");

    /// <summary>直近のLoad/Saveで発生した例外（あれば）。呼び出し側でユーザーに知らせたり
    /// デバッグ出力に残したりするために公開している。silentに握りつぶして原因不明のまま
    /// 「設定が保存されない」という現象だけが表に出るのを避けるため。</summary>
    public static Exception? LastError { get; private set; }

    public static AppConfig Load()
    {
        try
        {
            LastError = null;
            var path = ConfigPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    cfg.Validate();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            // 壊れた設定ファイル等は既定値で上書きする。ただし原因を追えるように記録しておく。
            LastError = ex;
            System.Diagnostics.Debug.WriteLine($"[AppConfig.Load] 設定の読み込みに失敗しました: {ex}");
        }
        return new AppConfig();
    }

    /// <summary>設定ファイルを直接編集していたり、古いバージョンで保存された値が
    /// 現在のスキーマと噛み合わなかったりした場合に備えた検証・補正。
    /// JsonSerializer.Deserializeは型さえ合っていれば範囲外の値やあり得ない文字列でも
    /// そのまま素通ししてしまうため、ここで既知の選択肢・妥当な範囲に収まっているかを
    /// チェックし、外れていれば既定値へフォールバックする。
    /// （XAML側はこれらの値をそのままセルの幅/高さ等のレイアウト計算に使っているため、
    /// 例えば0や負の値・NaNが紛れ込むとWinUI3のネイティブレイアウト/コンポジション層で
    /// 例外にすらならない致命的なクラッシュ（crash.logにも残らない）を起こすことがある。）</summary>
    private void Validate()
    {
        // 以前のスライダー運用時代の値（80〜320の間の任意のpx）が設定ファイルに残っている
        // ケースにも配慮し、3段階のいずれとも一致しない場合は既定の"高"(320px)へフォールバックする。
        if (ThumbSizeOptions.All(o => o.Size != ThumbSize)) ThumbSize = 320;
        if (!double.IsFinite(ThumbGridCellSize) || ThumbGridCellSize < 60 || ThumbGridCellSize > 480)
            ThumbGridCellSize = 160;
        if (!double.IsFinite(TagListFontSize) || TagListFontSize < 6 || TagListFontSize > 48) TagListFontSize = 13;
        if (!double.IsFinite(FileListFontSize) || FileListFontSize < 6 || FileListFontSize > 48) FileListFontSize = 13;
        if (TagChipSizeOptions.All(o => o.Label != TagChipSize)) TagChipSize = "中";
        if (GridSpacingOptions.All(o => o.Label != GridSpacing)) GridSpacing = "普通";
        if (SortKey is not ("accessed" or "name" or "name_desc" or "star" or "added" or "open_count" or "win_mtime" or "size" or "duration"))
            SortKey = "accessed";
        if (OpenClickModeOptions.All(o => o.Key != OpenClickMode)) OpenClickMode = "single";
        if (TagSortKeyOptions.All(o => o.Key != TagSortKey)) TagSortKey = "count_desc";
        if (GroupSortKeyOptions.All(o => o.Key != GroupSortKey)) GroupSortKey = "name_asc";
        if (OpenCountBadgeModeOptions.All(o => o.Key != OpenCountBadgeMode)) OpenCountBadgeMode = "auto";
        if (string.IsNullOrWhiteSpace(Theme)) Theme = "dark";
        if (string.IsNullOrWhiteSpace(UiFont)) UiFont = "Yu Gothic UI";
        if (FontWeightOptions.All(o => o.Weight != UiFontWeight)) UiFontWeight = 400;
        if (string.IsNullOrWhiteSpace(TagFont)) TagFont = "Yu Gothic UI";
        if (FontWeightOptions.All(o => o.Weight != TagFontWeight)) TagFontWeight = 400;
        if (ExternalAppPath == null) ExternalAppPath = "";
    }

    /// <summary>「設定の初期化」ボタン用。グリッド関連の設定（サムネイル解像度・
    /// グリッドのセル表示サイズ・グリッド間隔）だけは意図的に引き継ぎ、それ以外はすべて既定値に戻した上で、
    /// 設定ファイル自体を一度削除してから新規に保存し直す（壊れた設定ファイルの
    /// 復旧手段も兼ねるため、単なる上書きではなく作り直しにしている）。
    /// タグ一覧・タグの色・ファイルのアクセス日/追加日/開いた回数/スター等はすべてDB側
    /// （AppDatabase）に保存されており、この設定ファイルとは無関係のため影響を受けない。</summary>
    public static AppConfig ResetKeepingGridSettings(AppConfig current)
    {
        var fresh = new AppConfig
        {
            ThumbSize = current.ThumbSize,
            ThumbGridCellSize = current.ThumbGridCellSize,
            GridSpacing = current.GridSpacing,
        };
        try
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
        fresh.Save();
        return fresh;
    }

    /// <summary>設定をJSONファイルへ保存する。成功したかどうかを戻り値で返す
    /// （呼び出し側で失敗をユーザーに知らせられるようにするため、以前は戻り値なしで
    /// 失敗を握りつぶしていた）。</summary>
    public bool Save()
    {
        try
        {
            LastError = null;
            var dir = ConfigDir;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // 書き込み中にアプリがクラッシュ/強制終了しても設定ファイルが壊れて
            // 次回起動時に読み込み失敗→既定値に戻る、という事態にならないよう、
            // 一時ファイルに書いてから置き換える（アトミックな更新）。
            var finalPath = ConfigPath;
            var tmpPath = finalPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Copy(tmpPath, finalPath, overwrite: true);
            File.Delete(tmpPath);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex;
            System.Diagnostics.Debug.WriteLine($"[AppConfig.Save] 設定の保存に失敗しました: {ex}");
            return false;
        }
    }
}
