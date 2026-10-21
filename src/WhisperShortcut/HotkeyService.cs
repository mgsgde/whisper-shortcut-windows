using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>A parsed hotkey like "Ctrl+Shift+D".</summary>
internal readonly record struct Hotkey(uint Modifiers, Keys Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0;
        Keys key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "strg": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": case "umschalt": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (key != Keys.None) return false;
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        if (key == Keys.None || mods == 0) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>Storage form, English modifier names ("Ctrl+Shift+D").</summary>
    public override string ToString() => Format("Ctrl", "Alt", "Shift", "Win");

    /// <summary>German form for the UI ("Strg+Umschalt+D").</summary>
    public string ToDisplayString() => Format("Strg", "Alt", "Umschalt", "Win");

    private string Format(string ctrl, string alt, string shift, string win)
    {
        var parts = new List<string>();
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add(ctrl);
        if ((Modifiers & MOD_ALT) != 0) parts.Add(alt);
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add(shift);
        if ((Modifiers & MOD_WIN) != 0) parts.Add(win);
        parts.Add(Key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (Key - Keys.D0))).ToString() : Key.ToString());
        return string.Join("+", parts);
    }

    public static string Display(string stored) => TryParse(stored, out var h) ? h.ToDisplayString() : stored;
}

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message-only window. The window never takes focus,
/// so the paste target stays the user's app. MOD_NOREPEAT: holding the keys fires once, not every 30 ms.
/// No Alt in the default hotkeys: RegisterHotKey swallows the letter, so the focused app sees Alt
/// pressed alone and opens its menu (WinUI Notepad key tips, Chrome's menu button), and our Ctrl+C
/// lands there. Masking the Alt release (vkE8 from WM_HOTKEY, and a low-level hook) did not stop it
/// in the VM, 2026-10-17; Ctrl+Shift worked at once.
/// The same window handle owns the clipboard when we write to it (OpenClipboard needs a non-null owner).
/// </summary>
internal sealed class HotkeyService : NativeWindow, IDisposable
{
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Dictionary<Hotkey, int> _ids = new();
    private int _nextId = 1;

    public HotkeyService()
    {
        // HWND_MESSAGE parent: message-only window, invisible, never in the taskbar or Alt+Tab.
        CreateHandle(new CreateParams { Caption = "WhisperShortcutHotkeys", Parent = new IntPtr(-3) });
    }

    /// <summary>Registers the hotkey; returns false when another program already owns it.</summary>
    public bool Register(Hotkey hotkey, Action onPressed)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.Key))
        {
            Core.Log.Error($"RegisterHotKey failed for {hotkey} (win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
        }
        _handlers[id] = onPressed;
        _ids[hotkey] = id;
        Core.Log.Info($"Hotkey registered: {hotkey}");
        return true;
    }

    /// <summary>Removes one hotkey (Esc after a recording); no-op when it is not registered.</summary>
    public void Unregister(Hotkey hotkey)
    {
        if (!_ids.Remove(hotkey, out int id)) return;
        UnregisterHotKey(Handle, id);
        _handlers.Remove(id);
    }

    public bool IsRegistered(Hotkey hotkey) => _ids.ContainsKey(hotkey);

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys) UnregisterHotKey(Handle, id);
        _handlers.Clear();
        _ids.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _handlers.TryGetValue((int)m.WParam, out var handler))
        {
            handler();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
