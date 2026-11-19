using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

/// <summary>
/// Which transcript a summary was built from: how many chunks it had and where the last one starts.
/// Written next to summary.md as summary.meta.json. A later splice (recovered backlog, a chunk that
/// arrived while the model was still writing) no longer matches, so the summary is stale.
/// Same fields as the Mac's <c>MeetingListService.SummaryMeta</c>.
/// </summary>
public sealed record MeetingSummaryMeta(
    [property: JsonPropertyName("chunkCount")] int ChunkCount,
    [property: JsonPropertyName("lastChunkStart")] double LastChunkStart)
{
    /// <summary>Meta for these chunks. The last start is the greatest start, or 0 when there are none.</summary>
    public static MeetingSummaryMeta FromChunks(IReadOnlyList<TranscriptChunk> chunks)
    {
        var last = 0.0;
        for (var i = 0; i < chunks.Count; i++)
            if (chunks[i].StartSeconds > last) last = chunks[i].StartSeconds;
        return new MeetingSummaryMeta(chunks.Count, last);
    }

    /// <summary>
    /// True when the summary still covers <paramref name="chunks"/>. A summary without meta predates
    /// this check and counts as matching, so old meetings are not regenerated wholesale.
    /// </summary>
    public static bool Matches(MeetingSummaryMeta? meta, IReadOnlyList<TranscriptChunk> chunks) =>
        meta == null || meta == FromChunks(chunks);
}

/// <summary>Whether an ended meeting's summary should be written again. Pure: no files, no model call.</summary>
public static class MeetingSummaryFreshness
{
    /// <summary>
    /// True when the meeting is no longer recording, its transcript has at least one chunk, no audio is
    /// still waiting for a retry, and either summary.md is missing or its sidecar does not match the
    /// transcript. A sidecar-less summary counts as current. Pending audio waits: the summary is redone
    /// after the retry, not from a fragment.
    /// </summary>
    public static bool NeedsNewSummary(MeetingStatus status, bool hasSummary, MeetingSummaryMeta? meta,
        IReadOnlyList<TranscriptChunk> chunks, int pendingAudioChunks)
    {
        if (status == MeetingStatus.Recording) return false;
        if (pendingAudioChunks > 0) return false;
        if (chunks.Count == 0) return false;
        if (!hasSummary) return true;
        return !MeetingSummaryMeta.Matches(meta, chunks);
    }
}
