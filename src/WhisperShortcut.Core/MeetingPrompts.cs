namespace WhisperShortcut.Core;

/// <summary>Prompts for the summary a Live Meeting writes when it ends (port of the macOS app's rules).</summary>
public static class MeetingPrompts
{
    /// <summary>
    /// System prompt for the end-of-meeting summary (pass as the custom system prompt). It asks for titled sections with "-"
    /// bullets, valid Markdown that survives the Dictate Prompt output rule (no # headers) appended to every system prompt.
    /// </summary>
    public const string SummarySystemPrompt =
        "You summarize meeting transcripts. The transcript consists of lines that start with a timestamp such as [03:15].\n" +
        "\n" +
        "Rules:\n" +
        "- Write the summary in the same language as the transcript.\n" +
        "- Output only Markdown. No preamble, no closing remarks, no code fences.\n" +
        // WHY no "##": the text-model call appends the Dictate Prompt output rule, which forbids # headers.
        "- Sections in this order: main points, key takeaways, decisions, action items. Each section starts with its title " +
        "alone on a line, ending with a colon (e.g. \"Hauptpunkte:\"), in the language of the transcript. " +
        "Leave out decisions and action items when the meeting has none.\n" +
        "- Under each heading use \"-\" bullets, one idea per bullet, short and concrete. Do not write plain paragraphs.\n" +
        "- Put a blank line between sections.\n" +
        "- For action items name the owner and the deadline if the transcript says so.\n" +
        "- Do not invent anything that is not in the transcript, and do not repeat the timestamps.";

    /// <summary>The instruction that goes with the transcript.</summary>
    public const string SummaryInstruction = "Summarize this meeting transcript.";

    /// <summary>Longest transcript sent for a summary, like the Mac (60 000 characters).</summary>
    public const int MaxTranscriptChars = 60_000;

    /// <summary>Returns the transcript, or its last <see cref="MaxTranscriptChars"/> characters when longer: the end holds the outcome of a meeting.</summary>
    public static string Clip(string transcript)
    {
        if (transcript.Length <= MaxTranscriptChars) return transcript;
        var start = transcript.Length - MaxTranscriptChars;
        // Do not start in the middle of a surrogate pair.
        if (char.IsLowSurrogate(transcript[start])) start++;
        return transcript[start..];
    }
}
