using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Threading;

namespace TanukiTag;

public partial class App : Application
{
    private Window? _window;

    /// <summary>多重起動防止用のシステム全体Mutex。プロセス終了までGCされないよう
    /// フィールドとして保持し続ける（ローカル変数だとファイナライザで早期に解放されうる）。</summary>
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public App()
    {
        InitializeComponent();

        // 無言でクラッシュする問題の切り分け用: 未処理例外をすべてログファイルに書き出す
        this.UnhandledException += (s, e) =>
        {
            LogCrash(e.Exception);
            // e.Handled = true にはしない（原因不明のまま続行させるのは危険なため、
            // ログだけ残して通常通りクラッシュさせる）
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogCrash(ex);
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 多重起動禁止: システム全体で共有される名前付きMutexを取得できるかどうかで判定する。
        // すでに他プロセスが起動中の場合は取得できない（createdNew == false）ため、
        // その場で通知して即終了させる（メインウィンドウは生成しない）。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "TanukiTag_SingleInstance_Mutex", out var createdNew);
        if (!createdNew)
        {
            const uint MB_OK = 0x0;
            const uint MB_ICONINFORMATION = 0x40;
            MessageBoxW(IntPtr.Zero, "TanukiTagはすでに起動しています。", "TanukiTag", MB_OK | MB_ICONINFORMATION);
            Environment.Exit(0);
            return;
        }

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw; // ログは残しつつ元の挙動（クラッシュ）は変えない
        }
    }

    internal static void LogCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Settings");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "crash.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // ログ出力自体の失敗は握りつぶす（これ以上できることがないため）
        }
    }
}
