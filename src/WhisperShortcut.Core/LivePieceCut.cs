namespace WhisperShortcut.Core;

/// <summary>
/// When a Dictate Live piece ends (Mac #483, <c>ChunkedDictateRecorder.silenceCriterion</c>).
/// Pure: meter-window levels in, cut points out. Meetings leave <see cref="MeetingChunkPolicy.SoftCutAfter"/> null,
/// so they keep the hard pause only. The max length stays the backstop when nothing is quiet.
/// </summary>
public static class LivePieceCut
{
    /// <summary>What counts as silence for a piece of this length.</summary>
    /// <param name="ThresholdDb">Level (dBFS) a window must be under.</param>
    /// <param name="DecisionsNeeded">Consecutive quiet windows that end the piece.</param>
    /// <param name="IsSoft">True when the relaxed rule after <see cref="MeetingChunkPolicy.SoftCutAfter"/> is in force.</param>
    public readonly record struct SilenceCriterion(float ThresholdDb, int DecisionsNeeded, bool IsSoft);

    /// <summary>
    /// The hard pause, or — once the piece has reached <see cref="MeetingChunkPolicy.SoftCutAfter"/> — two windows
    /// (the live policy's 0.4 s) under <paramref name="baseThresholdDb"/> plus
    /// <see cref="MeetingChunkPolicy.SoftCutBoostDb"/>, never above <see cref="MeetingChunkPolicy.MaxSoftCutThresholdDb"/>.
    /// </summary>
    public static SilenceCriterion Criterion(MeetingChunkPolicy policy, double pieceSeconds, float baseThresholdDb)
    {
        int Needed(TimeSpan duration)
        {
            var interval = policy.MeterWindow.TotalSeconds;
            if (interval <= 0) return 1;
            return Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / interval - 1e-9));
        }

        if (policy.SoftCutAfter is { } after && pieceSeconds + 1e-6 >= after.TotalSeconds)
        {
            var silence = policy.SoftCutSilence > TimeSpan.Zero ? policy.SoftCutSilence : TimeSpan.FromSeconds(0.4);
            return new SilenceCriterion(
                Math.Min(baseThresholdDb + policy.SoftCutBoostDb, policy.MaxSoftCutThresholdDb),
                Needed(silence), true);
        }
        return new SilenceCriterion(baseThresholdDb, Needed(policy.SilenceDuration), false);
    }

    /// <summary>One cut. The piece is windows <c>[previous end, <see cref="EndWindow"/>)</c> of the sequence.</summary>
    /// <param name="EndWindow">Exclusive index of the last window that belongs to the piece.</param>
    /// <param name="Soft">True when the relaxed rule ended it.</param>
    public readonly record struct Cut(int EndWindow, bool Soft);

    /// <summary>
    /// Cuts <paramref name="windowDb"/> the way Dictate Live cuts a recording. Each value is one
    /// <see cref="MeetingChunkPolicy.MeterWindow"/>. Silence is counted from the start of the piece, so a pause
    /// that straddles the minimum still ends it. Nothing is cut on the final window: that tail stays with the
    /// caller. At <see cref="MeetingChunkPolicy.MaxChunk"/> a piece ends even when every window is loud.
    /// </summary>
    public static List<Cut> Cuts(IReadOnlyList<float> windowDb, MeetingChunkPolicy policy, float baseThresholdDb)
    {
        var window = policy.MeterWindow.TotalSeconds;
        var maxWindows = Math.Max(1, (int)Math.Round(policy.MaxChunk.TotalSeconds / Math.Max(window, 1e-9)));
        var minSeconds = policy.MinChunk.TotalSeconds;
        var cuts = new List<Cut>();
        int start = 0;
        int silent = 0;
        for (int i = 0; i < windowDb.Count; i++)
        {
            int inPiece = i - start + 1;
            double pieceSeconds = inPiece * window;
            // The last window is the tail, never an empty piece after a cut.
            if (i + 1 >= windowDb.Count) break;
            if (inPiece >= maxWindows)
            {
                cuts.Add(new Cut(i + 1, false));
                start = i + 1;
                silent = 0;
                continue;
            }
            if (pieceSeconds + 1e-9 < minSeconds)
            {
                silent = windowDb[i] < baseThresholdDb ? silent + 1 : 0;
                continue;
            }
            var criterion = Criterion(policy, pieceSeconds, baseThresholdDb);
            if (windowDb[i] < criterion.ThresholdDb)
            {
                silent++;
                if (silent >= criterion.DecisionsNeeded)
                {
                    cuts.Add(new Cut(i + 1, criterion.IsSoft));
                    start = i + 1;
                    silent = 0;
                }
            }
            else silent = 0;
        }
        return cuts;
    }
}
