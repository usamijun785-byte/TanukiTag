namespace TanukiTag.Services;

/// <summary>
/// 動画サムネイル生成（シェルサムネイル/ffmpeg）の失敗理由を記録する、ごく単純なファイルロガー。
///
/// 背景: 動画サムネイルが表示されない原因（コーデック未対応でシェルが失敗/ffmpeg自体が
/// 見つからない/ffmpegのデコード失敗/タイムアウト等）は、これまで例外を握りつぶすだけで
/// 呼び出し元からは判別できなかった。原因切り分けができるよう、失敗時にのみ1行ずつ
/// テキストログへ追記する。
///
/// 設計方針:
///   - 通常時（成功時）は一切書き込まない。失敗が起きたときだけログが増える。
///   - Debug.WriteLine と違い、Release ビルドでも出力される（[Conditional("DEBUG")]の対象外）。
///   - ログファイルが際限なく肥大化しないよう、一定サイズを超えたら先頭を切り詰める。
///   - 複数スレッド（動画サムネイルは同時に最大2本並行実行される）から同時に書き込まれても
///     安全なように、ファイルI/Oはlockで直列化する。
/// </summary>
public static class ThumbnailLog
{
    private const long MaxSizeBytes = 2 * 1024 * 1024; // 2MB程度で十分（1行あたり高々数百byte）
    private static readonly object FileLock = new();
    private static string? _logPath;

    /// <summary>アプリ起動時に一度だけ呼び出し、ログファイルの出力先を確定する
    /// （ThumbnailCache/AppDatabaseと同じ %LocalAppData%\TanukiTag フォルダ配下）。</summary>
    public static void Initialize(string appDataDir)
    {
        _logPath = Path.Combine(appDataDir, "thumbnail_errors.log");
    }

    /// <summary>ログファイルのフルパス（設定画面から「ログを開く」用に参照できるよう公開）。
    /// Initialize未呼び出し時はnull。</summary>
    public static string? LogPath => _logPath;

    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var path = _logPath;
        if (path == null) return; // Initialize前は何もしない（テスト・呼び出し順序ミス等の保険）

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (FileLock)
        {
            try
            {
                TrimIfTooLarge(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // ログ出力自体の失敗でアプリ本体の動作に影響を与えないよう、ここは黙殺する。
            }
        }
    }

    /// <summary>ファイルサイズが上限を超えていたら、末尾側（新しい方）の半分だけを残して
    /// 先頭を切り詰める。ローテーションのような複数ファイル管理をせず単一ファイルのまま
    /// 肥大化を防ぐための簡易実装。</summary>
    private static void TrimIfTooLarge(string path)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length <= MaxSizeBytes) return;

        var lines = File.ReadAllLines(path);
        var keepFrom = lines.Length / 2;
        File.WriteAllLines(path, lines[keepFrom..]);
    }
}
