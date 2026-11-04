using System.Runtime.InteropServices;
using System.Text;

namespace WhisperShortcut;

/// <summary>
/// The character already in the field before Live diktieren's first piece (Mac <c>FocusedTextContext</c>).
/// <para>
/// The Mac asks Accessibility; Windows reads the focused classic edit control of the foreground window (Edit and
/// RichEdit, which is what Notepad and most form-based practice software use) with two messages, 150 ms each. Anything
/// else (browsers, Word, a password field, a hung window) is <see cref="Core.CaretContext.Unknown"/>, and the first
/// piece is spaced as before, or from <see cref="LiveInsertionMemory"/>. The character is patient text and is never
/// logged; <see cref="Core.CaretContext.LogKind"/> is.
/// </para>
/// </summary>
internal static class FocusedTextContext
{
    /// <summary>Never throws.</summary>
    public static Core.CaretContext CharacterBeforeCaret(IntPtr foreground)
    {
        try
        {
            if (foreground == IntPtr.Zero) return Core.CaretContext.Unknown;
            uint thread = GetWindowThreadProcessId(foreground, out _);
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero) return Core.CaretContext.Unknown;
            var focus = info.hwndFocus;

            var cls = new StringBuilder(64);
            GetClassName(focus, cls, cls.Capacity);
            var name = cls.ToString();
            bool richEdit = name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("RICHEDIT", StringComparison.Ordinal);
            // "Edit", "TEdit"/"TMemo" (Delphi), "WindowsForms10.EDIT…", "RichEditD2DPT" (Windows 11 Notepad).
            bool edit = richEdit || name.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("EDIT", StringComparison.OrdinalIgnoreCase) || name.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)
                || name is "TEdit" or "TMemo";
            if (!edit) return Core.CaretContext.Unknown;
            if ((GetWindowLongPtr(focus, GWL_STYLE).ToInt64() & ES_PASSWORD) != 0) return Core.CaretContext.Unknown;

            // EM_GETSEL: low word = selection start (a caret is an empty selection). The DWORD form caps at 65535;
            // the pointer form would need memory in the other process, so long texts stay unknown.
            if (SendMessageTimeout(focus, EM_GETSEL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out var sel) == IntPtr.Zero)
                return Core.CaretContext.Unknown;
            long packed = sel.ToInt64();
            int start = (int)(packed & 0xFFFF);
            if (start == 0xFFFF) return Core.CaretContext.Unknown;
            if (start == 0) return Core.CaretContext.StartOfText;

            if (SendMessageTimeout(focus, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out var len) == IntPtr.Zero)
                return Core.CaretContext.Unknown;
            int length = (int)Math.Min(len.ToInt64(), MaxChars);
            if (length <= 0 || len.ToInt64() > MaxChars) return Core.CaretContext.Unknown;
            var buffer = new StringBuilder(length + 1);
            if (SendMessageTimeoutText(focus, WM_GETTEXT, (IntPtr)(length + 1), buffer, SMTO_ABORTIFHUNG, TimeoutMs, out _) == IntPtr.Zero)
                return Core.CaretContext.Unknown;
            return Core.LiveDictation.CaretFromText(buffer.ToString(), start, crCountsAsOneBeforeLf: richEdit);
        }
        catch (Exception e)
        {
            Core.Log.Warn($"LIVE-DICTATE: caret read failed ({e.GetType().Name})");
            return Core.CaretContext.Unknown;
        }
    }

    /// <summary>The process name (without ".exe") that owns <paramref name="hwnd"/>, or null.</summary>
    public static string? ProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const uint TimeoutMs = 150; // Mac: AXUIElementSetMessagingTimeout 0.15 s
    private const long MaxChars = 0xFFFF;
    private const int EM_GETSEL = 0x00B0, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int GWL_STYLE = -16;
    private const long ES_PASSWORD = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>
/// How the previous Live dictation ended, so the next one can space and case its first piece when the caret cannot be
/// read (Mac <c>MenuBarController.rememberLastLiveInsertion</c>). In memory only, never logged. Any click, any key that
/// is not one of our own shortcuts, and any switch to another program forgets it. The hooks exist only while a memory
/// does; injected input (our own Ctrl+V) is ignored.
/// </summary>
internal sealed class LiveInsertionMemory : IDisposable
{
    private readonly Func<Keys, bool> _isOwnShortcut;
    private readonly System.Windows.Forms.Timer _foregroundPoll = new() { Interval = 500 };
    private LowLevelProc? _keyboardProc, _mouseProc; // kept alive while hooked
    private IntPtr _keyboardHook, _mouseHook;
    private long _armedAt;

    /// <param name="isOwnShortcut">True for a key press (key plus modifiers held) that is one of our hotkeys.</param>
    public LiveInsertionMemory(Func<Keys, bool> isOwnShortcut)
    {
        _isOwnShortcut = isOwnShortcut;
        _foregroundPoll.Tick += (_, _) =>
        {
            if (Last == null) return;
            var app = FocusedTextContext.ProcessName(ClipboardPaster.ForegroundRoot());
            if (app != null && !string.Equals(app, Last.App, StringComparison.OrdinalIgnoreCase)) Forget("app switch");
        };
    }

    /// <summary>The memory, while nothing has moved the caret since.</summary>
    public Core.LastInsertion? Last { get; private set; }

    /// <summary>Remembers a finished dictation into <paramref name="app"/>. Call on the UI thread.</summary>
    public void Remember(string insertedText, string? app)
    {
        Forget(null);
        var memory = Core.LastInsertion.Make(insertedText, app, DateTime.UtcNow);
        if (memory == null) return;
        Last = memory;
        // A straggling Ctrl+V of our own is long delivered (the piece gap); this covers any late echo.
        _armedAt = Environment.TickCount64 + 300;
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            // Without the hooks a click could go unseen: better no memory than a wrong space.
            Core.Log.Warn("LIVE-DICTATE: input hooks unavailable, not remembering the last dictation");
            Forget(null);
            return;
        }
        _foregroundPoll.Start();
        Core.Log.Info("LIVE-DICTATE: remembering how the dictation ended for the next one");
    }

    /// <summary>Drops the memory and the hooks. <paramref name="reason"/> is logged when a memory existed.</summary>
    public void Forget(string? reason)
    {
        _foregroundPoll.Stop();
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
        if (Last != null && reason != null) Core.Log.Info($"LIVE-DICTATE: forgot the last dictation ({reason})");
        Last = null;
    }

    public void Dispose()
    {
        Forget(null);
        _foregroundPoll.Dispose();
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) && Environment.TickCount64 >= _armedAt)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var key = (Keys)data.vkCode;
            bool injected = (data.flags & LLKHF_INJECTED) != 0;
            if (!injected && !IsModifier(key))
            {
                var withModifiers = key | (Down(Keys.ControlKey) ? Keys.Control : 0) | (Down(Keys.Menu) ? Keys.Alt : 0)
                    | (Down(Keys.ShiftKey) ? Keys.Shift : 0);
                if (!_isOwnShortcut(withModifiers)) ForgetLater("input");
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Environment.TickCount64 >= _armedAt && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) == 0) ForgetLater("input");
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // WHY posted: unhooking from inside the hook callback is allowed but the callback must return quickly.
    private void ForgetLater(string reason)
    {
        if (SynchronizationContext.Current is { } ui) ui.Post(_ => Forget(reason), null);
        else Forget(reason);
    }

    private static bool Down(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;

    private static bool IsModifier(Keys k) => k is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.ControlKey
        or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private static readonly IntPtr WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
