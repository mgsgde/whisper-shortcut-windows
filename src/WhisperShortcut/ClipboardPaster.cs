using System.Globalization;
using System.Runtime.InteropServices;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

internal enum PasteOutcome
{
    /// <summary>Ctrl+V was sent; the old clipboard comes back after 3 s.</summary>
    Pasted,
    /// <summary>The target runs as administrator (UIPI would drop our keystrokes): text stays on the clipboard.</summary>
    ClipboardOnlyElevatedTarget,
    /// <summary>Ctrl/Alt were still held after 3 s: a Ctrl+V now would arrive as Ctrl+Alt+V. Text stays on the clipboard.</summary>
    ClipboardOnlyKeysHeld,
    /// <summary>"Automatisch einfügen" is off: the text was only put on the clipboard.</summary>
    ClipboardOnlyByChoice,
    /// <summary>The user switched to another window since the recording started and the old one could not be brought back. Text stays on the clipboard.</summary>
    ClipboardOnlyTargetChanged,
    /// <summary>The window the recording started in was closed. Text stays on the clipboard.</summary>
    ClipboardOnlyTargetGone,
    /// <summary>The window in front is one of our own (settings, a message box): Ctrl+V would vanish there. Text stays on the clipboard.</summary>
    ClipboardOnlyOwnWindow,
    /// <summary>Something else wrote the clipboard between our write and Ctrl+V: nothing pasted, the clipboard is left as it is.</summary>
    ClipboardOnlyClipboardChanged,
    /// <summary>Could not write the clipboard at all.</summary>
    Failed,
}

/// <summary>The clipboard changed after Ctrl+C but its text could not be read: there may be a selection we never saw.</summary>
internal sealed class SelectionUnreadableException : Exception
{
    public SelectionUnreadableException() : base("Selection copied but the clipboard could not be read") { }
}

/// <summary>
/// Clipboard + synthetic Ctrl+C / Ctrl+V, the Windows port of the Mac's non-destructive paste.
/// Runs on the UI thread; waits are awaited, never blocking. The whole clipboard (every memory-based
/// format, so images and Office content survive too) is saved and restored.
/// </summary>
internal sealed class ClipboardPaster
{
    // 500 ms (the plan's first number) was too short: a hand holding the hotkey 600 ms made Ctrl+V arrive
    // as Ctrl+Alt+V (= AltGr+V, typed "√" in Notepad). Found in the VM with `vm.sh hold 600`.
    private static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(3);
    /// <summary>After the deadline a slow app may still deliver its Ctrl+C; the Mac keeps watching too.</summary>
    private static readonly TimeSpan LateCopyWindow = TimeSpan.FromMilliseconds(3000);
    /// <summary>A copy bumps the sequence number more than once (empty, then each format): let it settle.</summary>
    private static readonly TimeSpan LateCopySettle = TimeSpan.FromMilliseconds(100);
    /// <summary>Above this the snapshot falls back to text only, so a 200 MB image never sits in memory.</summary>
    private const long SnapshotCap = 64L * 1024 * 1024;
    private const uint CF_DIB = 8;

    private readonly IntPtr _owner;

    // The restore that is still waiting: the user's original clipboard, the sequence number our text got,
    // and the token that cancels it. A second paste inside the 3 s must not take our own text for
    // the user's clipboard, so it reuses this original and replaces the older restore.
    private ClipboardSnapshot? _pendingOriginal;
    private uint _pendingSequence;
    private CancellationTokenSource? _pendingRestore;

    // Sequence number after our last clipboard write (text or restore): a change to it is not a copy.
    private uint _lastOwnWriteSequence;

    // Late-copy watch (see CopySelectionAsync). Only touched on the UI thread.
    private CancellationTokenSource? _lateWatch;
    private uint _lateBaseline;
    private bool _lateCopyArrived;

    public ClipboardPaster(IntPtr clipboardOwner) => _owner = clipboardOwner;

    /// <summary>
    /// True when a Ctrl+C that missed the 2 s deadline arrived afterwards: there is a selection the
    /// recording never saw, so composing a new text over it would be wrong.
    /// </summary>
    public bool LateCopyArrived => _lateCopyArrived;

    /// <summary>Stops the late-copy watch and clears its flag (new recording, Esc, paste, retry).</summary>
    public void CancelLateWatch()
    {
        _lateWatch?.Cancel();
        _lateWatch = null;
        _lateCopyArrived = false;
    }

    /// <summary>The top-level window in front right now (the paste target Mac keeps as the front app).</summary>
    public static IntPtr ForegroundRoot()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hwnd, GA_ROOT);
        return root != IntPtr.Zero ? root : hwnd;
    }

    /// <summary>True when the window (or its owner chain root) belongs to this process.</summary>
    public static bool BelongsToThisProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return Core.ReplyGuards.IsOwnWindow(pid, (uint)Environment.ProcessId);
    }

    /// <summary>The foreground window belongs to this process (settings window or one of its dialogs).</summary>
    public static bool ForegroundIsOwnWindow() => BelongsToThisProcess(GetForegroundWindow());

    private static Core.ForegroundKind ClassifyForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return Core.ForegroundKind.None;
        if (BelongsToThisProcess(hwnd)) return Core.ForegroundKind.Own;
        var root = GetAncestor(hwnd, GA_ROOT);
        var name = new System.Text.StringBuilder(256);
        GetClassName(root != IntPtr.Zero ? root : hwnd, name, name.Capacity);
        return Core.PasteTargetPolicy.IsShellWindowClass(name.ToString())
            ? Core.ForegroundKind.Shell
            : Core.ForegroundKind.Foreign;
    }

    /// <summary>Leaves <paramref name="text"/> on the clipboard without pasting (the caller tells the user).</summary>
    public bool KeepOnClipboard(string text)
    {
        CancelLateWatch(); // our own write would look like a late copy
        return WriteText(text);
    }

    /// <summary>
    /// Puts <paramref name="text"/> at the cursor: save the clipboard, set ours, Ctrl+V, and after
    /// 3 s put the old clipboard back (unless <paramref name="restoreClipboard"/> is off), but only when nobody else wrote it in between.
    /// Pastes only into <paramref name="target"/> (the window in front when the recording began). It is brought
    /// back only when nothing, our own window or the taskbar is in front; with another window in front or the
    /// target closed the text stays on the clipboard. <c>IntPtr.Zero</c> means: no target to check.
    /// </summary>
    public async Task<PasteOutcome> PasteAsync(string text, IntPtr target, bool restoreClipboard = true, CancellationToken ct = default)
    {
        // The user may still hold Ctrl+Alt from the hotkey: Ctrl+V would arrive as Ctrl+Alt+V.
        bool released = await WaitForModifiersReleasedAsync(ct);
        ct.ThrowIfCancellationRequested(); // cancelled while waiting: leave the clipboard alone
        CancelLateWatch(); // our own write below would look like a late copy
        if (!released)
            return WriteText(text) ? PasteOutcome.ClipboardOnlyKeysHeld : PasteOutcome.Failed;

        bool hasTarget = target != IntPtr.Zero;
        var action = Core.PasteTargetPolicy.Decide(hasTarget, hasTarget && BelongsToThisProcess(target),
            hasTarget && IsWindow(target), hasTarget && IsIconic(target),
            hasTarget && ForegroundRoot() == target, ClassifyForeground());
        switch (action)
        {
            case Core.PasteTargetAction.ClipboardOnlyOwnWindow:
                Core.Log.Info("Paste: target is one of our own windows, leaving text on the clipboard");
                return WriteText(text) ? PasteOutcome.ClipboardOnlyOwnWindow : PasteOutcome.Failed;
            case Core.PasteTargetAction.ClipboardOnlyTargetGone:
                Core.Log.Info("Paste: the recording's window is gone, leaving text on the clipboard");
                return WriteText(text) ? PasteOutcome.ClipboardOnlyTargetGone : PasteOutcome.Failed;
            case Core.PasteTargetAction.ClipboardOnlyTargetChanged:
                Core.Log.Info("Paste: another window is in front (or the target is minimised), not taking focus back, leaving text on the clipboard");
                return WriteText(text) ? PasteOutcome.ClipboardOnlyTargetChanged : PasteOutcome.Failed;
            case Core.PasteTargetAction.ActivateTargetThenPaste:
            {
                // Windows may refuse (foreground lock); then the check below simply fails. No input tricks.
                bool asked = SetForegroundWindow(target);
                for (int i = 0; i < 10 && ForegroundRoot() != target; i++) await Task.Delay(20, ct);
                if (ForegroundRoot() != target)
                {
                    string why = asked ? "target did not come to the front" : "SetForegroundWindow refused";
                    Core.Log.Info($"Paste: could not bring the recording's window back ({why}), leaving text on the clipboard");
                    return WriteText(text) ? PasteOutcome.ClipboardOnlyTargetChanged : PasteOutcome.Failed;
                }
                Core.Log.Info("Paste: brought the recording's window back");
                break;
            }
        }

        if (ForegroundIsOwnWindow())
        {
            Core.Log.Info("Paste: foreground is one of our own windows, leaving text on the clipboard");
            return WriteText(text) ? PasteOutcome.ClipboardOnlyOwnWindow : PasteOutcome.Failed;
        }

        if (ForegroundIsElevated())
        {
            Core.Log.Info("Paste: foreground window runs elevated, leaving text on the clipboard");
            return WriteText(text) ? PasteOutcome.ClipboardOnlyElevatedTarget : PasteOutcome.Failed;
        }

        ClipboardSnapshot? original;
        if (_pendingRestore != null && GetClipboardSequenceNumber() == _pendingSequence)
        {
            // Still our earlier text on the clipboard: the user's own is the one saved back then.
            original = _pendingOriginal;
        }
        else
        {
            original = TrySnapshot();
        }
        if (!WriteText(text)) return PasteOutcome.Failed;
        uint ourSequence = GetClipboardSequenceNumber();
        _pendingRestore?.Cancel(); // only the latest paste restores
        var restore = new CancellationTokenSource();
        _pendingRestore = restore;
        _pendingOriginal = original;
        _pendingSequence = ourSequence;

        await Task.Delay(Core.ClipboardPasteGuard.PrePasteDelay, ct);
        // Something else may have written the clipboard meanwhile: Ctrl+V would paste that instead of our text.
        uint currentSequence = GetClipboardSequenceNumber();
        TryReadText(out var current);
        if (!Core.ClipboardPasteGuard.PasteAllowed(ourSequence, currentSequence, text, current))
        {
            Core.Log.Info("Paste: clipboard changed before Ctrl+V, not pasting");
            if (_pendingRestore == restore) { _pendingRestore = null; _pendingOriginal = null; }
            restore.Dispose();
            return PasteOutcome.ClipboardOnlyClipboardChanged;
        }
        SendChord(0x56); // V
        Core.Log.Info($"Paste: sent Ctrl+V ({text.Length} chars), restore {(restoreClipboard ? "on" : "off")}");

        if (restoreClipboard) _ = RestoreLaterAsync(original, ourSequence, restore);
        else
        {
            // No restore wanted: the user's old text stays gone, and a later paste must not bring it back.
            _pendingRestore = null;
            _pendingOriginal = null;
            restore.Dispose();
        }
        return PasteOutcome.Pasted;
    }

    /// <summary>Puts text on the clipboard without pasting (tray menu: "Letzten Text kopieren").</summary>
    public bool CopyToClipboard(string text)
    {
        CancelLateWatch();
        return WriteText(text);
    }

    // ---- Live diktieren ----

    // The user's clipboard from before a live dictation (only when "restore" is on); null = nothing to bring back.
    private ClipboardSnapshot? _liveOriginal;

    /// <summary>
    /// A live dictation starts: remembers the user's clipboard (the saved one when our earlier text is still on it)
    /// and drops any restore still pending, which would put the old clipboard back mid-dictation.
    /// </summary>
    public void BeginLive(bool restoreClipboard)
    {
        CancelLateWatch();
        ClipboardSnapshot? original = _pendingRestore != null && GetClipboardSequenceNumber() == _pendingSequence
            ? _pendingOriginal
            : TrySnapshot();
        _pendingRestore?.Cancel();
        _pendingRestore = null;
        _pendingOriginal = null;
        _liveOriginal = restoreClipboard ? original : null;
    }

    /// <summary>
    /// Pastes one live piece into <paramref name="target"/>. False = not inserted (hold the text): no target, keys still
    /// held, another or our own or an elevated window in front, or the clipboard write failed. Never brings a window
    /// forward and never types into another window.
    /// </summary>
    public async Task<bool> PasteLivePieceAsync(string text, IntPtr target, CancellationToken ct)
    {
        if (target == IntPtr.Zero) return false;
        if (!await WaitForModifiersReleasedAsync(ct)) return false;
        ct.ThrowIfCancellationRequested();
        if (ForegroundRoot() != target) return false;
        if (ForegroundIsOwnWindow()) return false;
        if (ForegroundIsElevated()) return false;
        CancelLateWatch(); // our own write below would look like a late copy
        if (!WriteText(text)) return false;
        await Task.Delay(Core.LiveDictation.PrePasteDelay, ct);
        // An Alt+Tab in those 50 ms must not send the piece elsewhere.
        if (ForegroundRoot() != target) return false;
        // Someone replaced the clipboard in those 50 ms (the user's copy, or the end of an Esc restore): Ctrl+V would paste that.
        if (GetClipboardSequenceNumber() != _lastOwnWriteSequence) return false;
        SendChord(0x56); // V
        Core.Log.Info($"LIVE-DICTATE: inserted {text.Length} chars");
        await Task.Delay(Core.LiveDictation.PieceGap, ct);
        return true;
    }

    /// <summary>Ends a live dictation and settles the clipboard as <paramref name="action"/> says. Always forgets the saved original.</summary>
    public void EndLive(Core.LiveClipboardEnd action, string? text)
    {
        try
        {
            switch (action)
            {
                case Core.LiveClipboardEnd.RestoreBefore:
                    if (_liveOriginal != null)
                    {
                        if (GetClipboardSequenceNumber() == _lastOwnWriteSequence)
                            Core.Log.Info(Restore(_liveOriginal) ? "LIVE-DICTATE: restored previous clipboard" : "LIVE-DICTATE: restore failed");
                        else
                            Core.Log.Info("LIVE-DICTATE: restore skipped, clipboard changed since our last write");
                    }
                    break;
                case Core.LiveClipboardEnd.KeepText:
                case Core.LiveClipboardEnd.KeepFallback:
                    CancelLateWatch();
                    WriteText(text ?? "");
                    break;
                case Core.LiveClipboardEnd.KeepHeld:
                    CancelLateWatch();
                    WriteText((text ?? "").Trim(' '));
                    break;
                case Core.LiveClipboardEnd.KeepTextThenClear:
                    if (!string.IsNullOrEmpty(text))
                    {
                        CancelLateWatch();
                        WriteText(text);
                    }
                    ClearLaterIfStillOurs();
                    break;
                case Core.LiveClipboardEnd.LeaveAsIs:
                    break;
            }
        }
        finally
        {
            _liveOriginal = null;
        }
    }

    private void ClearLaterIfStillOurs()
    {
        uint ours = _lastOwnWriteSequence;
        _ = ClearLaterAsync(ours);
    }

    private async Task ClearLaterAsync(uint ours)
    {
        try
        {
            await Task.Delay(Core.ClipboardPasteGuard.RestoreDelay);
            if (GetClipboardSequenceNumber() != ours) return;
            if (Clear()) Core.Log.Info("LIVE-DICTATE: cleared clipboard after last piece");
        }
        catch (Exception e)
        {
            Core.Log.Warn($"LIVE-DICTATE: clearing the clipboard failed ({e.GetType().Name})");
        }
    }

    private async Task RestoreLaterAsync(ClipboardSnapshot? previous, uint ourSequence, CancellationTokenSource self)
    {
        try
        {
            try { await Task.Delay(Core.ClipboardPasteGuard.RestoreDelay, self.Token); }
            catch (OperationCanceledException) { return; } // a newer paste took over
            if (previous == null) return; // the clipboard was empty before; nothing of the user's to bring back
            if (!Core.ClipboardPasteGuard.RestoreAllowed(ourSequence, GetClipboardSequenceNumber()))
            {
                Core.Log.Info("Paste: clipboard changed by someone else, not restoring");
                return;
            }
            Core.Log.Info(Restore(previous) ? "Paste: restored previous clipboard" : "Paste: restore failed");
            // Our own restore is no late copy: move the watch's baseline along.
            if (_lateWatch != null) _lateBaseline = GetClipboardSequenceNumber();
        }
        finally
        {
            if (_pendingRestore == self) _pendingRestore = null;
            self.Dispose();
        }
    }

    /// <summary>
    /// Copies the current selection with Ctrl+C and returns it, or null when nothing was selected.
    /// Only a changed clipboard sequence number counts: an unchanged clipboard still holds an old copy
    /// that has nothing to do with this recording. The user's clipboard is put back afterwards.
    /// Throws <see cref="SelectionUnreadableException"/> when the clipboard changed but stayed unreadable:
    /// "no selection" would let a new text overwrite the marked one. Cancelling (Esc) sends no Ctrl+C
    /// when it comes before the keystroke, and puts the clipboard back when it comes after.
    /// After the deadline it keeps watching for 3 s (<see cref="LateCopyArrived"/>), like the Mac.
    /// </summary>
    public async Task<string?> CopySelectionAsync(CancellationToken ct = default)
    {
        CancelLateWatch(); // a watch from an earlier recording is obsolete
        if (!await WaitForModifiersReleasedAsync(ct)) return null;
        ct.ThrowIfCancellationRequested();
        if (ForegroundIsElevated())
        {
            Core.Log.Info("Copy: foreground window runs elevated, treating as no selection");
            return null;
        }

        // A paste restore still waiting means the clipboard holds our own pasted text: the user's is the saved one.
        ClipboardSnapshot? previous;
        if (_pendingRestore != null && GetClipboardSequenceNumber() == _pendingSequence)
        {
            previous = _pendingOriginal;
            Core.Log.Info("Copy: paste restore still pending, using the saved clipboard as the user's");
        }
        else
        {
            previous = TrySnapshot();
        }
        uint before = GetClipboardSequenceNumber();
        SendChord(0x43); // C

        var started = DateTime.UtcNow;
        TimeSpan? changedAt = null;
        try
        {
            while (true)
            {
                await Task.Delay(15, ct);
                var elapsed = DateTime.UtcNow - started;
                bool changed = Core.SelectionCopyPolicy.CopyChanged(before, GetClipboardSequenceNumber(), _lastOwnWriteSequence);
                string? copied = null;
                bool readable = changed && TryReadText(out copied);
                if (changed && changedAt == null) changedAt = elapsed;
                var r = Core.SelectionCopyPolicy.Poll(changed, readable, elapsed, changedAt);
                if (r == Core.CopyPollResult.Wait) continue;
                if (r == Core.CopyPollResult.NoSelection) break;
                if (previous != null) Restore(previous);
                if (r == Core.CopyPollResult.Unreadable)
                {
                    Core.Log.Error("Copy: clipboard changed but could not be read");
                    throw new SelectionUnreadableException();
                }
                var trimmed = (copied ?? "").Trim();
                Core.Log.Info($"Copy: selection {(trimmed.Length == 0 ? "empty" : trimmed.Length + " chars")}");
                return trimmed.Length == 0 ? null : trimmed;
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is out already: the selection may have replaced the user's clipboard.
            if (previous != null && Core.SelectionCopyPolicy.CopyChanged(before, GetClipboardSequenceNumber(), _lastOwnWriteSequence)) Restore(previous);
            throw;
        }
        Core.Log.Info("Copy: clipboard unchanged after 2000 ms, no selection");
        var watch = new CancellationTokenSource();
        _lateWatch = watch;
        _lateBaseline = before;
        _ = WatchLateCopyAsync(previous, watch);
        return null;
    }

    /// <summary>
    /// 3 s more after the deadline: a slow app may still answer our Ctrl+C. Then the user's clipboard goes
    /// back (the copy is not wanted as clipboard content), and <see cref="LateCopyArrived"/> tells the
    /// caller not to compose over a selection it never saw.
    /// </summary>
    private async Task WatchLateCopyAsync(ClipboardSnapshot? previous, CancellationTokenSource self)
    {
        try
        {
            var end = DateTime.UtcNow + LateCopyWindow;
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(30, self.Token);
                if (!Core.SelectionCopyPolicy.CopyChanged(_lateBaseline, GetClipboardSequenceNumber(), _lastOwnWriteSequence)) continue;
                _lateCopyArrived = true;
                await Task.Delay(LateCopySettle, self.Token);
                if (previous == null)
                {
                    Core.Log.Info("Copy: late clipboard change after deadline, nothing to restore");
                    return;
                }
                Core.Log.Info(Restore(previous)
                    ? "Copy: late clipboard change after deadline, restored"
                    : "Copy: late clipboard change after deadline, restore failed");
                return;
            }
        }
        catch (OperationCanceledException) { /* a new recording, Esc or a paste took over */ }
        finally
        {
            if (_lateWatch == self) _lateWatch = null;
            self.Dispose();
        }
    }

    /// <summary>
    /// Screenshot hotkey: <paramref name="launch"/> opens the Windows snip UI, which delivers its image through the
    /// clipboard. Waits for it (see <see cref="Core.SnipWatch"/>) and returns the raw image: the "PNG" format if
    /// offered, else CF_DIB (<c>IsPng</c> false). Null on cancel or timeout. After a capture the image stays on the
    /// clipboard, so Ctrl+V pastes it anywhere (Mac: the screenshot goes to the pasteboard). Cancelling with the
    /// hotkey just as it arrives puts the user's clipboard back; a cancel in the snip UI leaves the clipboard alone.
    /// </summary>
    public async Task<(byte[] Data, bool IsPng)?> CaptureSnipAsync(Func<bool> launch, Func<bool> snipUiRunning, CancellationToken ct)
    {
        CancelLateWatch();
        // A paste restore still waiting: the clipboard holds our pasted text, the user's is the saved one.
        ClipboardSnapshot? previous = _pendingRestore != null && GetClipboardSequenceNumber() == _pendingSequence
            ? _pendingOriginal : TrySnapshot();
        uint before = GetClipboardSequenceNumber();
        if (!launch()) return null;

        uint png = RegisterClipboardFormat("PNG");
        var watch = new Core.SnipWatch();
        var started = DateTime.UtcNow;
        bool uiRunning = false;
        for (int tick = 0; ; tick++)
        {
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException)
            {
                // Cancelled just as the snip arrived: the image must not stay on the clipboard.
                if (GetClipboardSequenceNumber() != before && (IsClipboardFormatAvailable(png) || IsClipboardFormatAvailable(CF_DIB)))
                    _ = previous != null ? Restore(previous) : Clear();
                throw;
            }
            // Enumerating processes is not free: twice a second is enough for the cancel check.
            if (tick % 5 == 0) uiRunning = snipUiRunning();
            bool changed = GetClipboardSequenceNumber() != before;
            bool hasImage = changed && (IsClipboardFormatAvailable(png) || IsClipboardFormatAvailable(CF_DIB));
            var step = watch.Next(DateTime.UtcNow - started, changed, hasImage, uiRunning);
            switch (step)
            {
                case Core.SnipStep.Waiting:
                    continue;
                case Core.SnipStep.Captured:
                    // The snip writes its formats one after another: let it finish (no cancel here, the clipboard must be cleaned up).
                    await Task.Delay(250, CancellationToken.None);
                    var image = TryReadImage(png);
                    Core.Log.Info($"Snip: {(image == null ? "image unreadable" : $"{(image.Value.IsPng ? "PNG" : "DIB")} {image.Value.Data.Length / 1024} KB")}, "
                        + "left on the clipboard");
                    return image;
                default:
                    Core.Log.Info($"Snip: no image ({step}), clipboard left alone");
                    return null;
            }
        }
    }

    private (byte[] Data, bool IsPng)? TryReadImage(uint png)
    {
        if (!Open()) return null;
        try
        {
            foreach (var (format, isPng) in new[] { (png, true), (CF_DIB, false) })
            {
                IntPtr handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;
                long size = (long)GlobalSize(handle);
                if (size <= 0 || size > SnapshotCap) continue;
                var snap = new ClipboardSnapshot();
                CopyFormat(snap, format, handle, size);
                if (snap.Items.Count == 1) return (snap.Items[0].Data, isPng);
            }
            return null;
        }
        finally { CloseClipboard(); }
    }

    private bool Clear()
    {
        if (!Open()) return false;
        try { return EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    // ---- keyboard ----

    private static readonly int[] Modifiers =
        { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU };

    private static bool AnyModifierDown() => Modifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>
    /// Waits (max 3 s) until Ctrl, Alt, AltGr, Shift and both Win keys are up; false when they never were.
    /// We never send an Alt key-up ourselves: a lone Alt release opens the menu bar in Word and Notepad.
    /// </summary>
    private static async Task<bool> WaitForModifiersReleasedAsync(CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        while (AnyModifierDown())
        {
            if (DateTime.UtcNow - start >= ModifierWait)
            {
                Core.Log.Info("Modifiers still held after 3 s, not sending keystrokes: "
                    + string.Join(",", Modifiers.Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0).Select(vk => $"0x{vk:X2}")));
                return false;
            }
            await Task.Delay(10, ct);
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

    /// <summary>Everything that was on the clipboard: format id plus the raw bytes of its memory block.</summary>
    private sealed class ClipboardSnapshot
    {
        public readonly List<(uint Format, byte[] Data)> Items = new();
    }

    /// <summary>
    /// Copies the memory formats ClipboardFormats.ToSnapshot keeps. Null when the clipboard is empty or
    /// cannot be opened (nothing to bring back). Above 64 MB only the text is kept. The log names format
    /// ids and sizes, never content.
    /// </summary>
    private ClipboardSnapshot? TrySnapshot()
    {
        if (!Open()) return null;
        try
        {
            var available = new List<uint>();
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
                available.Add(format);

            var snap = new ClipboardSnapshot();
            long total = 0;
            var seen = new List<(uint Format, long Size)>();
            foreach (uint format in Core.ClipboardFormats.ToSnapshot(available))
            {
                IntPtr handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;
                long size = (long)GlobalSize(handle);
                if (size <= 0) continue; // not a memory block after all
                total += size;
                seen.Add((format, size));
                if (total > SnapshotCap)
                {
                    string sizes = string.Join(", ", seen.Select(s =>
                        $"{s.Format}: {(s.Size / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB"));
                    Core.Log.Info($"Clipboard snapshot above {SnapshotCap / (1024 * 1024)} MB ({sizes}), keeping text only");
                    snap.Items.Clear();
                    IntPtr text = GetClipboardData(CF_UNICODETEXT);
                    long textSize = text == IntPtr.Zero ? 0 : (long)GlobalSize(text);
                    if (textSize > 0 && textSize <= SnapshotCap) CopyFormat(snap, CF_UNICODETEXT, text, textSize);
                    return snap.Items.Count > 0 ? snap : null;
                }
                CopyFormat(snap, format, handle, size);
            }
            return snap.Items.Count > 0 ? snap : null;
        }
        finally { CloseClipboard(); }
    }

    private static void CopyFormat(ClipboardSnapshot snap, uint format, IntPtr handle, long size)
    {
        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            snap.Items.Add((format, bytes));
        }
        finally { GlobalUnlock(handle); }
    }

    /// <summary>
    /// Writes the snapshot back in one session. No history/cloud markers here: it is the user's own data
    /// and may belong in Win+V again.
    /// </summary>
    private bool Restore(ClipboardSnapshot snap)
    {
        bool ok = RestoreCore(snap);
        // Read after CloseClipboard so the number is the one our write ended on.
        if (ok) _lastOwnWriteSequence = GetClipboardSequenceNumber();
        return ok;
    }

    private bool RestoreCore(ClipboardSnapshot snap)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            int written = 0;
            foreach (var (format, data) in snap.Items)
            {
                IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
                if (handle == IntPtr.Zero) continue;
                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) { GlobalFree(handle); continue; }
                Marshal.Copy(data, 0, ptr, data.Length);
                GlobalUnlock(handle);
                if (SetClipboardData(format, handle) == IntPtr.Zero) GlobalFree(handle);
                else written++;
            }
            return written > 0;
        }
        finally { CloseClipboard(); }
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
        bool ok = WriteTextCore(text);
        // Read after CloseClipboard so the number is the one our write ended on.
        if (ok) _lastOwnWriteSequence = GetClipboardSequenceNumber();
        return ok;
    }

    private bool WriteTextCore(string text)
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
            // Keep dictations and copied selections out of the Win+V history and the cloud clipboard.
            SetDword("CanIncludeInClipboardHistory", 0);
            SetDword("CanUploadToCloudClipboard", 0);
            return true; // the system owns the memory now
        }
        finally { CloseClipboard(); }
    }

    private static void SetDword(string format, int value)
    {
        uint id = RegisterClipboardFormat(format);
        if (id == 0) return;
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4);
        if (h == IntPtr.Zero) return;
        Marshal.WriteInt32(GlobalLock(h), value);
        GlobalUnlock(h);
        if (SetClipboardData(id, h) == IntPtr.Zero) GlobalFree(h);
    }
}
