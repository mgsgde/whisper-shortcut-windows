using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// A parsed hotkey like "Ctrl+Shift+D", "Alt+1", or "RightCtrl" (right Ctrl alone: no modifiers, key
/// <see cref="Keys.RControlKey"/>). Text parsing and formatting live in <see cref="Core.HotkeyText"/>.
/// </summary>
internal readonly record struct Hotkey(uint Modifiers, Keys Key)
{
    /// <summary>The right Ctrl key alone, the Windows stand-in for the Mac's Fn key.</summary>
    public static readonly Hotkey RightCtrl = new(0, Keys.RControlKey);

    /// <summary>True for <see cref="RightCtrl"/>, which RegisterHotKey cannot serve (it needs the keyboard hook).</summary>
    public bool IsRightCtrl => this == RightCtrl;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (!Core.HotkeyText.TryParse(text, out var mods, out var token)) return false;
        if (token == Core.HotkeyText.RightCtrl)
        {
            hotkey = RightCtrl;
            return true;
        }
        Keys key;
        if (token.Length == 1 && char.IsDigit(token[0])) key = Keys.D0 + (token[0] - '0');
        else if (!Enum.TryParse(token, ignoreCase: true, out key)) return false;
        if (key == Keys.None) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>Storage form, English modifier names ("Ctrl+Shift+D", "Alt+1", "RightCtrl").</summary>
    public override string ToString() => Core.HotkeyText.Format(Modifiers, KeyToken, german: false);

    /// <summary>German form for the UI ("Strg+Umschalt+D", "Alt+1", "rechte Strg").</summary>
    public string ToDisplayString() => Core.HotkeyText.Format(Modifiers, KeyToken, german: true);

    /// <summary>Shared safety rule (<see cref="Core.HotkeyText.IsSafe"/>): Alt+digit, RightCtrl, or two of Ctrl/Alt/Shift without AltGr.</summary>
    public bool IsSafe => Core.HotkeyText.IsSafe(Modifiers, KeyToken);

    private string KeyToken => IsRightCtrl ? Core.HotkeyText.RightCtrl
        : Key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (Key - Keys.D0))).ToString() : Key.ToString();

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
        var count = _handlers.Count;
        foreach (var id in _handlers.Keys) UnregisterHotKey(Handle, id);
        if (count > 0) Core.Log.Info($"Hotkeys unregistered ({count})");
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
