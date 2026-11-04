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
/// Global hotkeys on a hidden message-only window. The window never takes focus, so the paste target stays
/// the user's app. The same window handle owns the clipboard when we write to it (OpenClipboard needs a non-null owner).
/// <list type="bullet">
/// <item>Ctrl/Shift combinations go through RegisterHotKey with MOD_NOREPEAT: holding the keys fires once.</item>
/// <item>Alt+digit (the Mac's digit shortcuts) goes through a low-level keyboard hook instead. RegisterHotKey
/// would swallow the digit, the focused app would see Alt pressed alone and open its menu (WinUI Notepad key tips,
/// Chrome's menu button), and our Ctrl+C would land there. The hook swallows the digit itself and taps the
/// unassigned vkE8 while Alt is still down, so Alt's release is not "Alt alone" (AutoHotkey's menu mask). In the VM
/// on 2026-10-17 the mask was sent from WM_HOTKEY, i.e. after the system had already processed the press.</item>
/// <item>Right Ctrl alone (the Mac's Fn key) feeds <see cref="Core.RightCtrlDictationToggle"/>; it is never swallowed,
/// so Ctrl+C with the right Ctrl keeps working.</item>
/// </list>
/// The hook callback only decides and posts; the actions run from the message loop, because Windows silently drops
/// a low-level hook that takes longer than LowLevelHooksTimeout.
/// </summary>
internal sealed class HotkeyService : NativeWindow, IDisposable
{
    private const int WM_RUN_POSTED = 0x8000 + 0x51; // WM_APP + n: run the actions the hook queued
    /// <summary>dwExtraInfo of our own menu-mask taps, so the hook ignores them.</summary>
    private static readonly IntPtr MaskSignature = new(0x57535731); // "WSW1"

    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Dictionary<Hotkey, int> _ids = new();
    private readonly Dictionary<Keys, Action> _altDigits = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();
    private readonly LowLevelKeyboardProc _hookProc; // held so the GC keeps the delegate Windows calls
    private IntPtr _hook;
    private Core.RightCtrlDictationToggle? _rightCtrl;
    private bool _rightCtrlDown;
    private Keys _heldAltDigit = Keys.None;
    private int _nextId = 1;

    public HotkeyService()
    {
        // HWND_MESSAGE parent: message-only window, invisible, never in the taskbar or Alt+Tab.
        CreateHandle(new CreateParams { Caption = "WhisperShortcutHotkeys", Parent = new IntPtr(-3) });
        _hookProc = HookCallback;
        InstallHook();
        // Resume from sleep can leave the hook dead; reinstalling is cheap.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Registers the hotkey; returns false when another program already owns it.</summary>
    public bool Register(Hotkey hotkey, Action onPressed)
    {
        if (hotkey.IsRightCtrl) return false; // see RegisterRightCtrl
        if (IsAltDigit(hotkey))
        {
            if (_altDigits.ContainsKey(hotkey.Key))
            {
                Core.Log.Error($"Hotkey {hotkey} already registered by WhisperShortcut");
                return false;
            }
            if (_hook == IntPtr.Zero && !InstallHook()) return false;
            _altDigits[hotkey.Key] = onPressed;
            Core.Log.Info($"Hotkey registered: {hotkey} (keyboard hook)");
            return true;
        }
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

    /// <summary>Right Ctrl alone drives <paramref name="toggle"/> until the next <see cref="UnregisterAll"/>.</summary>
    public bool RegisterRightCtrl(Core.RightCtrlDictationToggle toggle)
    {
        if (_hook == IntPtr.Zero && !InstallHook()) return false;
        _rightCtrl = toggle;
        Core.Log.Info("Hotkey registered: RightCtrl (keyboard hook)");
        return true;
    }

    /// <summary>Removes one hotkey (Esc after a recording); no-op when it is not registered.</summary>
    public void Unregister(Hotkey hotkey)
    {
        if (IsAltDigit(hotkey))
        {
            _altDigits.Remove(hotkey.Key);
            return;
        }
        if (!_ids.Remove(hotkey, out int id)) return;
        UnregisterHotKey(Handle, id);
        _handlers.Remove(id);
    }

    public bool IsRegistered(Hotkey hotkey) => IsAltDigit(hotkey) ? _altDigits.ContainsKey(hotkey.Key) : _ids.ContainsKey(hotkey);

    public void UnregisterAll()
    {
        var count = _handlers.Count + _altDigits.Count + (_rightCtrl != null ? 1 : 0);
        foreach (var id in _handlers.Keys) UnregisterHotKey(Handle, id);
        if (count > 0) Core.Log.Info($"Hotkeys unregistered ({count})");
        _handlers.Clear();
        _ids.Clear();
        _altDigits.Clear();
        _rightCtrl = null;
    }

    private static bool IsAltDigit(Hotkey hotkey) => hotkey.Modifiers == MOD_ALT && hotkey.Key is >= Keys.D0 and <= Keys.D9;

    private bool InstallHook()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Core.Log.Error($"Keyboard hook failed (win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
        }
        _rightCtrlDown = false;
        _heldAltDigit = Keys.None;
        return true;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Post(() =>
        {
            if (InstallHook()) Core.Log.Info("Keyboard hook reinstalled after resume");
        });
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo != MaskSignature && Swallow(k, (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)) return new IntPtr(1);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Decides one key event; true = the focused app never sees it.</summary>
    private bool Swallow(KBDLLHOOKSTRUCT k, bool down)
    {
        bool injected = (k.flags & LLKHF_INJECTED) != 0;
        if (k.vkCode == VK_RCONTROL)
        {
            // Our own Ctrl+V/Ctrl+C use VK_CONTROL (left), so an injected right Ctrl is someone else's; ignore it.
            if (injected) return false;
            if (down && !_rightCtrlDown)
            {
                _rightCtrlDown = true; // key repeat sends more downs; only the first counts
                Post(() => _rightCtrl?.KeyDown());
            }
            else if (!down && _rightCtrlDown)
            {
                _rightCtrlDown = false;
                Post(() => _rightCtrl?.KeyUp());
            }
            return false;
        }
        // A regular key while right Ctrl is held: it was a modifier (Ctrl+C), not a dictation press.
        // Modifiers do not count (Mac: keyDown events only, not flagsChanged).
        if (down && _rightCtrlDown && !injected && !IsModifierKey(k.vkCode)) Post(() => _rightCtrl?.OtherKeyDown());

        var key = (Keys)k.vkCode;
        if (key is < Keys.D0 or > Keys.D9) return false;
        if (!down)
        {
            if (_heldAltDigit != key) return false;
            _heldAltDigit = Keys.None;
            return true; // the down was ours, so is the up
        }
        if (_heldAltDigit == key) return true; // key repeat: fires once, like MOD_NOREPEAT
        if (!OnlyAltHeld(k) || !_altDigits.TryGetValue(key, out var action)) return false;
        _heldAltDigit = key;
        MaskAltRelease();
        Post(action);
        return true;
    }

    /// <summary>
    /// Alt and no other modifier. AltGr (Ctrl+right Alt on German keyboards: AltGr+2 is ², AltGr+3 is ³) has Ctrl down
    /// and is left alone.
    /// </summary>
    private static bool OnlyAltHeld(KBDLLHOOKSTRUCT k) =>
        (k.flags & LLKHF_ALTDOWN) != 0
        && !IsDown(VK_CONTROL) && !IsDown(VK_SHIFT) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierKey(uint vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN
        or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU;

    /// <summary>Taps vkE8 while Alt is still down, so the Alt release that follows does not open the focused app's menu.</summary>
    private static void MaskAltRelease()
    {
        var inputs = new[] { MaskKey(up: false), MaskKey(up: true) };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static INPUT MaskKey(bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU_MASK, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = MaskSignature } },
    };

    private void Post(Action action)
    {
        _posted.Enqueue(action);
        PostMessage(Handle, WM_RUN_POSTED, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _handlers.TryGetValue((int)m.WParam, out var handler))
        {
            handler();
            return;
        }
        if (m.Msg == WM_RUN_POSTED)
        {
            while (_posted.TryDequeue(out var action)) action();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        UnregisterAll();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        DestroyHandle();
    }
}
