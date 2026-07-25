using System.Runtime.InteropServices;

namespace TanukiTag.Services;

/// <summary>
/// libjpeg-turboの「DCTスケールデコード」（1/2, 1/4, 1/8で最初から縮小しながらデコードする機能）を
/// 使い、大きなJPEGをサムネイルサイズまで縮小する際のデコード自体を高速化するネイティブDLL
/// （native/FastJpegDecoder/FastJpegDecoder.cpp をビルドしたもの）のラッパー。
/// ImageSharpにはこの機能が無く、フルサイズでデコードしてからリサイズするしかないため、
/// 特に大きいJPEG（デジカメ写真等）のサムネイル生成で効果が大きい。
///
/// FastJpegDecoder.dll をexeと同じ場所に配置すると自動的に使われる。DLLが無い、
/// または読み込み・デコードに失敗した場合は、呼び出し側（ThumbnailGenerator）が
/// 自動的にImageSharpの通常デコードにフォールバックする（例外は外に投げない）。
/// </summary>
internal static class FastJpegDecoder
{
    private const string DllName = "FastJpegDecoder";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FastJpeg_DecodeScaled(
        byte[] data, int len, int targetSize,
        out IntPtr outBuffer, out int outWidth, out int outHeight);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FastJpeg_Free(IntPtr buffer);

    /// <summary>DLL自体が見つからない環境（未配置、または32bit/ARM64等でビルドしていない場合）では
    /// 呼び出すたびにDllNotFoundExceptionの例外コストを払うのは無駄なため、一度失敗したら
    /// 以降のGenerate呼び出しでは試さずに即フォールバックする。</summary>
    private static bool _unavailable;

    /// <summary>JPEGバイト列を、target_size以上の幅・高さを維持できる最大の縮小率でデコードする。
    /// 成功時はRGB24（1ピクセル3バイト、行パディング無し）の生データと幅・高さを返す。
    /// DLL未配置・デコード失敗時はnullを返す（呼び出し側はImageSharpの通常デコードに
    /// フォールバックすること）。</summary>
    public static (byte[] Rgb, int Width, int Height)? TryDecodeScaled(byte[] jpegBytes, int targetSize)
    {
        if (_unavailable) return null;

        var buffer = IntPtr.Zero;
        try
        {
            var ok = FastJpeg_DecodeScaled(jpegBytes, jpegBytes.Length, targetSize, out buffer, out var w, out var h);
            if (ok == 0 || buffer == IntPtr.Zero || w <= 0 || h <= 0) return null;

            var length = w * h * 3;
            var managed = new byte[length];
            Marshal.Copy(buffer, managed, 0, length);
            return (managed, w, h);
        }
        catch (DllNotFoundException)
        {
            _unavailable = true; // DLL未配置。以後は毎回試さずスキップする。
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            _unavailable = true; // 古い/不整合なDLL。
            return null;
        }
        catch
        {
            return null; // その他の予期しない失敗もフォールバックに任せる
        }
        finally
        {
            if (buffer != IntPtr.Zero) FastJpeg_Free(buffer);
        }
    }
}
