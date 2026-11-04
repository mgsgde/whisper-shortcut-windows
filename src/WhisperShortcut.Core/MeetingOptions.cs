namespace WhisperShortcut.Core;

/// <summary>
/// The Meetings settings choices (Mac MeetingsSettingsTab: LiveMeetingChunkInterval, MeetingSafeguardDuration, the meeting
/// summary default), and how they turn into the chunk policy and the „Langes Meeting“ question.
/// </summary>
public static class MeetingOptions
{
    /// <summary>Mac SettingsDefaults.liveMeetingChunkInterval: 60 s.</summary>
    public const int DefaultChunkSeconds = 60;

    /// <summary>Mac SettingsDefaults.liveMeetingSafeguardDuration: ask after 90 min.</summary>
    public const int DefaultSafeguardMinutes = 90;

    /// <summary>Mac SettingsDefaults.selectedMeetingSummaryModel: Gemini 3.5 Flash-Lite.</summary>
    public const string DefaultSummaryModel = "gemini-3.5-flash-lite";

    /// <summary>Mac LiveMeetingChunkInterval: 15, 30, 45, 60 s.</summary>
    public static IReadOnlyList<int> ChunkSeconds { get; } = new[] { 15, 30, 45, 60 };

    /// <summary>Mac MeetingSafeguardDuration: never (0), 60, 90, 120 min.</summary>
    public static IReadOnlyList<int> SafeguardMinutes { get; } = new[] { 0, 60, 90, 120 };

    /// <summary>A stored interval when it is one of <see cref="ChunkSeconds"/>, else the default.</summary>
    public static int NormalizeChunkSeconds(int? seconds) =>
        seconds is { } s && ChunkSeconds.Contains(s) ? s : DefaultChunkSeconds;

    /// <summary>A stored safeguard when it is one of <see cref="SafeguardMinutes"/>, else the default.</summary>
    public static int NormalizeSafeguardMinutes(int? minutes) =>
        minutes is { } m && SafeguardMinutes.Contains(m) ? m : DefaultSafeguardMinutes;

    /// <summary>German label: „15 Sekunden“ … „60 Sekunden“.</summary>
    public static string ChunkLabel(int seconds) => $"{seconds} Sekunden";

    /// <summary>German label (Mac wording): „Nie“, „60 Minuten“, „90 Minuten“, „2 Stunden“.</summary>
    public static string SafeguardLabel(int minutes) => minutes switch
    {
        0 => "Nie",
        120 => "2 Stunden",
        _ => $"{minutes} Minuten",
    };

    /// <summary>The chunk policy for an interval: the Mac's cut rules (10 s min, 1.5 s below -40 dB) with this maximum.</summary>
    public static MeetingChunkPolicy Policy(int chunkSeconds)
    {
        var max = TimeSpan.FromSeconds(NormalizeChunkSeconds(chunkSeconds));
        var d = MeetingChunkPolicy.Default;
        // A 15 s maximum still needs room for a pause cut: the minimum never exceeds the maximum.
        return d with { MaxChunk = max, MinChunk = d.MinChunk < max ? d.MinChunk : max };
    }

    /// <summary>
    /// Whether to ask „Langes Meeting“ now (Mac showSafeguardAlert): once per meeting, when the setting is not „Nie“ and the
    /// meeting has run for at least that long.
    /// </summary>
    public static bool ShouldAskLongMeeting(int safeguardMinutes, TimeSpan elapsed, bool alreadyAsked) =>
        !alreadyAsked && safeguardMinutes > 0 && elapsed >= TimeSpan.FromMinutes(safeguardMinutes);

    /// <summary>Mac: „This meeting has been transcribing for over %lld minutes. Stop or continue?“</summary>
    public static string LongMeetingText(int minutes) =>
        $"Dieses Meeting wird seit über {minutes} Minuten transkribiert. Beenden oder weitermachen?";
}
