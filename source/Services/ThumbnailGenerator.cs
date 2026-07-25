using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using DrawingColor = System.Drawing.Color;

namespace TanukiTag.Services;

/// <summary>
/// 画像/ZIP内先頭画像をデコードし、size x size の正方形（余白は背景色で合成）に
/// リサイズしてJPEGバイト列を返す。Python版の ThumbnailManager._fit / _zip_thumb と同じ発想。
///
/// デコードには SixLabors.ImageSharp を使用する（System.Drawing/GDI+ はWebPをデコードできず、
/// 環境によってはPNG/GIFも実行環境のGDI+コーデック構成に依存して不安定なため、
/// 管理コードのみで完結し対応形式が明確なImageSharpに統一した）。
///
/// JPEGのみ、FastJpegDecoder.dll（libjpeg-turboのDCTスケールデコードをラップしたネイティブDLL、
/// native/FastJpegDecoder/参照）が exe と同じ場所に配置されていれば、まずそちらでの
/// 縮小デコードを試みる（LoadImageFile/LoadFirstImageFromArchive参照）。フルサイズでデコードしてから
/// 縮小するImageSharpと異なり、デコードの時点で1/2・1/4・1/8に縮小できるため、特に大きいJPEG
/// （デジカメ写真等）のサムネイル生成が高速化する。DLL未配置・デコード失敗時は自動的に
/// ImageSharpの通常デコードにフォールバックする（PNG/WebP等JPEG以外は元々ImageSharpのみ）。
/// </summary>
public static class ThumbnailGenerator
{
    // ImageSharpが標準でデコードできる形式（WebP/GIF/PNG/BMP/TIFF/JPEGを含む）に加え、
    // HeyRed.ImageSharp.Heif（下の静的コンストラクタ参照）によりAVIF/HEICも追加。
    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".avif", ".heic", ".heif"
    };

    /// <summary>ImageSharpは標準でAVIF/HEICをデコードできないため、HeyRed.ImageSharp.Heif
    /// （libheifのネイティブラップ）が提供するAvifConfigurationModule/HeifConfigurationModuleを
    /// Configuration.Defaultに一度だけ登録する。これにより以降のImage.Load(stream)呼び出し
    /// （LoadFirstFrame等、Configurationを明示指定していない箇所すべて）がAVIF/HEICも
    /// 自動的に扱えるようになる。</summary>
    static ThumbnailGenerator()
    {
        new HeyRed.ImageSharp.Heif.Formats.Avif.AvifConfigurationModule()
            .Configure(SixLabors.ImageSharp.Configuration.Default);
        new HeyRed.ImageSharp.Heif.Formats.Heif.HeifConfigurationModule()
            .Configure(SixLabors.ImageSharp.Configuration.Default);
    }

    /// <summary>動画拡張子（Python版 ThumbnailManager.VIDEO_EXTS と同じ一覧）。</summary>
    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
        ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".rmvb"
    };

    /// <summary>アーカイブとして中の先頭画像をサムネイル化する拡張子一覧。
    /// .cbr は本来RARベースのことが多いため、ZIP専用のSystem.IO.Compressionではなく
    /// ZIP/RAR/7z すべてを同じAPIで読めるSharpCompressで統一的に扱う。</summary>
    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz", ".cbr", ".7z", ".rar"
    };

    /// <summary>埋め込みジャケット画像をサムネイル化する音声ファイル拡張子。
    /// MP3(ID3v2 APIC/PICフレーム)とFLAC(METADATA_BLOCK_PICTURE)の埋め込み画像を独自パースする
    /// （TagLib#等の外部ライブラリを増やさず、必要な部分だけを直接読む）。</summary>
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac"
    };

    /// <summary>電子書籍（EPUB）。中身はZIPコンテナのため、OPFを解析して表紙画像を特定して
    /// サムネイル化する（LoadEpubCoverImage参照）。</summary>
    private static readonly HashSet<string> EbookExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub"
    };

    /// <summary>PDF。1ページ目をDocnet.Core(PDFium)でラスタライズしてサムネイル化する
    /// （LoadPdfFirstPage参照）。</summary>
    private static readonly HashSet<string> PdfExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public static bool IsImage(string path) => ImageExts.Contains(System.IO.Path.GetExtension(path));
    public static bool IsAudio(string path) => AudioExts.Contains(System.IO.Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExts.Contains(System.IO.Path.GetExtension(path));
    public static bool IsArchive(string path) => ArchiveExts.Contains(System.IO.Path.GetExtension(path));
    public static bool IsEbook(string path) => EbookExts.Contains(System.IO.Path.GetExtension(path));
    public static bool IsPdf(string path) => PdfExts.Contains(System.IO.Path.GetExtension(path));

    /// <summary>動画サムネイル生成(StorageFile.GetThumbnailAsyncによるシェルサムネイル取得、
    /// および失敗時のffmpegサブプロセス起動)は、画像デコードと違って内部でCOM/Media Foundationの
    /// デコードセッションやシェルのサムネイルキャッシュ機構を経由するため、同時に何本も並行実行すると
    /// （タグ一覧の先読みプリフェッチ＋GridViewの可視セルぶんの読み込みが重なるとすぐに数本〜十数本規模になる）
    /// デコードセッション数の上限超過やシェル側の非スレッドセーフな処理が原因と見られる、
    /// 管理コードの例外にすらならない無言のネイティブクラッシュ(WERのE_UNEXPECTED等)を引き起こすことがある。
    /// 呼び出し元(プリフェッチ/GridViewコンテナ読み込み)を問わず、アプリ全体で同時に動画サムネイルを
    /// 生成する本数をここで一括して制限する。</summary>
    private static readonly SemaphoreSlim VideoThumbnailSemaphore = new(2);

    /// <summary>同期処理。呼び出し側で Task.Run 等によりバックグラウンドスレッドから呼ぶこと。
    /// 画像/アーカイブ(zip/cbz/cbr/7z/rar)/音声埋め込み画像(mp3/flac)のみ対応
    /// （動画は非同期APIを使うため GenerateAsync を使うこと）。
    /// width/height は最終的な出力サイズ（グリッド形状に応じて正方形/縦長/横長）。</summary>
    public static byte[]? Generate(string path, int width, int height, DrawingColor background)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            using Image<Rgba32>? source = ext switch
            {
                _ when ArchiveExts.Contains(ext) => LoadFirstImageFromArchive(path, Math.Max(width, height)),
                _ when ImageExts.Contains(ext) => LoadImageFile(path, Math.Max(width, height)),
                _ when AudioExts.Contains(ext) => LoadEmbeddedAudioArt(path),
                _ when EbookExts.Contains(ext) => LoadEpubCoverImage(path),
                _ when PdfExts.Contains(ext) => LoadPdfFirstPage(path, width, height),
                _ => null
            };

            if (source == null) return null;

            return PadAndEncode(source, width, height, background);
        }
        catch
        {
            return null; // 破損ファイル等は呼び出し側でプレースホルダー表示にフォールバック
        }
    }

    /// <summary>画像/ZIP/動画/その他すべてのファイルに対応する非同期版。動画はWindowsシェルのネイティブ
    /// サムネイル（エクスプローラーが表示するのと同じもの）をまず試し、取得できなければffmpegで
    /// フレームを抜き出してフォールバックする（Python版 _windows_shell_thumb / _video_thumb 相当）。
    /// 画像/動画/アーカイブのいずれでもない一般ファイル（.txt/.pdf/.exeなど）は、同じくシェルAPI経由で
    /// エクスプローラーと同じ「拡張子に紐づいたアイコン」を取得して表示する。</summary>
    public static async Task<byte[]?> GenerateAsync(string path, int width, int height, DrawingColor background, CancellationToken token = default, bool showDuration = false)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (VideoExts.Contains(ext))
        {
            byte[]? videoBytes;
            await VideoThumbnailSemaphore.WaitAsync(token);
            try { videoBytes = await GenerateVideoThumbnail(path, width, height, background, token, showDuration); }
            catch (OperationCanceledException) { return null; }
            catch { videoBytes = null; }
            finally { VideoThumbnailSemaphore.Release(); }

            if (videoBytes != null || token.IsCancellationRequested) return videoBytes;
            // シェルサムネイル・ffmpegの両方が失敗した動画（コーデック未対応、破損、極端に短い/壊れた
            // ファイル等）は、以前はサムネ枠が空白のままになっていた。他の非対応ファイルと同様に、
            // Windows既定の拡張子アイコン（エクスプローラーで見えるのと同じもの）へフォールバックする。
            return await GenerateOtherFileIcon(path, width, height, background, token);
        }
        if (ImageExts.Contains(ext) || ArchiveExts.Contains(ext) || AudioExts.Contains(ext)
            || EbookExts.Contains(ext) || PdfExts.Contains(ext))
        {
            var bytes = await Task.Run(() => Generate(path, width, height, background), token);
            if (bytes != null || token.IsCancellationRequested) return bytes;
            // アーカイブ(zip/cbz/cbr/7z/rar)内に画像が1枚も無い、あるいはmp3/flacに埋め込み
            // ジャケット画像が無い場合はGenerateがnullを返す。以前はこのままプレースホルダーすら
            // 出ずサムネ枠が空白になっていたため、他の一般ファイルと同様にWindows既定の
            // 拡張子アイコン（シェルアイコン）へフォールバックする。
            // EPUB(表紙が特定できない/画像が1枚も無い)・PDF(暗号化・破損等で描画に失敗)も同様。
            if (ArchiveExts.Contains(ext) || AudioExts.Contains(ext)
                || EbookExts.Contains(ext) || PdfExts.Contains(ext))
                return await GenerateOtherFileIcon(path, width, height, background, token);
            return null;
        }

        return await GenerateOtherFileIcon(path, width, height, background, token);
    }

    /// <summary>画像・動画・アーカイブのいずれでもない一般ファイル用。StorageFile.GetThumbnailAsync は
    /// 専用のサムネイルハンドラーを持たないファイルに対しては自動的に「関連付けアイコン」
    /// （エクスプローラーの一覧表示で見えるのと同じアイコン）にフォールバックしてくれるため、
    /// 動画用に既に用意している TryGetShellThumbnail をそのまま流用できる。
    /// ただし .lnk ショートカットはこのWinRT経路だと取得に失敗する（またはショートカット矢印の
    /// 付いていない不完全なアイコンになる）ことがあるため、失敗時は Shell32 の SHGetFileInfo による
    /// 直接取得（GetIconViaShGetFileInfo）にもフォールバックする。</summary>
    private static async Task<byte[]?> GenerateOtherFileIcon(string path, int width, int height, DrawingColor background, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested) return null;
            var decodeSize = Math.Max(width, height);
            // GetIconViaShGetFileInfoは内部でシェルのCOMコンポーネントを使うため、
            // TryGetShellThumbnailと同じくShellThumbnailWorkerの専用STAスレッド上でだけ
            // 実行する（以前はTask.Run＝任意のスレッドプールのスレッドから呼んでおり、
            // 特にショートカット(.lnk)のリンク先解決でスレッドをまたいだCOM呼び出しが発生し、
            // try/catchでは捕まえられないネイティブクラッシュ(STATUS_STOWED_EXCEPTION)の
            // 原因になっていた）。
            using var icon = await TryGetShellThumbnail(path, decodeSize, token)
                ?? await ShellThumbnailWorker.Run(() => token.IsCancellationRequested ? null : GetIconViaShGetFileInfo(path));
            if (icon == null || token.IsCancellationRequested) return null;
            return PadAndEncode(icon, width, height, background);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>StorageFile.GetThumbnailAsync（WinRT）がショートカット(.lnk)等で失敗する場合の
    /// 保険として、Win32のSHGetFileInfoで直接HICONを取得する。エクスプローラーが内部で使うのと
    /// 同じAPIのため、ショートカット矢印オーバーレイ込みの正しいアイコンを確実に取得できる。
    /// 同期APIだが軽量なので、既に呼び出し元がバックグラウンドスレッド上にいる前提で同期実行する。</summary>
    private static Image<Rgba32>? GetIconViaShGetFileInfo(string path)
    {
        var shinfo = new SHFILEINFO();
        var result = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero) return null;
        try
        {
            using var sysIcon = System.Drawing.Icon.FromHandle(shinfo.hIcon);
            using var bitmap = sysIcon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return Image.Load<Rgba32>(ms);
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static byte[] PadAndEncode(Image<Rgba32> source, int width, int height, DrawingColor background)
    {
        var bg = new Rgba32(background.R, background.G, background.B, 255);
        source.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Pad,           // アスペクト比を保って縮小し、余白は背景色でパディング
            PadColor = bg,
            Sampler = KnownResamplers.Lanczos3,
        }));
        // 拡張子アイコン(シェルアイコン)等はアイコン内部にも透過領域を持つことが多い。
        // JPEGにはアルファチャンネルが無いため、BackgroundColorで合成せずにSaveAsJpegすると
        // 透過ピクセルのRGB値（多くは0,0,0=黒）がそのまま使われ、アイコンの背景だけ
        // テーマに関係なく黒くなってしまう。ここで明示的にbg色へ合成してから保存する。
        source.Mutate(x => x.BackgroundColor(bg));

        using var ms = new MemoryStream();
        source.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
        return ms.ToArray();
    }

    /// <summary>動画サムネイル生成。①Windowsシェルのネイティブサムネイル（コーデックさえ入っていれば
    /// 大抵の動画で高速・高品質に取れる）→ ②ffmpegでフレーム抽出、の順にフォールバックする。</summary>
    private static async Task<byte[]?> GenerateVideoThumbnail(string path, int width, int height, DrawingColor background, CancellationToken token, bool showDuration = false)
    {
        var decodeSize = Math.Max(width, height);
        string? shellFailReason = null;
        var shellFrame = await TryGetShellThumbnail(path, decodeSize, token, reason => shellFailReason = reason, rejectIconFallback: true);

        Image<Rgba32>? frame = shellFrame;
        if (frame == null && !token.IsCancellationRequested)
        {
            frame = TryGetFfmpegFrame(path, decodeSize);
            // ffmpeg側の詳細な失敗理由はTryGetFfmpegFrame内で既にログ済みなので、ここではシェル側の
            // 理由も合わせた「両方失敗」の要約だけを1行残す（原因切り分けの起点になる）。
            if (frame == null && !token.IsCancellationRequested)
                ThumbnailLog.Error($"動画サムネイル取得に失敗（シェル・ffmpegとも）: {path} | シェル: {shellFailReason ?? "不明"}");
        }
        using var frameDisposable = frame;

        if (frame == null || token.IsCancellationRequested) return null;

        // 再生時間の取得はシェルサムネイル取得と独立しているため、フレーム取得の成否に関わらず試す。
        // 失敗（プロパティ未対応のコーデック等）してもサムネイル自体は表示できるよう、
        // 取得できなかった場合は単にバッジを描かないだけにする。
        TimeSpan? duration = null;
        if (showDuration && !token.IsCancellationRequested)
            duration = await GetVideoDurationAsync(path, token);

        var bg = new Rgba32(background.R, background.G, background.B, 255);
        frame.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Pad,
            PadColor = bg,
            Sampler = KnownResamplers.Lanczos3,
        }));
        // 画像側と同様、フレーム/シェルサムネイル内部の透過ピクセルが黒くならないよう合成しておく
        frame.Mutate(x => x.BackgroundColor(bg));
        DrawPlayBadge(frame, Math.Min(width, height));
        if (duration is { } d) DrawDurationBadge(frame, Math.Min(width, height), FormatDuration(d));

        using var ms = new MemoryStream();
        frame.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
        return ms.ToArray();
    }

    /// <summary>StorageFile.Properties.GetVideoPropertiesAsyncで動画の再生時間を取得する。
    /// COMオブジェクトの取り扱いに関するThumbnailWorkerの注意書き（後述）を踏襲し、
    /// 念のため同じ専用スレッド上で取得する。</summary>
    private static async Task<TimeSpan?> GetVideoDurationAsync(string path, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        try
        {
            // ラムダをそのままRun<TimeSpan>に渡すと、"return null;"と"return TimeSpanの式;"が
            // 混在することで戻り値の型がFunc<TimeSpan?>ではなくFunc<TimeSpan>と推論されてしまい、
            // ビルドエラーになる。ローカル関数として明示的にTimeSpan?型で宣言することで回避する。
            TimeSpan? GetDuration()
            {
                if (token.IsCancellationRequested) return null;
                var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                var props = file.Properties.GetVideoPropertiesAsync().AsTask().GetAwaiter().GetResult();
                return props.Duration > TimeSpan.Zero ? props.Duration : (TimeSpan?)null;
            }
            return await ShellThumbnailWorker.Run<TimeSpan?>(GetDuration);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>1:23:45 / 3:07 のように、1時間未満は m:ss、1時間以上は h:mm:ss 形式にする
    /// （エクスプローラーや一般的な動画プレイヤーの表示と同じ書式）。</summary>
    private static string FormatDuration(TimeSpan d)
    {
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";
    }

    /// <summary>
    /// StorageFile / GetThumbnailAsync まわりのWinRT COMオブジェクトを、常に同一の
    /// 専用スレッドだけで取得・使用・破棄するための実行キュー。
    ///
    /// 背景: これまでの実装では `await file.GetThumbnailAsync(...)` のように await するたびに、
    /// 継続処理(using による破棄も含む)がどのスレッドプールのスレッドで再開されるか保証が無かった。
    /// WinRTのCOMオブジェクトは生成時のスレッド(アパートメント)以外から操作・破棄すると
    /// ネイティブ層で致命的なエラーになることがあり、.NET側の try/catch はおろか
    /// AppDomain.UnhandledException 等でも一切捕まえられずプロセスごと落ちる
    /// (終了コード 0xc000027b = STATUS_STOWED_EXCEPTION が典型)。
    /// 生成・破棄を必ず1本の専用スレッドの中だけで完結させることで、この種のスレッドまたぎ
    /// COM違反を構造的に起こりえなくする。
    /// </summary>
    private static class ShellThumbnailWorker
    {
        private static readonly BlockingCollection<Action> Queue = new();

        static ShellThumbnailWorker()
        {
            var thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "ShellThumbnailWorker",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static void RunLoop()
        {
            foreach (var action in Queue.GetConsumingEnumerable())
            {
                action();
            }
        }

        /// <summary>専用スレッド上で func を実行し、結果を返す。func の中で WinRT の
        /// StorageFile/Thumbnail オブジェクトの取得・使用・破棄をすべて完結させること。</summary>
        public static Task<T?> Run<T>(Func<T?> func)
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }

    private static async Task<Image<Rgba32>?> TryGetShellThumbnail(string path, int size, CancellationToken token, Action<string>? onFail = null, bool rejectIconFallback = false)
    {
        if (token.IsCancellationRequested) return null; // 実行キューに積む前にまず確認

        (byte[] Bytes, bool IsIcon)? result;
        try
        {
            result = await ShellThumbnailWorker.Run<(byte[], bool)?>(() =>
            {
                if (token.IsCancellationRequested) return null; // 順番が回ってきた時点で改めて確認

                // ここから破棄まで、すべて ShellThumbnailWorker の専用スレッド上だけで完結させる。
                // フォルダの場合はStorageFile ではなく StorageFolder 経由で取得する
                // （こちらもGetThumbnailAsyncを持ち、ThumbnailMode.SingleItemで
                // エクスプローラーの一覧表示と同じフォルダアイコンが得られる）。
                var reqSize = (uint)Math.Min(size * 2, 256);
                StorageItemThumbnail? thumb;
                if (Directory.Exists(path))
                {
                    var folder = StorageFolder.GetFolderFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                    thumb = folder.GetThumbnailAsync(ThumbnailMode.SingleItem, reqSize, ThumbnailOptions.UseCurrentScale)
                        .AsTask().GetAwaiter().GetResult();
                }
                else
                {
                    var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                    thumb = file.GetThumbnailAsync(ThumbnailMode.SingleItem, reqSize, ThumbnailOptions.UseCurrentScale)
                        .AsTask().GetAwaiter().GetResult();
                }
                using var thumbDisposable = thumb;
                if (thumb == null || thumb.Size == 0) return null;

                // 動画用のサムネイルハンドラー/コーデックが無い場合、GetThumbnailAsyncは例外や
                // サイズ0を返すのではなく、「既定のアプリアイコン」を画像として"成功"扱いで返す
                // ことがある（エクスプローラーの一覧でアイコン表示になっているファイルと同じ状態）。
                // Type==Icon の場合は実際のフレームではないため、呼び出し元でffmpegへフォール
                // バックさせられるよう、判定結果もあわせて返す。
                var isIcon = thumb.Type == ThumbnailType.Icon;

                using var stream = thumb.AsStream();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return (ms.ToArray(), isIcon);
            });
        }
        catch (Exception ex)
        {
            onFail?.Invoke($"シェルサムネイル取得中に例外: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (result == null)
        {
            if (!token.IsCancellationRequested)
                onFail?.Invoke("シェルはサムネイルを返しませんでした（専用のサムネイルハンドラー/コーデックが無い可能性）");
            return null;
        }
        if (token.IsCancellationRequested) return null;

        var (bytes, isIconResult) = result.Value;
        if (isIconResult && rejectIconFallback)
        {
            onFail?.Invoke("シェルは実サムネイルではなく既定のアプリアイコンを返しました（対応コーデック/サムネイルハンドラーが無い可能性）");
            return null;
        }
        if (token.IsCancellationRequested) return null;
        try
        {
            return Image.Load<Rgba32>(bytes);
        }
        catch
        {
            // サムネイルハンドラーを持たないファイル（ショートカット .lnk・実行ファイル .exe など）は
            // GetThumbnailAsyncが実サムネイルではなく「アイコンそのもの」(.ico形式)を返すことがある。
            // ImageSharpは.icoデコーダーを持たないためここで例外になり、結果としてこれらのファイルだけ
            // 実際のアイコン（ショートカット矢印付きなど）が表示されず、汎用グレーアイコンに
            // フォールバックしてしまっていた。System.Drawing.Icon経由でデコードして救済する。
            return TryDecodeIcoFallback(bytes);
        }
    }

    /// <summary>System.Drawing.Icon で .ico 形式のバイト列をデコードし、ImageSharpのImageへ変換する。
    /// TryGetShellThumbnailがImageSharpで直接デコードできなかった場合のフォールバック専用。</summary>
    private static Image<Rgba32>? TryDecodeIcoFallback(byte[] bytes)
    {
        try
        {
            using var iconStream = new MemoryStream(bytes);
            using var icon = new System.Drawing.Icon(iconStream);
            using var bitmap = icon.ToBitmap();
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            pngStream.Position = 0;
            return Image.Load<Rgba32>(pngStream);
        }
        catch
        {
            return null;
        }
    }

    // ffmpegパスキャッシュ（exeフォルダ→PATHの順で一度だけ探す。Python版 _find_ffmpeg 相当）
    private static string? _ffmpegPath;
    private static bool _ffmpegChecked;

    private static string? FindFfmpeg()
    {
        if (_ffmpegChecked) return _ffmpegPath;
        _ffmpegChecked = true;
        try
        {
            var exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]);
            var local = exeDir != null ? System.IO.Path.Combine(exeDir, "ffmpeg.exe") : null;
            if (local != null && File.Exists(local))
            {
                _ffmpegPath = local;
                return _ffmpegPath;
            }

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
            {
                var candidate = System.IO.Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    _ffmpegPath = candidate;
                    return _ffmpegPath;
                }
            }
        }
        catch (Exception ex)
        {
            ThumbnailLog.Warn($"ffmpeg.exeの探索中に例外が発生しました: {ex.Message}");
        }
        // exeフォルダにもPATHにも見つからなかった場合。この探索結果はプロセス起動中ずっと
        // キャッシュされる（_ffmpegChecked）ため、毎回ではなく起動後最初の1回だけ記録する。
        if (_ffmpegPath == null)
            ThumbnailLog.Warn("ffmpeg.exeが見つかりません（exeと同じフォルダ、およびPATH環境変数を探索しましたが見つかりませんでした）。動画サムネイルはシェルサムネイルのみに依存します。");
        return _ffmpegPath;
    }

    /// <summary>ffmpegで動画から1フレーム抜き出す。まず5秒地点、失敗したら先頭フレームを試す
    /// （Python版 _video_thumb と同じ2段構え）。失敗した場合は原因（タイムアウト/デコード失敗等）を
    /// ログに残す。</summary>
    private static Image<Rgba32>? TryGetFfmpegFrame(string path, int size)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null; // ffmpeg.exe自体が見つからない旨はFindFfmpeg内で既にログ済み

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tfnet_{Guid.NewGuid():N}.jpg");
        try
        {
            var vf = $"scale={size}:{size}:force_original_aspect_ratio=decrease";
            var attempt1 = RunFfmpeg(ffmpeg, new[] { "-y", "-ss", "00:00:05", "-i", path, "-vframes", "1", "-vf", vf, "-q:v", "3", tmp });
            if (!attempt1.Success || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                var attempt2 = RunFfmpeg(ffmpeg, new[] { "-y", "-i", path, "-vframes", "1", "-vf", vf, "-q:v", "3", tmp });
                if (!attempt2.Success || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                {
                    ThumbnailLog.Error(
                        $"ffmpegでのフレーム抽出に失敗しました: {path}" +
                        $" | 5秒地点: {DescribeFfmpegResult(attempt1)}" +
                        $" | 先頭フレーム: {DescribeFfmpegResult(attempt2)}");
                    return null;
                }
            }

            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            return Image.Load<Rgba32>(ms);
        }
        catch (Exception ex)
        {
            ThumbnailLog.Error($"ffmpegフレームのデコード中に例外が発生しました: {path} | {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 無視 */ }
        }
    }

    /// <summary>RunFfmpegの失敗理由をログ用の短い文字列にする。</summary>
    private static string DescribeFfmpegResult(FfmpegRunResult r)
    {
        if (r.Success) return "成功";
        if (r.TimedOut) return "タイムアウト(20秒)";
        if (r.LaunchFailed) return "ffmpegプロセスの起動に失敗";
        var stderrTail = string.IsNullOrWhiteSpace(r.StdErr) ? "(stderrなし)" : SummarizeStdErr(r.StdErr);
        return $"exitCode={r.ExitCode} {stderrTail}";
    }

    /// <summary>ffmpegのstderrは冗長なため、末尾数行（実際のエラー原因が出やすい部分）だけを残す。</summary>
    private static string SummarizeStdErr(string stdErr)
    {
        var lines = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Length > 3 ? lines[^3..] : lines;
        return string.Join(" / ", tail);
    }

    private readonly record struct FfmpegRunResult(bool Success, int ExitCode, bool TimedOut, bool LaunchFailed, string StdErr);

    private static FfmpegRunResult RunFfmpeg(string ffmpegPath, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return new FfmpegRunResult(false, -1, false, true, "");

            // 標準エラーはffmpegがログ出力に使う。バッファが埋まってプロセスがブロックしないよう、
            // WaitForExitの前に非同期で読み始めておく。
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(); } catch { /* 無視 */ }
                return new FfmpegRunResult(false, -1, true, false, "");
            }

            string stdErr;
            try { stdErr = stdErrTask.GetAwaiter().GetResult(); }
            catch { stdErr = ""; }

            return new FfmpegRunResult(proc.ExitCode == 0, proc.ExitCode, false, false, stdErr);
        }
        catch (Exception ex)
        {
            return new FfmpegRunResult(false, -1, false, true, ex.Message);
        }
    }

    /// <summary>動画バッジ（右上に赤地の▶三角）を描画する（Python版 _video_thumb の再生アイコン相当）。
    /// SixLabors.ImageSharp.Drawing（パス/文字描画）を追加参照せずに済むよう、矩形塗り（コア機能のみ）で
    /// 三角形を1行ずつ幅を変えながら塗って近似する。</summary>
    private static void DrawPlayBadge(Image<Rgba32> img, int size)
    {
        var badge = Math.Max(18, size / 8);
        var red = new Rgba32(0xC0, 0x30, 0x00, 220);
        var white = new Rgba32(255, 255, 255, 255);

        var badgeW = Math.Min(badge, img.Width - 2);
        var badgeH = Math.Min((int)(badge * 0.72), img.Height - 2);
        if (badgeW <= 0 || badgeH <= 0) return;

        // 右上を基準にバッジ矩形を配置（左端 = 画像幅 - バッジ幅 - 余白2px）
        var rectX = img.Width - badgeW - 2;
        FillRect(img, rectX, 2, badgeW, badgeH, red);

        var triH = badgeH - 6;
        var triW = (int)(badge * 0.5);
        var top = 5;
        // バッジ矩形内で三角の左端を求める（元は左寄せの left=6 だった分をそのまま右側の矩形基準に平行移動）
        var left = rectX + 6;
        for (var row = 0; row < triH; row++)
        {
            var distFromCenter = Math.Abs(row - triH / 2.0);
            var width = (int)((1 - distFromCenter / (triH / 2.0)) * triW);
            if (width <= 0) continue;
            FillRect(img, left, top + row, width, 1, white);
        }
    }

    /// <summary>3列×5行のビットマップフォント（0-9とコロンのみ）。DrawPlayBadgeと同じ方針で、
    /// SixLabors.ImageSharp.Drawing（フォント描画）を追加参照せずに済むよう、コアAPIの
    /// 矩形塗りだけで簡易的な数字を表現する。1=点灯 0=消灯。</summary>
    private static readonly Dictionary<char, string[]> DurationFontGlyphs = new()
    {
        ['0'] = new[] { "111", "101", "101", "101", "111" },
        ['1'] = new[] { "010", "110", "010", "010", "111" },
        ['2'] = new[] { "111", "001", "111", "100", "111" },
        ['3'] = new[] { "111", "001", "111", "001", "111" },
        ['4'] = new[] { "101", "101", "111", "001", "001" },
        ['5'] = new[] { "111", "100", "111", "001", "111" },
        ['6'] = new[] { "111", "100", "111", "101", "111" },
        ['7'] = new[] { "111", "001", "010", "010", "010" },
        ['8'] = new[] { "111", "101", "111", "101", "111" },
        ['9'] = new[] { "111", "101", "111", "001", "111" },
        [':'] = new[] { "000", "010", "000", "010", "000" },
    };

    /// <summary>動画の再生時間（"3:07" / "1:23:45" 等）を、サムネイル左下に半透明の背景つきで描画する
    /// （DrawPlayBadgeの右上再生アイコンと重ならない位置）。</summary>
    private static void DrawDurationBadge(Image<Rgba32> img, int size, string text)
    {
        // サムネイルが小さいほどピクセルサイズも小さくして、極端にはみ出さないようにする。
        // 以前は size/100（低解像度サムネでは1pxしかなく文字が潰れて読めなかった）だったのを、
        // 見やすさ優先で size/40・最小2pxに引き上げていたが、それでも大きすぎるとの要望により
        // size/60・最小2pxへ縮小した（低解像度120pxで2px、高解像度320pxで5px相当になる）。
        var pixel = Math.Max(2, size / 60);
        const int glyphCols = 3, glyphRows = 5;
        var glyphW = glyphCols * pixel;
        var glyphH = glyphRows * pixel;
        var spacing = pixel; // 文字間の隙間
        var padding = pixel * 2; // 背景の余白

        var textW = text.Length * glyphW + Math.Max(0, text.Length - 1) * spacing;
        var boxW = Math.Min(textW + padding * 2, img.Width - 2);
        var boxH = Math.Min(glyphH + padding * 2, img.Height - 2);
        if (boxW <= 0 || boxH <= 0) return;

        // 左下を基準に配置（右上のDrawPlayBadgeと対角に置き、視認性を確保する）。
        var boxX = 2;
        var boxY = img.Height - boxH - 2;
        FillRect(img, boxX, boxY, boxW, boxH, new Rgba32(0, 0, 0, 170));

        var white = new Rgba32(255, 255, 255, 255);
        var penX = boxX + padding;
        var penY = boxY + padding;
        foreach (var ch in text)
        {
            if (DurationFontGlyphs.TryGetValue(ch, out var glyph))
            {
                for (var row = 0; row < glyph.Length; row++)
                {
                    var line = glyph[row];
                    for (var col = 0; col < line.Length; col++)
                    {
                        if (line[col] != '1') continue;
                        FillRect(img, penX + col * pixel, penY + row * pixel, pixel, pixel, white);
                    }
                }
            }
            penX += glyphW + spacing;
        }
    }

    /// <summary>矩形を単色で塗る（SixLabors.ImageSharp.Drawingの拡張パッケージを追加せずに済むよう、
    /// コアAPIのピクセルインデクサーで直接書き込む）。範囲外は自動でクリップする。</summary>
    private static void FillRect(Image<Rgba32> img, int x, int y, int width, int height, Rgba32 color)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(img.Width, x + width);
        var y1 = Math.Min(img.Height, y + height);
        if (x0 >= x1 || y0 >= y1) return;

        img.ProcessPixelRows(accessor =>
        {
            for (var py = y0; py < y1; py++)
            {
                var row = accessor.GetRowSpan(py);
                for (var px = x0; px < x1; px++)
                    row[px] = color;
            }
        });
    }

    /// <summary>EPUBの表紙画像を読み込む。EPUBは実体がZIPコンテナのため、まずMETA-INF/container.xmlから
    /// OPF(パッケージ定義)ファイルの場所を特定し、OPF内の &lt;meta name="cover"&gt;（EPUB2）または
    /// properties="cover-image"（EPUB3）で示された表紙画像エントリを取得する。
    /// どちらの方式でも表紙が特定できなかった場合（規格外のEPUBや壊れたOPF等）は、
    /// 他のアーカイブ形式と同様にファイル名昇順で最初の画像にフォールバックする。</summary>
    private static Image<Rgba32>? LoadEpubCoverImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = SharpCompress.Archives.Zip.ZipArchive.Open(fs);

        string? ReadEntryText(string entryName)
        {
            var normalized = entryName.Replace('\\', '/').TrimStart('/');
            var e = archive.Entries.FirstOrDefault(x =>
                !x.IsDirectory &&
                (x.Key?.Replace('\\', '/').TrimStart('/') ?? "").Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            using var s = e.OpenEntryStream();
            using var sr = new StreamReader(s, System.Text.Encoding.UTF8);
            return sr.ReadToEnd();
        }

        string? opfPath = null;
        var containerXml = ReadEntryText("META-INF/container.xml");
        if (containerXml != null)
        {
            var containerMatch = Regex.Match(containerXml, "full-path=\"([^\"]+)\"");
            if (containerMatch.Success) opfPath = containerMatch.Groups[1].Value;
        }

        string? coverHref = null;
        if (opfPath != null)
        {
            var opfText = ReadEntryText(opfPath);
            if (opfText != null)
            {
                var opfDir = System.IO.Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";

                // EPUB2方式: <meta name="cover" content="表紙のitem id"/> から該当<item>のhrefを引く
                var metaMatch = Regex.Match(opfText, "<meta[^>]*name=\"cover\"[^>]*content=\"([^\"]+)\"");
                if (!metaMatch.Success)
                    metaMatch = Regex.Match(opfText, "<meta[^>]*content=\"([^\"]+)\"[^>]*name=\"cover\"");
                if (metaMatch.Success)
                {
                    var coverId = Regex.Escape(metaMatch.Groups[1].Value);
                    var itemMatch = Regex.Match(opfText, $"<item[^>]*id=\"{coverId}\"[^>]*href=\"([^\"]+)\"");
                    if (!itemMatch.Success)
                        itemMatch = Regex.Match(opfText, $"<item[^>]*href=\"([^\"]+)\"[^>]*id=\"{coverId}\"");
                    if (itemMatch.Success) coverHref = itemMatch.Groups[1].Value;
                }

                // EPUB3方式: <item properties="cover-image" href="..."/>
                if (coverHref == null)
                {
                    var propMatch = Regex.Match(opfText, "<item[^>]*properties=\"[^\"]*cover-image[^\"]*\"[^>]*href=\"([^\"]+)\"");
                    if (!propMatch.Success)
                        propMatch = Regex.Match(opfText, "<item[^>]*href=\"([^\"]+)\"[^>]*properties=\"[^\"]*cover-image[^\"]*\"");
                    if (propMatch.Success) coverHref = propMatch.Groups[1].Value;
                }

                if (coverHref != null)
                {
                    coverHref = System.Net.WebUtility.UrlDecode(coverHref);
                    coverHref = string.IsNullOrEmpty(opfDir) ? coverHref : $"{opfDir}/{coverHref}";
                }
            }
        }

        if (coverHref != null)
        {
            var normalizedTarget = coverHref.Replace('\\', '/').TrimStart('/');
            var coverEntry = archive.Entries.FirstOrDefault(e =>
                !e.IsDirectory &&
                (e.Key?.Replace('\\', '/').TrimStart('/') ?? "").Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase));
            if (coverEntry != null)
            {
                using var entryStream = coverEntry.OpenEntryStream();
                using var ms = new MemoryStream();
                entryStream.CopyTo(ms);
                ms.Position = 0;
                return LoadFirstFrame(ms);
            }
        }

        // OPFの解析や表紙特定に失敗した場合のフォールバック（表紙ではない可能性はあるが、
        // 何も表示できないよりは良い。他のアーカイブ形式と同じ基準で選ぶ）。
        var fallbackEntry = archive.Entries
            .Where(e => !e.IsDirectory)
            .Where(e => ImageExts.Contains(System.IO.Path.GetExtension(e.Key ?? "")))
            .Where(e => !System.IO.Path.GetFileName(e.Key ?? "").StartsWith('.'))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fallbackEntry == null) return null;

        using var fallbackStream = fallbackEntry.OpenEntryStream();
        using var fallbackMs = new MemoryStream();
        fallbackStream.CopyTo(fallbackMs);
        fallbackMs.Position = 0;
        return LoadFirstFrame(fallbackMs);
    }

    /// <summary>PDFの1ページ目をDocnet.Core（PDFiumのネイティブラッパー）でラスタライズする。
    /// 最終的な出力サイズよりやや大きめの解像度で描画してからPadAndEncode側でLanczos縮小させることで、
    /// 文字が多いページでも視認性を保つ。パスワード保護・破損PDF等はDocnet.Core側で例外になるため、
    /// 呼び出し元（Generate）のtry-catchでシェルアイコン表示へフォールバックする。</summary>
    private static Image<Rgba32>? LoadPdfFirstPage(string path, int width, int height)
    {
        var renderSize = Math.Max(Math.Max(width, height) * 2, 512);
        using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(renderSize, renderSize));
        if (docReader.GetPageCount() < 1) return null;

        using var pageReader = docReader.GetPageReader(0);
        var rawBgra = pageReader.GetImage(); // BGRA32、上から下・左から右の並び
        var pageWidth = pageReader.GetPageWidth();
        var pageHeight = pageReader.GetPageHeight();
        if (pageWidth <= 0 || pageHeight <= 0 || rawBgra.Length < pageWidth * pageHeight * 4) return null;

        using var bgraImage = Image.LoadPixelData<Bgra32>(rawBgra, pageWidth, pageHeight);
        return bgraImage.CloneAs<Rgba32>();
    }

    /// <summary>拡張子だけで判定する簡易JPEG判定（.jpg/.jpeg）。ネイティブ高速デコードを
    /// 試すかどうかの分岐にのみ使う（実データの中身までは見ない。中身がJPEGでなかった場合は
    /// FastJpegDecoder.TryDecodeScaled側が失敗を返すので、通常のImageSharpデコードにフォールバックする）。</summary>
    private static bool IsJpegExt(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>JPEGバイト列に対してFastJpegDecoder（libjpeg-turboのDCTスケールデコード）を試み、
    /// 成功すればRGB24生データからImage&lt;Rgba32&gt;を組み立てて返す。DLL未配置・デコード失敗時はnull。</summary>
    private static Image<Rgba32>? TryLoadJpegFast(byte[] jpegBytes, int targetSize)
    {
        var decoded = FastJpegDecoder.TryDecodeScaled(jpegBytes, targetSize);
        if (decoded == null) return null;
        var (rgb, w, h) = decoded.Value;
        using var rgbImage = Image.LoadPixelData<Rgb24>(rgb, w, h);
        return rgbImage.CloneAs<Rgba32>();
    }

    private static Image<Rgba32> LoadImageFile(string path, int targetSize)
    {
        if (IsJpegExt(path))
        {
            var bytes = File.ReadAllBytes(path);
            var fast = TryLoadJpegFast(bytes, targetSize);
            if (fast != null) return fast;
            using var msFallback = new MemoryStream(bytes);
            return LoadFirstFrame(msFallback);
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return LoadFirstFrame(fs);
    }

    /// <summary>アーカイブ（zip/cbz/cbr/7z/rar）内の先頭画像（ファイル名昇順）を読み込む。
    /// SharpCompressのArchiveFactory.Openは拡張子ではなく実データのシグネチャで形式を自動判別するため、
    /// 拡張子がcbrでも中身がZIPだった、といったケースにも対応できる。</summary>
    private static Image<Rgba32>? LoadFirstImageFromArchive(string path, int targetSize)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // ArchiveFactory.Openは呼ぶたびにファイル先頭のシグネチャを見てZIP/RAR/7z…と順に
        // 自動判別する。.cbrは中身が実はZIPということがあるためこの自動判別が必要だが、
        // .zip/.cbzは拡張子を信頼してよいので、その分の判別コストを毎回払わずに済むよう
        // SharpCompressのZipArchive.Openを直接呼ぶ（結果は同じ、判別処理だけを省略する）。
        var ext = System.IO.Path.GetExtension(path);
        using var archive = ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase)
            ? (SharpCompress.Archives.IArchive)SharpCompress.Archives.Zip.ZipArchive.Open(fs)
            : SharpCompress.Archives.ArchiveFactory.Open(fs);

        // 目的の1件（ファイル名昇順で最初の画像）を選ぶためだけに全エントリを毎回ソートする
        // OrderBy().FirstOrDefault()（O(n log n)）は無駄なので、1回のループで最小キーを
        // 追跡するだけのO(n)走査にする。実際に画像データを読み込むのは、最後に選ばれた
        // 1件のOpenEntryStream()だけで、他のエントリの中身は最初から読んでいない。
        SharpCompress.Archives.IArchiveEntry? entry = null;
        foreach (var e in archive.Entries)
        {
            if (e.IsDirectory) continue;
            var key = e.Key ?? "";
            if (!ImageExts.Contains(System.IO.Path.GetExtension(key))) continue;
            if (System.IO.Path.GetFileName(key).StartsWith('.')) continue;
            if (entry == null || StringComparer.OrdinalIgnoreCase.Compare(key, entry.Key ?? "") < 0)
                entry = e;
        }
        if (entry == null) return null;

        using var entryStream = entry.OpenEntryStream();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        var bytes = ms.ToArray();

        if (IsJpegExt(entry.Key ?? ""))
        {
            var fast = TryLoadJpegFast(bytes, targetSize);
            if (fast != null) return fast;
        }

        using var msFallback = new MemoryStream(bytes);
        return LoadFirstFrame(msFallback);
    }

    /// <summary>アニメGIF等の複数フレーム画像は、サムネイル用に1枚目のフレームだけを取り出す
    /// （全フレーム保持したままリサイズ/エンコードするのは無駄が大きいため）。</summary>
    private static Image<Rgba32> LoadFirstFrame(Stream stream)
    {
        var image = Image.Load<Rgba32>(stream);
        if (image.Frames.Count <= 1) return image;

        var firstFrame = image.Frames.CloneFrame(0);
        image.Dispose();
        return firstFrame;
    }

    /// <summary>MP3(ID3v2)/FLACの埋め込みジャケット画像（アルバムアート）を読み込む。
    /// 埋め込みが無いファイル（タグ自体が無い、あるいはAPIC/PICTUREフレームが無い）ではnullを返す
    /// （呼び出し元でWindows既定の音楽ファイルアイコンへフォールバックする）。</summary>
    private static Image<Rgba32>? LoadEmbeddedAudioArt(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = ext == ".flac" ? ExtractFlacPicture(fs) : ExtractId3v2Picture(fs);
        if (bytes == null) return null;
        using var ms = new MemoryStream(bytes);
        return LoadFirstFrame(ms);
    }

    /// <summary>MP3のID3v2タグからAPIC（ID3v2.3/2.4）またはPIC（ID3v2.2）フレームの
    /// 画像データ本体を取り出す。ID3v2ヘッダーが無い（タグ自体が無い）場合はnullを返す。
    /// FLACの実装同様、必要な部分だけを直接バイナリ解析する（外部ライブラリを増やさない方針）。</summary>
    private static byte[]? ExtractId3v2Picture(Stream stream)
    {
        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header) != 10) return null;
        // "ID3" マジック + メジャーバージョン(3=v2.3, 4=v2.4, 2=v2.2) + マイナー + フラグ(1) + サイズ(4, synchsafe)
        if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3') return null;
        var majorVersion = header[3];
        var flags = header[5];
        var tagSize = SynchsafeToInt(header[6], header[7], header[8], header[9]);
        var extendedHeader = (flags & 0x40) != 0;

        var tagBytes = new byte[tagSize];
        var read = stream.Read(tagBytes, 0, tagSize);
        if (read < tagSize) Array.Resize(ref tagBytes, read);

        var pos = 0;
        // 拡張ヘッダーがある場合はその分だけスキップする（サイズフィールドの読み方はv2.3/2.4で異なる）。
        if (extendedHeader && pos + 4 <= tagBytes.Length)
        {
            var extSize = majorVersion >= 4
                ? SynchsafeToInt(tagBytes[pos], tagBytes[pos + 1], tagBytes[pos + 2], tagBytes[pos + 3])
                : BigEndianToInt(tagBytes[pos], tagBytes[pos + 1], tagBytes[pos + 2], tagBytes[pos + 3]) ; // v2.3は非synchsafe
            pos += (majorVersion >= 4 ? 4 : 4) + extSize;
        }

        if (majorVersion == 2)
        {
            // ID3v2.2: フレームIDは3文字、サイズは3バイト(非synchsafe)固定。画像フレームIDは"PIC"。
            while (pos + 6 <= tagBytes.Length)
            {
                var frameId = System.Text.Encoding.ASCII.GetString(tagBytes, pos, 3);
                if (frameId == "\0\0\0") break;
                var frameSize = BigEndianToInt(0, tagBytes[pos + 3], tagBytes[pos + 4], tagBytes[pos + 5]);
                pos += 6;
                if (pos + frameSize > tagBytes.Length || frameSize <= 0) break;
                if (frameId == "PIC")
                {
                    var picBytes = ParsePicFrame(tagBytes, pos, frameSize);
                    if (picBytes != null) return picBytes;
                }
                pos += frameSize;
            }
            return null;
        }

        // ID3v2.3 / v2.4: フレームIDは4文字、サイズは4バイト（v2.4はsynchsafe、v2.3は通常の整数）。
        while (pos + 10 <= tagBytes.Length)
        {
            var frameId = System.Text.Encoding.ASCII.GetString(tagBytes, pos, 4);
            if (frameId == "\0\0\0\0") break;
            var frameSize = majorVersion >= 4
                ? SynchsafeToInt(tagBytes[pos + 4], tagBytes[pos + 5], tagBytes[pos + 6], tagBytes[pos + 7])
                : BigEndianToInt(tagBytes[pos + 4], tagBytes[pos + 5], tagBytes[pos + 6], tagBytes[pos + 7]);
            pos += 10;
            if (frameSize <= 0 || pos + frameSize > tagBytes.Length) break;
            if (frameId == "APIC")
            {
                var picBytes = ParseApicFrame(tagBytes, pos, frameSize);
                if (picBytes != null) return picBytes;
            }
            pos += frameSize;
        }
        return null;
    }

    /// <summary>APIC(ID3v2.3/2.4)フレームの中身をパースし、画像データ本体だけを返す。
    /// 構造: [エンコーディング1byte][MIMEタイプ(null終端、常にLatin-1)][絵柄種別1byte]
    ///        [説明文(エンコーディングに応じた文字コード、null終端)][画像データ本体]</summary>
    private static byte[]? ParseApicFrame(byte[] data, int offset, int length)
    {
        if (length < 2) return null;
        var p = offset;
        var end = offset + length;
        var textEncoding = data[p]; p += 1;
        var mimeEnd = Array.IndexOf(data, (byte)0, p, end - p);
        if (mimeEnd < 0) return null;
        p = mimeEnd + 1;
        if (p >= end) return null;
        p += 1; // picture type (1 byte)
        p = SkipNullTerminatedString(data, p, end, textEncoding);
        if (p < 0 || p >= end) return null;
        var imgLen = end - p;
        if (imgLen <= 0) return null;
        var result = new byte[imgLen];
        Array.Copy(data, p, result, 0, imgLen);
        return result;
    }

    /// <summary>PIC(ID3v2.2)フレームの中身をパースする。
    /// 構造: [エンコーディング1byte][画像形式3byte(例:"JPG"/"PNG")][絵柄種別1byte]
    ///        [説明文(null終端)][画像データ本体]</summary>
    private static byte[]? ParsePicFrame(byte[] data, int offset, int length)
    {
        if (length < 5) return null;
        var p = offset + 1 + 3 + 1; // エンコーディング + 画像形式3byte + 絵柄種別
        var end = offset + length;
        var textEncoding = data[offset];
        p = SkipNullTerminatedString(data, p, end, textEncoding);
        if (p < 0 || p >= end) return null;
        var imgLen = end - p;
        if (imgLen <= 0) return null;
        var result = new byte[imgLen];
        Array.Copy(data, p, result, 0, imgLen);
        return result;
    }

    /// <summary>ID3タグ内の文字列（説明文等）はエンコーディングにより終端が異なる
    /// （0=Latin-1/1byte終端、1=UTF-16 BOM付き/2byte終端、2=UTF-16BE/2byte終端、3=UTF-8/1byte終端）。
    /// 文字列の中身は使わないため、終端位置を見つけて読み飛ばすだけでよい。</summary>
    private static int SkipNullTerminatedString(byte[] data, int pos, int end, byte textEncoding)
    {
        var wide = textEncoding == 1 || textEncoding == 2;
        if (!wide)
        {
            var idx = Array.IndexOf(data, (byte)0, pos, Math.Max(0, end - pos));
            return idx < 0 ? -1 : idx + 1;
        }
        for (var i = pos; i + 1 < end; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0) return i + 2;
        }
        return -1;
    }

    private static int SynchsafeToInt(byte b0, byte b1, byte b2, byte b3) =>
        (b0 << 21) | (b1 << 14) | (b2 << 7) | b3;

    private static int BigEndianToInt(byte b0, byte b1, byte b2, byte b3) =>
        (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;

    /// <summary>FLACのMETADATA_BLOCK_PICTURE（ブロックタイプ6）から画像データ本体を取り出す。
    /// "fLaC"マーカーに続くメタデータブロックを順に読み、PICTUREブロックが見つかった時点で返す
    /// （STREAMINFO等の他ブロックはスキップするだけでよい）。</summary>
    private static byte[]? ExtractFlacPicture(Stream stream)
    {
        Span<byte> marker = stackalloc byte[4];
        if (stream.Read(marker) != 4) return null;
        if (marker[0] != (byte)'f' || marker[1] != (byte)'L' || marker[2] != (byte)'a' || marker[3] != (byte)'C')
            return null;

        Span<byte> blockHeader = stackalloc byte[4];
        while (true)
        {
            if (stream.Read(blockHeader) != 4) return null;
            var isLast = (blockHeader[0] & 0x80) != 0;
            var blockType = blockHeader[0] & 0x7F;
            var blockLength = BigEndianToInt(0, blockHeader[1], blockHeader[2], blockHeader[3]);

            if (blockType == 6) // PICTURE
            {
                var blockData = new byte[blockLength];
                var read = stream.Read(blockData, 0, blockLength);
                if (read < blockLength) return null;
                return ParseFlacPictureBlock(blockData);
            }

            stream.Seek(blockLength, SeekOrigin.Current);
            if (isLast) return null;
        }
    }

    /// <summary>METADATA_BLOCK_PICTUREの構造（すべてビッグエンディアン）:
    /// [絵柄種別4byte][MIME長4byte][MIME文字列][説明文長4byte][説明文]
    /// [幅4byte][高さ4byte][色深度4byte][インデックスカラー数4byte][画像データ長4byte][画像データ]</summary>
    private static byte[]? ParseFlacPictureBlock(byte[] data)
    {
        var p = 0;
        int ReadBEInt()
        {
            var v = BigEndianToInt(data[p], data[p + 1], data[p + 2], data[p + 3]);
            p += 4;
            return v;
        }
        if (data.Length < 32) return null;

        ReadBEInt(); // 絵柄種別
        var mimeLen = ReadBEInt();
        p += mimeLen; // MIME文字列は使わない
        if (p + 4 > data.Length) return null;
        var descLen = ReadBEInt();
        p += descLen; // 説明文も使わない
        if (p + 20 > data.Length) return null;

        ReadBEInt(); // 幅
        ReadBEInt(); // 高さ
        ReadBEInt(); // 色深度
        ReadBEInt(); // インデックスカラー数
        var dataLen = ReadBEInt();
        if (dataLen <= 0 || p + dataLen > data.Length) return null;

        var result = new byte[dataLen];
        Array.Copy(data, p, result, 0, dataLen);
        return result;
    }
}
