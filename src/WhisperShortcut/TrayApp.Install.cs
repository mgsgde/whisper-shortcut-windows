using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>
/// Install-related behaviour of the tray app: the signals from a second start / the uninstaller, and the offer to move
/// the exe to its fixed place (%LOCALAPPDATA%\Programs\WhisperShortcut), so autostart, Start menu and updates keep working
/// when Downloads is cleaned up or a ZIP was never unpacked.
/// </summary>
internal sealed partial class TrayApp
{
    /// <summary>Second start of the app (Start menu search): like the Mac's reopen, show the settings. Any thread.</summary>
    public void ShowSettingsFromSecondStart() => _ui.Post(_ => ShowSettings(), null);

    /// <summary><c>--uninstall</c> asks the running instance to exit. Any thread.</summary>
    public void QuitFromUninstall()
    {
        Core.Log.Info("Quit requested by the uninstaller");
        _ui.Post(_ => Quit(), null);
    }

    /// <summary>
    /// Once per exe path: offers to copy the exe to its fixed place and restart from there. Posted after the welcome tour
    /// has opened (if it does), so the two never fight over the first window.
    /// </summary>
    private void OfferMoveToFixedPlace()
    {
        try
        {
            var exe = InstallState.ExePath;
            var place = InstallState.Place;
            if (InstallState.TestRun || string.IsNullOrEmpty(exe) || !Core.InstallLocation.ShouldOfferMove(place)) return;
            if (_settings.MoveOfferDismissed.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;

            var where = place == Core.InstallPlace.Desktop ? "dem Desktop" : "Downloads";
            var text = place == Core.InstallPlace.Temporary
                ? "Du hast WhisperShortcut direkt aus der ZIP-Datei gestartet, ohne sie zu entpacken. Diese Kopie ist nur vorübergehend da "
                    + "und verschwindet bald.\n\n"
                : $"WhisperShortcut läuft gerade aus {where}. Wenn dort aufgeräumt wird, ist die App weg.\n\n";
            text += "Soll es sich in deinen Programme-Ordner kopieren (%LOCALAPPDATA%\\Programs\\WhisperShortcut)? Dann funktionieren Autostart, "
                + "Startmenü und Updates dauerhaft. Danach startet WhisperShortcut von dort neu. Die Datei hier bleibt liegen.";
            int choice = ChoiceDialog.Ask(null!, "WhisperShortcut an seinen festen Platz legen?", text, new[] { "Verschieben", "Hier lassen" });
            if (choice != 0)
            {
                // Esc and the X count as "Hier lassen": one question per path is enough.
                _settings.MoveOfferDismissed.Add(exe);
                TrySave();
                return;
            }
            if (MoveToFixedPlace(exe, out var error)) Quit();
            else
            {
                Core.Log.Error($"Move to the fixed place failed: {error}");
                MessageBox.Show("Das Verschieben hat nicht geklappt. WhisperShortcut läuft weiter von hier. "
                    + "Du kannst die Datei auch selbst nach %LOCALAPPDATA%\\Programs\\WhisperShortcut legen.",
                    "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Error("Move offer failed", ex);
        }
    }

    /// <summary>
    /// Copies this exe to the canonical path and starts it with <c>--moved</c> (it waits for our mutex). True when the copy runs.
    /// An existing file there is replaced unless it is this very file or already at least as new.
    /// </summary>
    private static bool MoveToFixedPlace(string self, out string? error)
    {
        error = null;
        try
        {
            var target = InstallState.CanonicalExe;
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)) return true;
            Directory.CreateDirectory(InstallState.CanonicalDir);
            if (!File.Exists(target) || !ExistingIsNewer(target, self))
            {
                // Copy beside, then rename: a half-written exe never sits at the final name.
                var tmp = target + ".new";
                File.Copy(self, tmp, overwrite: true);
                File.Move(tmp, target, overwrite: true);
            }
            Core.Log.Info($"Moved to the fixed place, starting {target}");
            Process.Start(new ProcessStartInfo(target) { Arguments = "--moved", UseShellExecute = false, WorkingDirectory = InstallState.CanonicalDir })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
    }

    private static bool ExistingIsNewer(string existing, string self)
    {
        try
        {
            return Version.TryParse(FileVersionInfo.GetVersionInfo(existing).FileVersion, out var a)
                && Version.TryParse(FileVersionInfo.GetVersionInfo(self).FileVersion, out var b) && a > b;
        }
        catch (Exception) { return false; }
    }
}
