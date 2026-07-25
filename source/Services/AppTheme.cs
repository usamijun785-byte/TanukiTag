using System.Linq;
using Windows.UI;

namespace TanukiTag.Services;

/// <summary>1テーマ分の配色パレット。Python版 THEMES の各辞書エントリに対応。</summary>
public class ThemePalette
{
    public required Color Bg { get; init; }
    public required Color Bg2 { get; init; }
    public required Color Bg3 { get; init; }
    public required Color Fg { get; init; }
    public required Color Fg2 { get; init; }
    public required Color Border { get; init; }
    public required Color Sel { get; init; }
    public required Color Cell { get; init; }
    public required Color Thumb { get; init; }
}

/// <summary>
/// Python版 tagfiler.py の THEMES 辞書を移植。キー名(dark/light/pastel_blue...)も同じにしてある。
/// </summary>
public static class AppTheme
{
    private static Color H(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }

    /// <summary>テーマキー ⇄ 日本語表示名。設定画面のコンボボックス表示に使用する。</summary>
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["dark"] = "ダーク",
        ["light"] = "ライト",
        ["pastel_blue"] = "パステルブルー",
        ["pastel_green"] = "パステルグリーン",
        ["pastel_pink"] = "パステルピンク",
        ["pastel_yellow"] = "パステルイエロー",
    };

    public static string GetDisplayName(string key) =>
        DisplayNames.TryGetValue(key, out var name) ? name : key;

    public static string GetKeyFromDisplayName(string displayName) =>
        DisplayNames.FirstOrDefault(kv => kv.Value == displayName).Key ?? displayName;

    public static readonly Dictionary<string, ThemePalette> Themes = new()
    {
        ["dark"] = new ThemePalette
        {
            Bg = H("#1E1E1E"), Bg2 = H("#252525"), Bg3 = H("#1A1A1A"),
            Fg = H("#E0E0E0"), Fg2 = H("#AAAAAA"), Border = H("#3A3A3A"),
            Sel = H("#0078D4"), Cell = H("#252525"), Thumb = H("#1A1A1A"),
        },
        ["light"] = new ThemePalette
        {
            Bg = H("#F0F0F0"), Bg2 = H("#FAFAFA"), Bg3 = H("#E8E8E8"),
            Fg = H("#1A1A1A"), Fg2 = H("#555555"), Border = H("#CCCCCC"),
            Sel = H("#0078D4"), Cell = H("#FFFFFF"), Thumb = H("#E8E8E8"),
        },
        ["pastel_blue"] = new ThemePalette
        {
            Bg = H("#EEF2F7"), Bg2 = H("#F8FAFD"), Bg3 = H("#E4EAF3"),
            Fg = H("#2C3E6A"), Fg2 = H("#6A7FA8"), Border = H("#C5D3E8"),
            Sel = H("#7AA6D4"), Cell = H("#F0F5FC"), Thumb = H("#E0EAFA"),
        },
        ["pastel_green"] = new ThemePalette
        {
            Bg = H("#EEF7F0"), Bg2 = H("#F6FCF7"), Bg3 = H("#E0F0E4"),
            Fg = H("#1E4A2A"), Fg2 = H("#5A8A64"), Border = H("#B8DFC0"),
            Sel = H("#5FAD72"), Cell = H("#F0FAF2"), Thumb = H("#DCEEE0"),
        },
        ["pastel_pink"] = new ThemePalette
        {
            Bg = H("#F7EEF2"), Bg2 = H("#FDF6F9"), Bg3 = H("#F0E0EA"),
            Fg = H("#6A2C42"), Fg2 = H("#A87080"), Border = H("#E8C5D0"),
            Sel = H("#D47AA0"), Cell = H("#FDF0F5"), Thumb = H("#F5E0EC"),
        },
        ["pastel_yellow"] = new ThemePalette
        {
            Bg = H("#F7F5EE"), Bg2 = H("#FDFBF5"), Bg3 = H("#F0ECD8"),
            Fg = H("#4A4210"), Fg2 = H("#8A7A40"), Border = H("#E0D4A0"),
            Sel = H("#C4A830"), Cell = H("#FDFAF0"), Thumb = H("#F0EAD0"),
        },
    };

    public static ThemePalette Get(string name) =>
        Themes.TryGetValue(name, out var p) ? p : Themes["dark"];
}
