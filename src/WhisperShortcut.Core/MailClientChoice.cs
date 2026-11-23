using System.Runtime.InteropServices;

namespace WhisperShortcut.Core;

/// <summary>
/// Whether „Problem melden“ can hand its report to the mail program through Simple MAPI (Mac: only Apple Mail can take the
/// file). Pure parts of the decision, so the tests can check them without a mail program installed.
/// </summary>
public static class MailClientChoice
{
    /// <summary>
    /// True when Simple MAPI reaches the program the user writes mail with: the MAPI client registered under
    /// Software\Clients\Mail runs the same executable as the mailto: handler (classic Outlook, Thunderbird). The new
    /// Outlook, Gmail in a browser or no mail program at all give false: those get mailto: and the file in Explorer.
    /// </summary>
    public static bool PrefersMapi(string? mailtoExecutable, string? mapiClientExecutable) =>
        !string.IsNullOrWhiteSpace(mailtoExecutable) && !string.IsNullOrWhiteSpace(mapiClientExecutable)
        && string.Equals(Path.GetFileName(mailtoExecutable.Trim().Trim('"')), Path.GetFileName(mapiClientExecutable.Trim().Trim('"')),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The program path at the start of a command line (<c>"C:\x\a.exe" /y</c> or <c>C:\x\a.exe /y</c>), or null.</summary>
    public static string? ExecutableOf(string? commandLine)
    {
        var c = (commandLine ?? "").Trim();
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>
    /// True when a DLL built for <paramref name="peMachine"/> loads into a process of <paramref name="process"/>
    /// architecture. A 32-bit Outlook's MAPI DLL does not load into the 64-bit app: the call would fail or hang in the
    /// system's "no mail client" message box, so such a client takes the mailto: route.
    /// </summary>
    public static bool MachineMatches(ushort peMachine, Architecture process) => process switch
    {
        Architecture.X64 => peMachine == 0x8664,
        Architecture.Arm64 => peMachine is 0xAA64 or 0xA641, // ARM64 and ARM64EC/ARM64X
        Architecture.X86 => peMachine == 0x14C,
        _ => false,
    };

    /// <summary>The Machine field of a PE file's COFF header, or null when the file is missing or not a PE image.</summary>
    public static ushort? PeMachine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(fs);
            if (fs.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return null; // "MZ"
            fs.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 6) return null;
            fs.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
            return reader.ReadUInt16();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
