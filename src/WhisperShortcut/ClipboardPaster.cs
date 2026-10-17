using System.Runtime.InteropServices;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

internal enum PasteOutcome
{
    /// <summary>Ctrl+V was sent; the old clipboard comes back after 900 ms.</summary>
    Pasted,
    /// <summary>The target runs as administrator (UIPI would drop our keystrokes): text stays on the clipboard.</summary>
    ClipboardOnlyElevatedTarget,
    /// <summary>Ctrl/Alt were still held after 3 s: a Ctrl+V now would arrive as Ctrl+Alt+V. Text stays on the clipboard.</summary>
    ClipboardOnlyKeysHeld,
    /// <summary>Could not write the clipboard at all.</summary>
    Failed,
}

/// <summary>
/// Clipboard + synthetic Ctrl+C / Ctrl+V, the Windows port of the Mac's non-destructive paste.
/// Runs on the UI thread; waits are awaited, never blocking. Only text is saved and restored.
/// </summary>
internal sealed class ClipboardPaster
{
    /// <summary>Word and Outlook read the clipboard later than Mac apps do (Mac: 400 ms).</summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(900);
    // 500 ms (the plan's first number) was too short: a hand holding the hotkey 600 ms made Ctrl+V arrive
    // as Ctrl+Alt+V (= AltGr+V, typed "√" in Notepad). Found in the VM with `vm.sh hold 600`.
    private static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CopyDeadline = TimeSpan.FromMilliseconds(500);

    private readonly IntPtr _owner;

    public ClipboardPaster(IntPtr clipboardOwner) => _owner = clipboardOwner;

    /// <summary>
    /// Puts <paramref name="text"/> at the cursor: save clipboard text, set ours, Ctrl+V, and after
    /// 900 ms put the old text back, but only when nobody else wrote the clipboard in between.
    /// </summary>
    public async Task<PasteOutcome> PasteAsync(string text)
    {
        // The user may still hold Ctrl+Alt from the hotkey: Ctrl+V would arrive as Ctrl+Alt+V.
        if (!await WaitForModifiersReleasedAsync())
            return WriteText(text) ? PasteOutcome.ClipboardOnlyKeysHeld : PasteOutcome.Failed;

        if (ForegroundIsElevated())
        {
            Core.Log.Info("Paste: foreground window runs elevated, leaving text on the clipboard");
            return WriteText(text) ? PasteOutcome.ClipboardOnlyElevatedTarget : PasteOutcome.Failed;
        }

        bool hadText = TryReadText(out var previous);
        if (!WriteText(text)) return PasteOutcome.Failed;
        uint ourSequence = GetClipboardSequenceNumber();

        await Task.Delay(50);
        SendChord(0x56); // V
        Core.Log.Info($"Paste: sent Ctrl+V ({text.Length} chars)");

        _ = RestoreLaterAsync(hadText ? previous : null, ourSequence);
        return PasteOutcome.Pasted;
    }

    private async Task RestoreLaterAsync(string? previous, uint ourSequence)
    {
        await Task.Delay(RestoreDelay);
        if (previous == null) return; // there was no text before; nothing of the user's to bring back
        if (GetClipboardSequenceNumber() != ourSequence)
        {
            Core.Log.Info("Paste: clipboard changed by someone else, not restoring");
            return;
        }
        Core.Log.Info(WriteText(previous) ? "Paste: restored previous clipboard" : "Paste: restore failed");
    }

    /// <summary>
    /// Copies the current selection with Ctrl+C and returns it, or null when nothing was selected.
    /// Only a changed clipboard sequence number counts: an unchanged clipboard still holds an old copy
    /// that has nothing to do with this recording. The user's clipboard is put back afterwards.
    /// </summary>
    public async Task<string?> CopySelectionAsync()
    {
        if (!await WaitForModifiersReleasedAsync()) return null;
        if (ForegroundIsElevated())
        {
            Core.Log.Info("Copy: foreground window runs elevated, treating as no selection");
            return null;
        }

        bool hadText = TryReadText(out var previous);
        uint before = GetClipboardSequenceNumber();
        SendChord(0x43); // C

        var deadline = DateTime.UtcNow + CopyDeadline;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
            if (GetClipboardSequenceNumber() == before) continue;
            // Some apps announce the change before the data is readable; give them a few tries.
            string? copied = null;
            for (int i = 0; i < 5 && copied == null; i++)
            {
                if (!TryReadText(out copied)) await Task.Delay(15);
            }
            if (hadText) WriteText(previous!);
            var trimmed = copied?.Trim();
            Core.Log.Info($"Copy: selection {(string.IsNullOrEmpty(trimmed) ? "empty" : trimmed.Length + " chars")}");
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
        Core.Log.Info("Copy: clipboard unchanged after 500 ms, no selection");
        return null;
    }

    // ---- keyboard ----

    private static readonly int[] Modifiers =
        { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU };

    private static bool AnyModifierDown() => Modifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>
    /// Waits (max 3 s) until Ctrl, Alt, AltGr, Shift and both Win keys are up; false when they never were.
    /// We never send an Alt key-up ourselves: a lone Alt release opens the menu bar in Word and Notepad.
    /// </summary>
    private static async Task<bool> WaitForModifiersReleasedAsync()
    {
        var start = DateTime.UtcNow;
        while (AnyModifierDown())
        {
            if (DateTime.UtcNow - start >= ModifierWait)
            {
                Core.Log.Info("Modifiers still held after 3 s, not sending keystrokes");
                return false;
            }
            await Task.Delay(10);
        }
        var waited = DateTime.UtcNow - start;
        if (waited.TotalMilliseconds >= 20) Core.Log.Info($"Waited {waited.TotalMilliseconds:0} ms for modifiers to be released");
        return true;
    }

    private static void SendChord(ushort key)
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false), Key(key, false), Key(key, true), Key(VK_CONTROL, true),
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) Core.Log.Error($"SendInput sent {sent}/{inputs.Length} (win32 error {Marshal.GetLastWin32Error()})");
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = up ? KEYEVENTF_KEYUP : 0 },
        },
    };

    // ---- UIPI ----

    /// <summary>
    /// True when the foreground process has a higher integrity level than we do (e.g. "Als Administrator
    /// ausführen"). Windows silently drops SendInput into such windows; SendInput even reports success.
    /// </summary>
    private static bool ForegroundIsElevated()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        int? theirs = IntegrityOf(pid);
        int? ours = IntegrityOf((uint)Environment.ProcessId);
        // Can't read their token although it's our own session: that only happens for elevated processes.
        if (theirs == null) return ours != null;
        return ours != null && theirs > ours;
    }

    private static int? IntegrityOf(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) return null;
                    IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    // ---- clipboard ----

    /// <summary>Other programs hold the clipboard open now and then: retry 10 × 20 ms.</summary>
    private bool Open()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(_owner)) return true;
            Thread.Sleep(20);
        }
        Core.Log.Error("OpenClipboard failed 10 times");
        return false;
    }

    private bool TryReadText(out string? text)
    {
        text = null;
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return false;
        if (!Open()) return false;
        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return false;
            IntPtr ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return false;
            try { text = Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(handle); }
            return text != null;
        }
        finally { CloseClipboard(); }
    }

    private bool WriteText(string text)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) return false;
            IntPtr ptr = GlobalLock(handle);
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0);
            GlobalUnlock(handle);
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            return true; // the system owns the memory now
        }
        finally { CloseClipboard(); }
    }
}
