namespace WhisperShortcut.Core;

/// <summary>
/// Pure text form of a hotkey ("Ctrl+Shift+D", "Alt+1", "RightCtrl"), without WinForms, so tests reach it.
/// The app's <c>Hotkey</c> turns the key token into a <c>Keys</c> value; the modifier bits are the Win32 MOD_* values.
/// </summary>
public static class HotkeyText
{
    /// <summary>MOD_ALT.</summary>
    public const uint Alt = 0x0001;

    /// <summary>MOD_CONTROL.</summary>
    public const uint Ctrl = 0x0002;

    /// <summary>MOD_SHIFT.</summary>
    public const uint Shift = 0x0004;

    /// <summary>MOD_WIN.</summary>
    public const uint Win = 0x0008;

    /// <summary>
    /// Stored value for the right Ctrl key pressed alone, the Windows stand-in for the Mac's Fn key. It has no modifiers
    /// and no other key; the parser returns it as the key token with modifiers 0.
    /// </summary>
    public const string RightCtrl = "RightCtrl";

    /// <summary>German display of <see cref="RightCtrl"/>.</summary>
    public const string RightCtrlDisplay = "rechte Strg";

    /// <summary>True for "RightCtrl" and its spellings ("rightcontrol", "rechte Strg", "RCtrl"), case and spaces ignored.</summary>
    public static bool IsRightCtrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_')).ToLowerInvariant();
        return t is "rightctrl" or "rightcontrol" or "rctrl" or "rcontrol" or "rechtestrg" or "strgrechts";
    }

    /// <summary>
    /// Splits <paramref name="text"/> into modifier bits and one key token. A digit key comes back as "0".."9";
    /// any other key token is returned as written (the caller decides whether it names a real key). At least one modifier
    /// is required, except for <see cref="RightCtrl"/>, which returns modifiers 0 and key "RightCtrl".
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out string key)
    {
        modifiers = 0;
        key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsRightCtrl(text))
        {
            key = RightCtrl;
            return true;
        }
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "strg": case "control": modifiers |= Ctrl; break;
                case "alt": modifiers |= Alt; break;
                case "shift": case "umschalt": modifiers |= Shift; break;
                case "win": modifiers |= Win; break;
                default:
                    if (key.Length > 0) return Fail(out modifiers, out key);
                    key = raw;
                    break;
            }
        }
        if (key.Length == 0 || modifiers == 0) return Fail(out modifiers, out key);
        return true;
    }

    private static bool Fail(out uint modifiers, out string key)
    {
        modifiers = 0;
        key = "";
        return false;
    }

    /// <summary>
    /// Text form: English modifier names for storage ("Ctrl+Shift+D", "Alt+1", "RightCtrl"), German for the UI
    /// ("Strg+Umschalt+D", "Alt+1", "rechte Strg"). Order Ctrl, Alt, Shift, Win.
    /// </summary>
    public static string Format(uint modifiers, string key, bool german)
    {
        if (modifiers == 0 && key == RightCtrl) return german ? RightCtrlDisplay : RightCtrl;
        var parts = new List<string>();
        if ((modifiers & Ctrl) != 0) parts.Add(german ? "Strg" : "Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add(german ? "Umschalt" : "Shift");
        if ((modifiers & Win) != 0) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>
    /// The one rule for a global hotkey, shared by the settings window, the meeting dialog and the AI-app catalog:
    /// <list type="bullet">
    /// <item><see cref="RightCtrl"/> alone (Mac Fn).</item>
    /// <item>Alt plus a digit and nothing else (the Mac's digit shortcuts; Windows uses Alt because Ctrl+Alt is AltGr).</item>
    /// <item>Otherwise two of Ctrl/Alt/Shift, one of them Ctrl or Alt; Ctrl+Alt without Shift is refused (AltGr on
    /// German keyboards: Ctrl+Alt+Q would swallow every @). Win may be added.</item>
    /// </list>
    /// </summary>
    public static bool IsSafe(uint modifiers, string key)
    {
        if (key == RightCtrl) return modifiers == 0;
        if (modifiers == Alt && key.Length == 1 && key[0] is >= '0' and <= '9') return true;
        bool ctrl = (modifiers & Ctrl) != 0, alt = (modifiers & Alt) != 0, shift = (modifiers & Shift) != 0;
        int count = (ctrl ? 1 : 0) + (alt ? 1 : 0) + (shift ? 1 : 0);
        if (ctrl && alt && !shift) return false;
        return count >= 2 && (ctrl || alt);
    }
}
