namespace WhisperShortcut.Core;

/// <summary>
/// The last template dictation's audio, for „Nochmal ohne Vorlage“ (Mac runAgainWithoutTemplate). Kept in memory only,
/// never on disk (patient notes), and dropped after <see cref="Lifetime"/> or when the app quits.
/// </summary>
public sealed class TemplateReplay
{
    /// <summary>How long the menu item stays (Mac lastTemplateRecordingLifetime, 10 min).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private (byte[] Wav, double Seconds, string TemplateName, DateTime Until)? _kept;

    /// <summary>Keeps <paramref name="wav"/>, replacing any earlier one.</summary>
    public void Keep(byte[] wav, double seconds, string templateName, DateTime now) =>
        _kept = (wav, seconds, string.IsNullOrWhiteSpace(templateName) ? "Vorlage" : templateName.Trim(), now + Lifetime);

    /// <summary>The kept recording while it is fresh; null (and dropped) once it expired.</summary>
    public (byte[] Wav, double Seconds, string TemplateName)? Current(DateTime now)
    {
        DropIfExpired(now);
        return _kept is { } k ? (k.Wav, k.Seconds, k.TemplateName) : null;
    }

    /// <summary>Drops the recording once its time is up; true when it was dropped now.</summary>
    public bool DropIfExpired(DateTime now)
    {
        if (_kept is not { } k || now < k.Until) return false;
        _kept = null;
        return true;
    }

    /// <summary>Forgets the recording.</summary>
    public void Clear() => _kept = null;
}

/// <summary>
/// The last dictation cancelled while it was being processed, for „Abgebrochene Aufnahme transkribieren“ (Mac
/// transcribeCancelledRecording). Only ever one: an undo for the cancel just made, not a history. Memory only.
/// </summary>
public sealed class CancelledRecording
{
    private (byte[] Wav, double Seconds)? _kept;

    /// <summary>Whether a cancelled dictation is kept.</summary>
    public bool HasRecording => _kept != null;

    /// <summary>Keeps a cancelled dictation, replacing an older one. Empty audio is not worth keeping.</summary>
    public void Keep(byte[]? wav, double seconds)
    {
        if (wav == null || wav.Length == 0) return;
        _kept = (wav, seconds);
    }

    /// <summary>Hands the recording over (it is gone afterwards); null when there is none.</summary>
    public (byte[] Wav, double Seconds)? Take()
    {
        var k = _kept;
        _kept = null;
        return k;
    }
}
