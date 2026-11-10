namespace WhisperShortcut.Core;

/// <summary>
/// One meter rule for every level display (recording pill, meeting card, welcome mic test), so "silent" means
/// the same everywhere. Fast attack, slow release, and a square root so normal speech fills the bars.
/// </summary>
public sealed class LevelMeter
{
    /// <summary>Under 1 % is a blocked or muted microphone, not a quiet voice.</summary>
    public const float SilentPeak = 0.01f;

    private readonly float _release;

    /// <param name="release">Share of the old value kept per step while the level falls: smoothing = release * old + (1 - release) * new.</param>
    public LevelMeter(float release) => _release = release;

    public float Smoothed { get; private set; }

    /// <summary>Highest clamped raw level fed so far (before the square root).</summary>
    public float Peak { get; private set; }

    public float Feed(float level)
    {
        float raw = Math.Clamp(level, 0f, 1f);
        if (raw > Peak) Peak = raw;
        raw = MathF.Sqrt(raw);
        Smoothed = raw > Smoothed ? raw : Smoothed * _release + raw * (1f - _release);
        return Smoothed;
    }

    /// <summary>A new recording starts: the old bars and peak must not leak into it.</summary>
    public void Reset()
    {
        Smoothed = 0;
        Peak = 0;
    }

    public static bool IsSilent(float peak, TimeSpan elapsed, TimeSpan after) => elapsed > after && peak < SilentPeak;
}

/// <summary>The elapsed-time text of the recording overlays.</summary>
public static class Clock
{
    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
