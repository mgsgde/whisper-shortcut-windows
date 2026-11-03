namespace WhisperShortcut.Core.Mcp;

/// <summary>Core copy of the app's hotkey rules, so the settings catalog can validate without WinForms.</summary>
public static class HotkeyRules
{
    private static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift", "Win" };

    private static string? ModifierName(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "strg" => "Ctrl",
        "alt" => "Alt",
        "shift" or "umschalt" => "Shift",
        "win" or "windows" => "Win",
        _ => null,
    };

    private static string? KeyName(string part)
    {
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c.ToString();
            return null;
        }
        if ((part[0] is 'f' or 'F') && int.TryParse(part.AsSpan(1), out var n) && n is >= 1 and <= 12 && part.Length <= 3
            && part.Skip(1).All(char.IsAsciiDigit))
            return "F" + n;
        return null;
    }

    /// <summary>
    /// "Ctrl+Shift+D", "strg+umschalt+d", "Ctrl+Alt+Shift+F5", "alt+1" give the normalized form (order Ctrl, Alt, Shift, Win;
    /// key an upper-case letter, digit 0-9 or F1..F12). At least one modifier is required, except for the right Ctrl key
    /// alone ("RightCtrl", "rightControl", "rechte Strg"), which normalizes to "RightCtrl". Anything else returns false.
    /// </summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (HotkeyText.IsRightCtrl(text))
        {
            normalized = HotkeyText.RightCtrl;
            return true;
        }
        var parts = text.Split('+').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        var mods = new HashSet<string>();
        foreach (var p in parts[..^1])
        {
            var m = ModifierName(p);
            if (m == null || !mods.Add(m)) return false;
        }
        var key = KeyName(parts[^1]);
        if (key == null) return false;
        normalized = string.Join("+", ModifierOrder.Where(mods.Contains).Append(key));
        return true;
    }

    /// <summary>
    /// Same rule as the settings window (<see cref="HotkeyText.IsSafe"/>): Alt+digit, the right Ctrl key alone, or at least
    /// two of Ctrl/Alt/Shift with one of them Ctrl or Alt; Ctrl+Alt without Shift is refused (AltGr on German keyboards).
    /// Win may be added. Input is a normalized string.
    /// </summary>
    public static bool IsSafe(string normalized) =>
        TryNormalize(normalized, out var n) && HotkeyText.TryParse(n, out var mods, out var key) && HotkeyText.IsSafe(mods, key);

    /// <summary>German display: "Strg+Umschalt+D", "Alt+1", "rechte Strg".</summary>
    public static string Display(string normalized) =>
        HotkeyText.TryParse(normalized, out var mods, out var key) ? HotkeyText.Format(mods, key, german: true) : normalized;
}
