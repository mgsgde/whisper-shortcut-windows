using System.Text;

namespace WhisperShortcut;

internal sealed partial class TrayApp
{
    private bool _smokeSaveLogged;

    /// <summary>
    /// Headless CI run (<c>--smoke-dictate</c>): pushes the test wav through the same path as the tray item „Diktieren“
    /// (speech gate, Gemini, cleanup) and writes the result to the out file. Exit codes: 0 text, 1 error, 3 gate not usable, 4 empty text.
    /// </summary>
    private async Task RunSmokeDictationAsync()
    {
        try
        {
            // WHY up front: PrepareSpeechGate skips for these, so the wait below would only time out.
            if (_fakeGemini || _settings.EffectiveSpeechEngine == Core.Engine.Local)
            {
                SmokeFail("ERROR: smoke needs the cloud speech engine", 3);
                return;
            }

            // A careful tester waits for the Silero VAD; without it the gate would let everything through and the check proves nothing.
            Core.Log.Info("Smoke: waiting for the Silero VAD");
            var deadline = Environment.TickCount64 + 180_000;
            while (!Core.SpeechActivityGate.IsReady)
            {
                if (Environment.TickCount64 > deadline)
                {
                    SmokeFail("ERROR: Silero VAD not ready", 3);
                    return;
                }
                await Task.Delay(250);
            }

            // Idle, so the press gate does not apply: the same entry as the tray menu.
            OnHotkey(Mode.Dictate);
            if (_take is not { Phase: TakePhase.Recording, Kind: Core.RecordingKindName.Dictate })
            {
                SmokeFail($"ERROR: dictation did not start (state {PhaseName})", 1);
                return;
            }
            // The list may hold an older text (persisted history): only a new entry counts.
            var before = _recent.Latest;
            await Task.Delay(500);
            await StopAndProcessAsync();

            var text = ReferenceEquals(_recent.Latest, before) ? "" : _recent.Latest ?? "";
            File.WriteAllText(_smokeOut!, text, new UTF8Encoding(false));
            Core.Log.Info($"Smoke: done, {text.Length} chars");
            Environment.ExitCode = text.Length > 0 ? 0 : 4;
            Quit();
        }
        catch (Exception e)
        {
            Core.Log.Error("Smoke dictation failed", e);
            SmokeFail($"ERROR: {e.GetType().Name}: {e.Message}", 1);
        }
    }

    private void SmokeFail(string message, int exitCode)
    {
        Core.Log.Error("Smoke: " + message);
        try { File.WriteAllText(_smokeOut!, message, new UTF8Encoding(false)); } catch { /* the exit code still tells */ }
        Environment.ExitCode = exitCode;
        Quit();
    }
}
