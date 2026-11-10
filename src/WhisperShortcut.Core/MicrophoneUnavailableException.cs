namespace WhisperShortcut;

/// <summary>
/// No usable microphone. Declared in the parent namespace on purpose: the app and the settings window name it
/// unqualified, and <see cref="RunOutcomes"/> needs to recognise it.
/// </summary>
public sealed class MicrophoneUnavailableException : Exception
{
    public MicrophoneUnavailableException(Exception inner)
        : base("Kein Mikrofon gefunden oder Zugriff gesperrt. Bitte ein Mikrofon anschließen und in Windows als Standardgerät wählen. Zugriff prüfen: Einstellungen → Datenschutz (und Sicherheit) → Mikrofon → Desktop-Apps den Zugriff erlauben.", inner) { }
}
