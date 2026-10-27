namespace WhisperShortcut.Core;

/// <summary>Where the app keeps data on disk, and the "Zurücksetzen" that removes all of it.</summary>
public static class AppData
{
    /// <summary>%APPDATA%\WhisperShortcut: settings.json (API key, glossary, prompts).</summary>
    public static string SettingsFolder => Path.GetDirectoryName(Settings.DefaultPath)!;

    /// <summary>%LOCALAPPDATA%\WhisperShortcut: log file and offline models.</summary>
    public static string LocalFolder => Path.GetDirectoryName(Log.DefaultPath)!;

    /// <summary>Folder of the log file (inside <see cref="LocalFolder"/>).</summary>
    public static string LogFolder => Path.GetDirectoryName(Log.DefaultPath)!;

    /// <summary>Every folder the app writes to, distinct. Recent texts live in memory only.</summary>
    public static IReadOnlyList<string> Folders => new[] { SettingsFolder, LocalFolder, LogFolder, OfflineCatalog.Root }
        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// True once <see cref="Reset"/> ran on the real folders: <see cref="Settings.Save"/> and <see cref="Log"/>
    /// then do nothing, so the exit that follows cannot recreate what was just deleted.
    /// </summary>
    public static bool ResetDone { get; private set; }

    /// <summary>
    /// Deletes the folders recursively (default: <see cref="Folders"/>). Each folder is tried even if another failed;
    /// the ones that could not be deleted (e.g. a model file in use) are listed in the thrown <see cref="IOException"/>.
    /// </summary>
    public static void Reset(IEnumerable<string>? folders = null)
    {
        bool real = folders == null;
        if (real) ResetDone = true; // before deleting: stop writers first
        var failed = new List<string>();
        foreach (var folder in folders ?? Folders)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                failed.Add(folder);
            }
        }
        if (failed.Count > 0)
            throw new IOException("Nicht gelöscht werden konnte: " + string.Join("; ", failed));
    }
}
