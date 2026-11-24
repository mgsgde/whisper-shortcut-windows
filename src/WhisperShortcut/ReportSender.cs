using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WhisperShortcut;

/// <summary>
/// Hands the report text to the user's mail program (Mac ReportSender). Simple MAPI, which classic Outlook, Thunderbird
/// and most desktop mail programs offer, opens a compose window with the whole report as the mail's text. When the
/// program that opens mail links is not that MAPI client (the new Outlook, Gmail in the browser, no mail program at all),
/// the report goes on the clipboard and <c>mailto:</c> opens a mail that says to paste it with Strg+V.
/// </summary>
internal static class ReportSender
{
    /// <summary>What became of a MAPI compose request.</summary>
    public enum ComposeOutcome
    {
        /// <summary>No matching MAPI mail program, or it failed at once: use the mailto: route.</summary>
        NotPossible,
        /// <summary>The compose window is open (or the call returned success).</summary>
        Open,
        /// <summary>The call still runs after the wait: most likely the compose window waiting for the user (classic Outlook).</summary>
        Pending,
    }

    /// <summary>
    /// Asks the mail program for a compose window to <see cref="Core.FeedbackLinks.SupportEmail"/> with <paramref name="body"/>
    /// as its text. Never blocks the caller: the UI thread carries the app's keyboard hook, and Windows drops a
    /// hook that does not answer. MAPI runs on its own STA thread; some clients (classic Outlook) keep the call open until
    /// their compose window closes, so after a short wait the answer is <see cref="ComposeOutcome.Pending"/>. Should the call
    /// later end in an error, <paramref name="lateFailure"/> runs (on the MAPI thread) so the caller can still fall back.
    /// </summary>
    public static async Task<ComposeOutcome> ComposeAsync(string subject, string body, Action lateFailure)
    {
        var client = await Task.Run(() =>
        {
            var handler = MailtoExecutable();
            var (exe, dll) = MapiClient();
            if (!Core.MailClientChoice.PrefersMapi(handler, exe))
            {
                Core.Log.Info($"REPORT: no MAPI compose (mailto: {Path.GetFileName(handler ?? "-")}, MAPI client: {Path.GetFileName(exe ?? "-")})");
                return null;
            }
            // A 32-bit Office does not load into this 64-bit process: the call would fail or hang in a system message box.
            if (dll != null && Core.MailClientChoice.PeMachine(dll) is { } machine
                && !Core.MailClientChoice.MachineMatches(machine, RuntimeInformation.ProcessArchitecture))
            {
                Core.Log.Info($"REPORT: MAPI client {Path.GetFileName(exe)} is built for 0x{machine:X4}, not {RuntimeInformation.ProcessArchitecture}");
                return null;
            }
            return exe;
        });
        if (client == null) return ComposeOutcome.NotPossible;

        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool answered = false;
        var gate = new object();
        var thread = new Thread(() =>
        {
            uint result;
            try { result = SendMail(subject, body); }
            catch (Exception e) { Core.Log.Warn($"REPORT: MAPI call failed ({e.GetType().Name})"); result = MapiFailure; }
            Core.Log.Info($"REPORT: MAPI returned {result}");
            bool late;
            lock (gate) { late = answered; answered = true; }
            if (late && result is not (MapiSuccess or MapiUserAbort)) lateFailure();
            done.TrySetResult(result);
        }) { IsBackground = true, Name = "MAPI compose" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // An error comes back within a moment (no profile); an open compose window keeps waiting.
        var first = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(4)));
        lock (gate)
        {
            if (first != done.Task && !answered)
            {
                answered = true;
                Core.Log.Info($"REPORT: MAPI compose still open in {Path.GetFileName(client)}");
                return ComposeOutcome.Pending;
            }
            answered = true;
        }
        return done.Task.Result is MapiSuccess or MapiUserAbort ? ComposeOutcome.Open : ComposeOutcome.NotPossible;
    }

    /// <summary>Opens the mail program with a <c>mailto:</c> link. False when Windows found no handler.</summary>
    public static bool OpenMailto(Uri url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url.AbsoluteUri, UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: mailto did not open ({e.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Puts the report on the clipboard for Strg+V in the mail. Marked so Windows keeps it out of the clipboard history
    /// (Win+V) and the cloud clipboard: a report can quote a dictation with a patient's name.
    /// </summary>
    public static bool CopyText(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"), TextDataFormat.UnicodeText);
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
            return true;
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: text not on the clipboard ({e.GetType().Name})");
            return false;
        }
    }

    // ---- registry: which program handles mail ---------------------------------------------------------------------

    /// <summary>The executable that opens mailto: links (the user's choice in Windows Settings → Default apps).</summary>
    private static string? MailtoExecutable()
    {
        try
        {
            uint size = 1024;
            var sb = new System.Text.StringBuilder((int)size);
            int hr = AssocQueryString(AssocfIsProtocol, AssocstrExecutable, "mailto", "open", sb, ref size);
            return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: mailto handler unknown ({e.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// The default Simple MAPI client (HKCU, then HKLM Software\Clients\Mail): its executable and its MAPI DLL
    /// (DLLPathEx before DLLPath, as the system's MAPI stub prefers it), or nulls.
    /// </summary>
    private static (string? Exe, string? Dll) MapiClient()
    {
        try
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var clients = root.OpenSubKey(@"Software\Clients\Mail");
                if (clients?.GetValue(null) is not string name || name.Length == 0) continue;
                using var client = Registry.LocalMachine.OpenSubKey($@"Software\Clients\Mail\{name}")
                    ?? root.OpenSubKey($@"Software\Clients\Mail\{name}");
                if (client == null) continue;
                var dll = (client.GetValue("DLLPathEx") as string) is { Length: > 0 } ex ? ex : client.GetValue("DLLPath") as string;
                if (string.IsNullOrEmpty(dll)) continue;
                using var command = client.OpenSubKey(@"shell\open\command");
                if (command?.GetValue(null) is string cmd && Core.MailClientChoice.ExecutableOf(cmd) is { } exe)
                    return (exe, Environment.ExpandEnvironmentVariables(dll));
            }
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: MAPI client unknown ({e.GetType().Name})");
        }
        return (null, null);
    }

    // ---- Simple MAPI ----------------------------------------------------------------------------------------------

    private const uint MapiSuccess = 0, MapiUserAbort = 1, MapiFailure = 2;
    private const uint MapiLogonUi = 0x1, MapiDialog = 0x8, MapiTo = 1;
    private const uint AssocfIsProtocol = 0x1000, AssocstrExecutable = 2;

    private static uint SendMail(string subject, string body)
    {
        var allocated = new List<IntPtr>();
        IntPtr Str(string s) { var p = Marshal.StringToHGlobalUni(s); allocated.Add(p); return p; }
        try
        {
            var recip = new MapiRecipDescW
            {
                ulRecipClass = MapiTo,
                lpszName = Str(Core.FeedbackLinks.SupportEmail),
                lpszAddress = Str("SMTP:" + Core.FeedbackLinks.SupportEmail),
            };
            var recipPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipDescW>());
            allocated.Add(recipPtr);
            Marshal.StructureToPtr(recip, recipPtr, false);
            var message = new MapiMessageW
            {
                lpszSubject = Str(subject),
                lpszNoteText = Str(body.Replace("\r\n", "\n").Replace("\n", "\r\n")),
                nRecipCount = 1,
                lpRecips = recipPtr,
            };
            return MAPISendMailW(IntPtr.Zero, IntPtr.Zero, ref message, MapiLogonUi | MapiDialog, 0);
        }
        finally
        {
            foreach (var p in allocated) Marshal.FreeHGlobal(p);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiMessageW
    {
        public uint ulReserved;
        public IntPtr lpszSubject, lpszNoteText, lpszMessageType, lpszDateReceived, lpszConversationID;
        public uint flFlags;
        public IntPtr lpOriginator;
        public uint nRecipCount;
        public IntPtr lpRecips;
        public uint nFileCount;
        public IntPtr lpFiles;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiRecipDescW
    {
        public uint ulReserved, ulRecipClass;
        public IntPtr lpszName, lpszAddress;
        public uint ulEIDSize;
        public IntPtr lpEntryID;
    }

    [DllImport("mapi32.dll", ExactSpelling = true)]
    private static extern uint MAPISendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint str, string pszAssoc, string? pszExtra, System.Text.StringBuilder pszOut, ref uint pcchOut);
}
