using System.Globalization;
using System.Text.Json;

namespace WhisperShortcut.Core;

/// <summary>
/// Who this process is: the normal "WhisperShortcut" or the second, separate "WhisperShortcut Dev" instance (the Mac's
/// "WhisperShortcut Dev" build). It is the same published exe; Dev is switched on at runtime by a marker file
/// <see cref="MarkerFileName"/> next to the exe. Every per-instance name and path lives here, so nothing else
/// spells "WhisperShortcut" in a folder, mutex, event or registry name.
/// <para>Dev gets its own settings, log, history, chats, meetings, mutex and events, autostart value and tray icon.
/// The <b>offline models folder is shared</b> (<see cref="OfflineRoot"/>) so the two do not download 7 GB twice.</para>
/// </summary>
public sealed record AppIdentity(bool IsDev, string RoamingRoot, string LocalRoot)
{
    /// <summary>Marker file next to the exe (<c>AppContext.BaseDirectory</c>; the single-file publish): its presence makes the exe the Dev instance.</summary>
    public const string MarkerFileName = "dev-instance.txt";

    /// <summary>File in <see cref="LocalFolder"/> while a meeting records: <c>{ "pid": 123, "started": "2026-11-23T10:00:00.0000000Z" }</c>.
    /// The Dev update script reads it: a live pid means "do not replace the app now" (the Mac's live-meeting-recording.json).</summary>
    public const string LiveMeetingMarkerFileName = "live-meeting-recording.json";

    /// <summary>Why deleting offline models is refused in Dev.</summary>
    public const string OfflineDeleteLockedReason =
        "In der Dev-Version gesperrt: Die normale App nutzt dieselben Offline-Modelle.";

    private const string NormalName = "WhisperShortcut";
    private const string DevName = "WhisperShortcut Dev";

    private static AppIdentity? _current;

    /// <summary>The identity of this process: detected once from the marker file, unless a test replaced it.</summary>
    public static AppIdentity Current
    {
        get => _current ??= Detect();
        set => _current = value;
    }

    /// <summary>Back to the detected identity (tests).</summary>
    public static void ResetCurrent() => _current = null;

    /// <summary>True when <see cref="MarkerFileName"/> lies in <paramref name="exeDirectory"/>.</summary>
    public static bool MarkerExists(string exeDirectory)
    {
        try { return File.Exists(Path.Combine(exeDirectory, MarkerFileName)); }
        catch (Exception) { return false; }
    }

    private static AppIdentity Detect() => new(
        MarkerExists(AppContext.BaseDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>"WhisperShortcut" / "WhisperShortcut Dev": window titles, tray tooltip, messages.</summary>
    public string DisplayName => IsDev ? DevName : NormalName;

    /// <summary>Folder name below %APPDATA% and %LOCALAPPDATA% (same as <see cref="DisplayName"/>).</summary>
    public string FolderName => DisplayName;

    /// <summary>Single-instance mutex. Dev: <c>Local\WhisperShortcut.Windows.Dev</c>.</summary>
    public string MutexName => @"Local\WhisperShortcut.Windows" + (IsDev ? ".Dev" : "");

    public string ShowEventName => MutexName + ".Show";

    public string QuitEventName => MutexName + ".Quit";

    /// <summary>The same roots with the other identity.</summary>
    public AppIdentity Other => this with { IsDev = !IsDev };

    /// <summary>The Quit event of the other instance (<c>--takeover</c> signals it).</summary>
    public string OtherQuitEventName => Other.QuitEventName;

    /// <summary>HKCU Run value name. Dev: "WhisperShortcut Dev".</summary>
    public string AutoStartValueName => DisplayName;

    /// <summary>%APPDATA%\WhisperShortcut[ Dev]: settings, history, chats, MCP files.</summary>
    public string SettingsFolder => Path.Combine(RoamingRoot, FolderName);

    public string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    /// <summary>%LOCALAPPDATA%\WhisperShortcut[ Dev]: log, meetings, unfinished recording, staging.</summary>
    public string LocalFolder => Path.Combine(LocalRoot, FolderName);

    public string LogPath => Path.Combine(LocalFolder, "wsw.log");

    /// <summary>The offline models. <b>Always the normal app's folder</b>, so both instances share the downloads.</summary>
    public string OfflineRoot => Path.Combine(LocalRoot, NormalName, "Offline");

    /// <summary>%LOCALAPPDATA%\Programs\WhisperShortcut[ Dev]: where this instance is meant to live.</summary>
    public string InstallDir => InstallLocation.CanonicalDir(LocalRoot, IsDev);

    public string InstallExe => Path.Combine(InstallDir, UpdateInstallPath.ExeName);

    /// <summary>Where the other instance's exe would be (it may not exist).</summary>
    public string OtherInstallExe => Other.InstallExe;

    /// <summary>The live-meeting marker of this instance.</summary>
    public string LiveMeetingMarkerPath => Path.Combine(LocalFolder, LiveMeetingMarkerFileName);

    /// <summary>Dev has no updater (the Dev build is replaced by the scheduled task in tools/dev).</summary>
    public bool UpdatesEnabled => !IsDev;

    /// <summary>Dev must not touch what the normal app registered: Start menu, Apps entry, move offer, AI-app configs.</summary>
    public bool MayRegisterSystemEntries => !IsDev;

    // ---------------- live meeting marker ----------------

    /// <summary>Writes the marker for a recording that starts now in process <paramref name="pid"/>. Best effort, never throws.</summary>
    public void WriteLiveMeetingMarker(int pid, DateTime startedUtc)
    {
        try
        {
            Directory.CreateDirectory(LocalFolder);
            File.WriteAllText(LiveMeetingMarkerPath, JsonSerializer.Serialize(new { pid, started = startedUtc.ToString("O", CultureInfo.InvariantCulture) }));
        }
        catch (Exception ex) { Log.Error("Live meeting marker not written", ex); }
    }

    /// <summary>Removes the marker. Best effort, never throws; a missing file is fine.</summary>
    public void DeleteLiveMeetingMarker()
    {
        try { File.Delete(LiveMeetingMarkerPath); }
        catch (Exception ex) { Log.Error("Live meeting marker not removed", ex); }
    }

    /// <summary>Reads a marker file: pid and start time, or null when absent or unreadable.</summary>
    public static (int Pid, DateTime Started)? ReadLiveMeetingMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int pid = root.GetProperty("pid").GetInt32();
            var started = DateTime.Parse(root.GetProperty("started").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return (pid, started);
        }
        catch (Exception) { return null; }
    }
}
