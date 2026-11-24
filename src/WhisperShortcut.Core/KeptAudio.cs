namespace WhisperShortcut.Core;

/// <summary>
/// A recording whose processing failed for a reason a second try can fix (network, quota, local model), or kept audio
/// that runs again (cancelled recording, "Nochmal ohne Vorlage", a take recovered after a crash). Replays are always
/// clipboard-only: the window the take was made for is whatever the user did since. <see cref="Kind"/> is never Live:
/// a live dictation replays as a plain dictation of its audio. Built from a running take by <c>Take.ToReplay</c>.
/// </summary>
public sealed record FailedRun(byte[] Wav, RecordingKindName Kind, double Seconds, string StopKey = "",
    string? Selection = null, string? QuickAction = null, IntPtr Target = default, DictationTemplate? Template = null,
    ScreenshotImage? Screenshot = null, bool NoNewTextTemplate = false);

/// <summary>What <see cref="KeptAudio.Expired"/> reports.</summary>
public enum KeptItem { Screenshot, TemplateReplay }

/// <summary>
/// Everything kept in memory for a later run, with one owner that decides when it goes: the failed run for the retry
/// (until the next success), the audio of a cancelled dictation (until used), the last template dictation (10 min) and
/// the held screenshot (5 min). A caller polls <see cref="ExpireDue"/> from ONE timer while <see cref="NeedsTimer"/>;
/// every read expires first, so an item is dropped, and <see cref="Expired"/> raised, exactly once. Memory only.
/// </summary>
public sealed class KeptAudio
{
    private readonly Func<DateTime> _clock;
    private readonly CancelledRecording _cancelled = new();
    private readonly TemplateReplay _template = new();
    private readonly ScreenshotHold _shot;

    public KeptAudio(Func<DateTime> clock)
    {
        _clock = clock;
        _shot = new ScreenshotHold(clock);
    }

    /// <summary>An item ran out of time (raised once per drop); explicit clears do not raise it.</summary>
    public event Action<KeptItem>? Expired;

    /// <summary>The run the "Letzte Aufnahme erneut verarbeiten" row would repeat; no time limit.</summary>
    public FailedRun? LastFailed { get; set; }

    // ---- cancelled dictation ----
    public bool HasCancelled => _cancelled.HasRecording;
    public void KeepCancelled(byte[]? wav, double seconds) => _cancelled.Keep(wav, seconds);
    public (byte[] Wav, double Seconds)? TakeCancelled() => _cancelled.Take();

    // ---- last template dictation ----
    public void KeepTemplate(byte[] wav, double seconds, string templateName) => _template.Keep(wav, seconds, templateName, _clock());

    /// <summary>The kept template dictation while it is fresh; retrying inside the window keeps it.</summary>
    public (byte[] Wav, double Seconds, string TemplateName)? CurrentTemplate()
    {
        ExpireDue();
        return _template.Current(_clock());
    }

    public void ClearTemplate() => _template.Clear();

    // ---- held screenshot ----
    public void HoldShot(ScreenshotImage image) => _shot.Hold(image);

    public bool HasShot
    {
        get
        {
            ExpireDue();
            return _shot.HasImage;
        }
    }

    /// <summary>The held image, once (it is gone afterwards); null when none or expired.</summary>
    public ScreenshotImage? TakeShot()
    {
        ExpireDue();
        return _shot.Take();
    }

    public void ClearShot() => _shot.Clear();

    /// <summary>Whether something with a time limit is still kept, so the timer must keep running.</summary>
    public bool NeedsTimer
    {
        get
        {
            ExpireDue();
            return _shot.HasImage || _template.Current(_clock()) != null;
        }
    }

    /// <summary>Drops what ran out of time and raises <see cref="Expired"/> once for each.</summary>
    public void ExpireDue()
    {
        var now = _clock();
        if (_template.DropIfExpired(now)) Expired?.Invoke(KeptItem.TemplateReplay);
        if (_shot.DropIfExpired()) Expired?.Invoke(KeptItem.Screenshot);
    }
}
