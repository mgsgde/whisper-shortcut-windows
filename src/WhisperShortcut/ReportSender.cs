using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WhisperShortcut;

/// <summary>
/// Hands a report file to the user's mail program (Mac ReportSender). The Mac attaches the file through Apple Mail's
/// compose service and otherwise opens <c>mailto:</c> and shows the file selected in Finder. Windows does the same with
/// Simple MAPI, which classic Outlook, Thunderbird and most desktop mail programs offer: a compose window with the file
/// attached. When the program that opens mail links is not that MAPI client (the new Outlook, Gmail in the browser, no
/// mail program at all), Windows falls back like the Mac: <c>mailto:</c>, the file selected in Explorer and on the
/// clipboard, so Strg+V in the mail attaches it.
/// </summary>
internal static class ReportSender
{
    /// <summary>
    /// True when Simple MAPI reaches the program the user writes mail with: the MAPI client registered under
    /// Software\Clients\Mail runs the same executable as the mailto: handler. Pure, for the tests.
    /// </summary>
    public static bool PrefersMapi(string? mailtoExecutable, string? mapiClientExecutable) =>
        !string.IsNullOrWhiteSpace(mailtoExecutable) && !string.IsNullOrWhiteSpace(mapiClientExecutable)
        && string.Equals(Path.GetFileName(mailtoExecutable.Trim().Trim('"')), Path.GetFileName(mapiClientExecutable.Trim().Trim('"')),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a compose window to <see cref="Core.FeedbackLinks.SupportEmail"/> with <paramref name="body"/> and the file
    /// attached. False when no MAPI mail program matches the mail handler or the call failed at once; the caller then
    /// falls back. Runs MAPI on its own STA thread: some clients block the call until their compose window closes.
    /// </summary>
    public static bool TryComposeWithAttachment(string subject, string body, string file)
    {
        var handler = MailtoExecutable();
        var client = MapiClientExecutable();
        if (!PrefersMapi(handler, client))
        {
            Core.Log.Info($"REPORT: no MAPI compose (mailto: {Path.GetFileName(handler ?? "-")}, MAPI client: {Path.GetFileName(client ?? "-")})");
            return false;
        }
        uint? result = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { result = SendMail(subject, body, file); }
            catch (Exception e) { Core.Log.Warn($"REPORT: MAPI call failed ({e.GetType().Name})"); result = MapiFailure; }
            finally { done.Set(); }
        }) { IsBackground = true, Name = "MAPI compose" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A compose window that is open and waiting for the user keeps the call running: that is success. An error
        // comes back within a moment (no profile, no attachment support).
        if (!done.Wait(TimeSpan.FromSeconds(4)))
        {
            Core.Log.Info($"REPORT: MAPI compose open in {Path.GetFileName(client)}");
            return true;
        }
        Core.Log.Info($"REPORT: MAPI returned {result}");
        return result is MapiSuccess or MapiUserAbort;
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

    /// <summary>Puts the file itself on the clipboard, as Explorer's Copy does: Strg+V in a mail or chat attaches it.</summary>
    public static bool CopyFileToClipboard(string file)
    {
        try
        {
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
            return true;
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: file not on the clipboard ({e.GetType().Name})");
            return false;
        }
    }

    /// <summary>An Explorer window on the report folder with the file selected.</summary>
    public static void RevealInExplorer(string file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: Explorer did not open ({e.GetType().Name})");
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

    /// <summary>The executable of the default Simple MAPI client (HKCU, then HKLM Software\Clients\Mail), or null.</summary>
    private static string? MapiClientExecutable()
    {
        try
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var clients = root.OpenSubKey(@"Software\Clients\Mail");
                if (clients?.GetValue(null) is not string name || name.Length == 0) continue;
                using var client = Registry.LocalMachine.OpenSubKey($@"Software\Clients\Mail\{name}")
                    ?? root.OpenSubKey($@"Software\Clients\Mail\{name}");
                if (client == null || client.GetValue("DLLPath") is not string dll || dll.Length == 0) continue;
                using var command = client.OpenSubKey(@"shell\open\command");
                if (command?.GetValue(null) is string cmd && ExecutableOf(cmd) is { } exe) return exe;
            }
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: MAPI client unknown ({e.GetType().Name})");
        }
        return null;
    }

    /// <summary>The program path at the start of a command line ("C:\x\a.exe" /y or C:\x\a.exe /y).</summary>
    internal static string? ExecutableOf(string commandLine)
    {
        var c = commandLine.Trim();
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    // ---- Simple MAPI ----------------------------------------------------------------------------------------------

    private const uint MapiSuccess = 0, MapiUserAbort = 1, MapiFailure = 2;
    private const uint MapiLogonUi = 0x1, MapiDialog = 0x8, MapiTo = 1;
    private const uint AssocfIsProtocol = 0x1000, AssocstrExecutable = 2;

    private static uint SendMail(string subject, string body, string file)
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
            var attach = new MapiFileDescW
            {
                nPosition = uint.MaxValue,
                lpszPathName = Str(file),
                lpszFileName = Str(Path.GetFileName(file)),
            };
            var recipPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipDescW>());
            allocated.Add(recipPtr);
            Marshal.StructureToPtr(recip, recipPtr, false);
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDescW>());
            allocated.Add(filePtr);
            Marshal.StructureToPtr(attach, filePtr, false);
            var message = new MapiMessageW
            {
                lpszSubject = Str(subject),
                lpszNoteText = Str(body.Replace("\r\n", "\n").Replace("\n", "\r\n")),
                nRecipCount = 1,
                lpRecips = recipPtr,
                nFileCount = 1,
                lpFiles = filePtr,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiFileDescW
    {
        public uint ulReserved, flFlags, nPosition;
        public IntPtr lpszPathName, lpszFileName, lpFileType;
    }

    [DllImport("mapi32.dll", ExactSpelling = true)]
    private static extern uint MAPISendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint str, string pszAssoc, string? pszExtra, System.Text.StringBuilder pszOut, ref uint pcchOut);
}
