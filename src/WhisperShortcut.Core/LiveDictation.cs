using System.Diagnostics;
using System.Globalization;

namespace WhisperShortcut.Core;

/// <summary>Why Live diktieren cannot start, in the order the user would fix it.</summary>
public enum LiveUnavailable { DuringMeeting, NotParakeet, NotDownloaded }

/// <summary>The dictation shortcut's mode: normal Diktieren or Live diktieren.</summary>
public enum DictateMode { Dictate, Live }

/// <summary>What the dictation shortcut does for a mode and the availability of Live.</summary>
public enum LiveStartDecision { StartDictate, StartLive, Refuse }

/// <summary>What is known about the character before the caret in the focused field.</summary>
public enum CaretKind { Unknown, StartOfText, Character }

/// <summary>
/// The character already before the caret (Mac FocusedTextContext.CaretContext). Unknown and start of text leave the
/// first live piece unspaced. The character is dictated text: never logged, <see cref="LogKind"/> is the label that is.
/// </summary>
public readonly record struct CaretContext(CaretKind Kind, char Character = '\0')
{
    /// <summary>The caret could not be read.</summary>
    public static CaretContext Unknown => new(CaretKind.Unknown);

    /// <summary>The caret is at the start of the field.</summary>
    public static CaretContext StartOfText => new(CaretKind.StartOfText);

    /// <summary>The character right before the caret.</summary>
    public static CaretContext Of(char character) => new(CaretKind.Character, character);

    /// <summary>"unknown", "start", "letter", "space", "newline" or "other". Never the character.</summary>
    public string LogKind => Kind switch
    {
        CaretKind.Unknown => "unknown",
        CaretKind.StartOfText => "start",
        _ when char.IsLetter(Character) => "letter",
        _ when Character == ' ' => "space",
        _ when LiveChunkJoiner.IsNewline(Character) => "newline",
        _ => "other",
    };
}

/// <summary>
/// The last character of the previous live dictation, kept in memory so the next one can be spaced and cased when the
/// caret cannot be read. Never persisted, never logged; it is patient text. <paramref name="App"/> is the target
/// process name.
/// </summary>
public sealed record LastInsertion(char Character, string App, DateTime At)
{
    /// <summary>Older than this, the user has most likely moved on.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(600);

    /// <summary>The memory for a finished dictation, or null when nothing was inserted or the target is unknown.</summary>
    public static LastInsertion? Make(string insertedText, string? app, DateTime now) =>
        insertedText.Length == 0 || string.IsNullOrEmpty(app) ? null : new LastInsertion(insertedText[^1], app, now);

    /// <summary>A readable caret always wins. Only Unknown falls back to the memory, and only for the same app, within <see cref="MaxAge"/>.</summary>
    public static CaretContext Seed(CaretContext caret, LastInsertion? last, string? frontmostApp, DateTime now)
    {
        if (caret.Kind != CaretKind.Unknown || last == null || frontmostApp == null
            || !string.Equals(last.App, frontmostApp, StringComparison.OrdinalIgnoreCase)) return caret;
        var age = now - last.At;
        if (age < TimeSpan.Zero || age >= MaxAge) return caret;
        return CaretContext.Of(last.Character);
    }
}

/// <summary>What happens to the clipboard when a live dictation ends.</summary>
public enum LiveClipboardEnd
{
    /// <summary>Put the user's clipboard from before the dictation back (if we still own it).</summary>
    RestoreBefore,
    /// <summary>Leave everything dictated on the clipboard.</summary>
    KeepText,
    /// <summary>Leave the dictated text on the clipboard, then clear it later if it is still ours.</summary>
    KeepTextThenClear,
    /// <summary>Put the text that could not be inserted (another window was in front) on the clipboard.</summary>
    KeepHeld,
    /// <summary>Put the whole-file decode on the clipboard because some pieces failed.</summary>
    KeepFallback,
    /// <summary>Do not touch the clipboard.</summary>
    LeaveAsIs,
}

/// <summary>
/// DIAG (branch diag/live-diktat): how a piece was cut. <paramref name="Reason"/> is "Pause", "Max" or "Stop";
/// <paramref name="CutTimestamp"/> is <see cref="Stopwatch.GetTimestamp"/> when the piece left the chunker.
/// Numbers only, never text.
/// </summary>
public sealed record LivePieceInfo(string Reason, long CutTimestamp);

/// <summary>What a live dictation produced.</summary>
/// <param name="Text">Everything the dictation produced, joined: what the document should now contain.</param>
/// <param name="HeldText">The part that could not be inserted because another window was in front.</param>
/// <param name="Pieces">Pieces that went to the decoder.</param>
/// <param name="FailedPieces">Pieces that failed to decode.</param>
public sealed record LiveDictationOutcome(string Text, string HeldText, int Pieces, int FailedPieces);

/// <summary>
/// Live diktieren (macOS "Dictate Live"): text at the cursor while speaking. The recording is cut at speech
/// pauses, each piece is decoded once with offline Parakeet and pasted at the cursor. Append-only: an inserted
/// piece is never touched again. This is the pure half; <see cref="LiveDictationSession"/> runs a recording.
/// </summary>
public static class LiveDictation
{
    /// <summary>
    /// Gap after each piece's Ctrl+V before the next piece may replace the clipboard; deliberately not the
    /// clipboard-restore delay, which would stall live text.
    /// </summary>
    public static readonly TimeSpan PieceGap = TimeSpan.FromSeconds(0.4);

    /// <summary>Pause between writing the clipboard and sending Ctrl+V.</summary>
    public static readonly TimeSpan PrePasteDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Mac <c>RotationPolicy.live</c> (min 1.5 s, cut after 0.6 s of silence = three 0.2 s windows); the 30 s max is
    /// ours (the Mac has none; it keeps a piece decodable when someone talks without a pause).
    /// The -45 dB threshold is the Mac's ChunkedDictateRecorder.silenceThresholdDB; -40 counted quiet speech as a pause
    /// and cut pieces mid-sentence (1-2 character pieces).
    /// </summary>
    public static MeetingChunkPolicy ChunkPolicy { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1.5),
        TimeSpan.FromSeconds(0.6), -45f, TimeSpan.FromSeconds(0.2));

    /// <summary>Null when Live diktieren may start; otherwise the first thing the user has to fix.</summary>
    public static LiveUnavailable? UnavailableReason(bool duringMeeting, bool speechIsLocal, bool speechInstalled)
    {
        if (duringMeeting) return LiveUnavailable.DuringMeeting;
        if (!speechIsLocal) return LiveUnavailable.NotParakeet;
        if (!speechInstalled) return LiveUnavailable.NotDownloaded;
        return null;
    }

    /// <summary>The German notice for a reason.</summary>
    public static string Message(LiveUnavailable reason) => reason switch
    {
        LiveUnavailable.DuringMeeting => "Live diktieren ist während eines Meetings nicht verfügbar.",
        LiveUnavailable.NotParakeet => "Live diktieren funktioniert mit dem Offline-Modell Parakeet. Wähle es unter Einstellungen → Diktieren.",
        LiveUnavailable.NotDownloaded => "Lade Parakeet unter Einstellungen → Diktieren, um Live diktieren zu nutzen.",
        _ => "",
    };

    /// <summary>What the dictation shortcut does in <paramref name="mode"/> given why Live may be unavailable.</summary>
    public static (LiveStartDecision Decision, LiveUnavailable? Reason) StartDecision(DictateMode mode, LiveUnavailable? unavailable)
    {
        if (mode == DictateMode.Dictate) return (LiveStartDecision.StartDictate, null);
        // During a live meeting, Live is never offered: the meeting already owns a dedicated dictation
        // segment, so the Dictate mode falls back to recording that instead of refusing.
        if (unavailable == LiveUnavailable.DuringMeeting) return (LiveStartDecision.StartDictate, null);
        if (unavailable is { } reason) return (LiveStartDecision.Refuse, reason);
        return (LiveStartDecision.StartLive, null);
    }

    /// <summary>
    /// One decoded piece as normal offline dictation finishes a transcript: standalone fillers dropped, model
    /// punctuation stripped in "Only when spoken" mode, then the spoken layout commands. Edge line breaks
    /// ("neuer Absatz" at the very end of a piece) are kept: <see cref="LiveChunkJoiner"/> relies on them to lay out
    /// the next piece without adding a space after. <paramref name="timedMarks"/>: the piece's commas, periods, "?" and
    /// "!" were already filtered by token timing, so Strip removes only semicolons.
    /// </summary>
    public static string FinishPiece(string text, PunctuationMode mode, bool timedMarks = false)
    {
        var withoutFillers = RemovingFillers(text);
        var source = mode == PunctuationMode.SpokenOnly
            ? SpokenPunctuation.StripModelPunctuation(withoutFillers, timedMarks)
            : withoutFillers;
        return SpokenPunctuation.Apply(source, mode, keepsEdgeBreaks: true);
    }

    // "hmm" is two or more m's; "hm" is the single one. Longer forms come first so a word boundary
    // still decides, and "ähm" is not eaten as "äh".
    private static readonly System.Text.RegularExpressions.Regex FillerRegex = new(
        @"[ \t]*\b(?:mm-hmm|mmhmm|m-hm|mhm|hm{2,}|hm|ähm|äh|öhm|ehm)\b[,.;]?[ \t]*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex FillerSpaceRun = new(@"[ \t]{2,}");

    /// <summary>
    /// Parakeet writes fillers ("Mm-hmm", "Ähm") into the record. Whole word, case-insensitive, plus one comma, period or
    /// semicolon glued on, and the spaces around them. "Um", "ah", "aha", "achso" and "oh" stay: they are real German
    /// words. A piece of only fillers comes back "".
    /// </summary>
    private static string RemovingFillers(string text)
    {
        if (!FillerRegex.IsMatch(text)) return text;
        var replaced = FillerRegex.Replace(text, " ");
        return FillerSpaceRun.Replace(replaced, " ").Trim(' ', '\t');
    }

    /// <summary>
    /// The character before the caret in <paramref name="text"/> (a control's text, as WM_GETTEXT returns it) at
    /// <paramref name="caretIndex"/>. Unknown for an index outside the text; start of text at 0. With
    /// <paramref name="crCountsAsOneBeforeLf"/> (RichEdit: its selection offsets count a "\r\n" pair as one position) the
    /// text is walked counting each pair once, and a caret right after a pair yields '\n'.
    /// </summary>
    public static CaretContext CaretFromText(string text, int caretIndex, bool crCountsAsOneBeforeLf)
    {
        if (caretIndex < 0) return CaretContext.Unknown;
        if (!crCountsAsOneBeforeLf)
        {
            if (caretIndex > text.Length) return CaretContext.Unknown;
            return caretIndex == 0 ? CaretContext.StartOfText : CaretContext.Of(text[caretIndex - 1]);
        }
        if (caretIndex == 0) return CaretContext.StartOfText;
        var position = 0;
        var i = 0;
        while (i < text.Length)
        {
            char unit;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { unit = '\n'; i += 2; }
            else { unit = text[i]; i++; }
            position++;
            if (position == caretIndex) return CaretContext.Of(unit);
        }
        return CaretContext.Unknown;
    }

    // ---- Comparing with the whole-file decode ----

    /// <summary>
    /// Word-level difference between what was inserted piece by piece and a single decode of the whole recording,
    /// as counts only (dictations are patient notes). Case and surrounding punctuation are ignored.
    /// </summary>
    public static (int InsertedWords, int WholeFileWords, int EditDistance) Drift(string inserted, string wholeFile)
    {
        var a = Words(inserted);
        var b = Words(wholeFile);
        return (a.Count, b.Count, EditDistance(a, b));
    }

    /// <summary>Lower-case words without punctuation or symbols at their ends.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.ToLowerInvariant();
            int start = 0, end = word.Length;
            while (start < end && IsPunctuationOrSymbol(word[start])) start++;
            while (end > start && IsPunctuationOrSymbol(word[end - 1])) end--;
            if (end > start) result.Add(word[start..end]);
        }
        return result;
    }

    private static bool IsPunctuationOrSymbol(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    /// <summary>Levenshtein distance over words, two rows of memory.</summary>
    public static int EditDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0) return b.Count;
        if (b.Count == 0) return a.Count;
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) previous[j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Count; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Count];
    }

    // ---- Clipboard at the end ----

    /// <summary>
    /// What to do with the clipboard when the dictation ends. <paramref name="outcome"/> null = cancelled
    /// (Esc, or cancel while finishing).
    /// </summary>
    public static LiveClipboardEnd ClipboardAtEnd(bool restoreClipboard, bool clearAfterPaste, LiveDictationOutcome? outcome, bool fallbackHasText)
    {
        if (outcome == null)
        {
            if (restoreClipboard) return LiveClipboardEnd.RestoreBefore;
            return clearAfterPaste ? LiveClipboardEnd.KeepTextThenClear : LiveClipboardEnd.LeaveAsIs;
        }
        if (outcome.FailedPieces > 0)
            return fallbackHasText ? LiveClipboardEnd.KeepFallback : LiveClipboardEnd.LeaveAsIs;
        if (outcome.HeldText.Length > 0) return LiveClipboardEnd.KeepHeld;
        if (outcome.Text.Length > 0)
        {
            if (restoreClipboard) return LiveClipboardEnd.RestoreBefore;
            return clearAfterPaste ? LiveClipboardEnd.KeepTextThenClear : LiveClipboardEnd.KeepText;
        }
        return LiveClipboardEnd.RestoreBefore;
    }
}

/// <summary>
/// Turns decoded pieces into the exact strings to insert, append-only.
/// <list type="bullet">
/// <item><b>Spacing:</b> one space between pieces, none at the very start. The seed is the character already before the
/// caret: a letter takes that leading space ("Basistherapie" + "Ah" -> " Ah"), whitespace or a line break there does
/// not, and an unknown caret keeps the plain start. Also none after a line break, none before a piece that starts with
/// a line break (spoken "neuer Absatz" becomes one) or with closing punctuation, none on either side of a slash
/// ("Schrägstrich" said in one piece and the next word in another must still read "L4/L5").</item>
/// <item><b>A period at a breath:</b> Parakeet punctuates each piece on its own, so a pause mid-sentence can end a
/// piece with "." although the sentence goes on. A piece's final period is therefore held back and emitted with
/// the next piece: dropped when that piece starts with a lowercase letter (the sentence clearly continued), kept
/// otherwise, and flushed by <see cref="Finish"/> at Stop. Abbreviations ("z. B.", "Dr.", "ca.", numbers) are never
/// held: only a period after a word of five or more letters without inner dots is. German capitalises nouns, so a
/// capital start is weak evidence; the period is then kept, which is what not holding it would have done anyway.
/// Never held at all when <see cref="HoldsPeriods"/> is false ("Only when spoken"): every period there was said on
/// purpose.</item>
/// <item><b>A capital after a pause:</b> Parakeet capitalises the first word of every piece. With
/// <see cref="HoldsPeriods"/> false, when the text so far (the seeded character plus <see cref="Inserted"/>) is
/// non-empty and does not end (trailing spaces aside) in ". ! ? :" or a newline, that capital is taken off a function
/// word (<see cref="SpokenPunctuation.LowercasingFunctionWord"/>). Nouns and verbs keep Parakeet's capital.</item>
/// </list>
/// </summary>
public sealed class LiveChunkJoiner
{
    private string _inserted = "";
    private bool _heldPeriod;
    // One character already before the caret, or empty when the field starts there or the caret could not be read.
    // Spacing and the spoken-only lower-case check see this plus _inserted; Inserted does not include it.
    private string _precedingText = "";

    /// <summary>Everything handed out so far, held marks included once emitted (not the seeded character).</summary>
    public string Inserted => _inserted;

    /// <summary>False in "Only when spoken" mode: no period is ever held back.</summary>
    public bool HoldsPeriods { get; set; } = true;

    /// <summary>Characters that never get a space in front.</summary>
    public static readonly HashSet<char> ClosingPunctuation = new() { ',', '.', ';', ':', '!', '?', ')', ']', '»', '“', '…' };

    /// <summary>The character already before the caret. Start of text and unknown leave the first piece unspaced.</summary>
    public void Seed(CaretContext context) =>
        _precedingText = context.Kind == CaretKind.Character ? context.Character.ToString() : "";

    /// <summary>The text to insert for <paramref name="piece"/> (possibly empty).</summary>
    public string Next(string piece)
    {
        // Only spaces and tabs, like Swift's .whitespaces: a line break the speaker asked for stays.
        var body = piece.Trim(' ', '\t');
        if (body.Length == 0) return "";

        if (!HoldsPeriods) body = LowercaseLeadingFunctionWord(body, _precedingText + _inserted);

        var output = new System.Text.StringBuilder();
        if (_heldPeriod)
        {
            _heldPeriod = false;
            if (!StartsWithLowercaseLetter(body)) output.Append('.');
        }
        var before = _precedingText + _inserted + output;
        char first = body[0];
        // Whitespace (a space or a line break already in the field, or a break this joiner wrote) already separates
        // the next piece, so it must not gain a second space.
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && before[^1] != '/'
            && !IsNewline(first) && first != '/' && !ClosingPunctuation.Contains(first))
            output.Append(' ');
        if (HoldsPeriods && HoldsFinalPeriod(body))
        {
            body = body[..^1];
            _heldPeriod = true;
        }
        output.Append(body);
        var result = output.ToString();
        _inserted += result;
        return result;
    }

    /// <summary>At Stop: the period still held back, if any.</summary>
    public string Finish()
    {
        if (!_heldPeriod) return "";
        _heldPeriod = false;
        _inserted += ".";
        return ".";
    }

    internal static bool IsNewline(char c) =>
        c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';

    // Parakeet capitalises the first word after a pause. Mid-sentence, in spoken-only mode, that capital belongs only
    // on a noun or a verb.
    private static string LowercaseLeadingFunctionWord(string body, string inserted)
    {
        var prior = inserted.Trim(' ', '\t');
        if (prior.Length == 0) return body;
        var last = prior[^1];
        if (IsNewline(last) || last is '.' or '!' or '?' or ':') return body;
        var split = -1;
        for (var i = 0; i < body.Length; i++)
            if (char.IsWhiteSpace(body[i])) { split = i; break; }
        var word = split < 0 ? body : body[..split];
        var lowered = SpokenPunctuation.LowercasingFunctionWord(word);
        if (lowered == null) return body;
        return split < 0 ? lowered : lowered + body[split..];
    }

    /// <summary>True for a letter that is lower case.</summary>
    public static bool StartsWithLowercaseLetter(string text) =>
        text.Length > 0 && char.IsLetter(text[0]) && char.IsLower(text[0]);

    /// <summary>True when the piece's final period may be a breath rather than a sentence end.</summary>
    public static bool HoldsFinalPeriod(string text)
    {
        if (!text.EndsWith('.') || text.EndsWith("..", StringComparison.Ordinal)) return false;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var word = words[^1][..^1];
        if (word.Contains('.')) return false;
        int letters = 0;
        foreach (var c in word)
        {
            if (char.IsLetter(c)) letters++;
            else if (c != '-') return false;
        }
        return letters >= 5;
    }
}

/// <summary>
/// One live dictation: pieces in (WAV chunks, in order), text at the cursor out.
/// <para>
/// Pieces are decoded and inserted strictly one after another on one chain, so the text lands in speaking order
/// even when a decode is slow. Decoding and inserting are passed in, which keeps this type free of WinForms:
/// <c>decode</c> turns a WAV into text ("" for no speech); <c>insert</c> puts text at the cursor and returns false
/// when it must not right now (the window the user dictated into is not in front). That text is held, never typed
/// into another window, and goes in with the next piece, on <see cref="RetryHeld"/>, or is handed back by
/// <see cref="FinishAsync"/> for the clipboard.
/// </para>
/// <para>
/// A piece that fails to decode is skipped and counted; the outcome reports the count so the caller can fall back to
/// a whole-file decode instead of leaving a silent gap in the document.
/// </para>
/// <para>
/// Threading: all public methods are called from one thread (the UI thread in the app). The chain's steps resume on
/// the caller's SynchronizationContext, so state is only touched on that thread. No ConfigureAwait(false) here.
/// </para>
/// </summary>
public sealed class LiveDictationSession
{
    private readonly Func<byte[], CancellationToken, Task<string>> _decode;
    private readonly Func<string, Task<bool>> _insert;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LiveChunkJoiner _joiner = new();
    private string _pending = "";
    private Task _chain = Task.CompletedTask;
    private bool _anyPieceAdded;
    private int _pieces;
    private int _failedPieces;
    private bool _retryQueued;

    /// <summary>
    /// Creates a session with the decoder and the inserter. <paramref name="holdsPeriods"/> is false in "Only when
    /// spoken" mode, where the joiner must never hold a period back. <paramref name="caret"/> is the character already
    /// before the cursor; the default (Unknown) spaces the first piece as before.
    /// </summary>
    public LiveDictationSession(Func<byte[], CancellationToken, Task<string>> decode, Func<string, Task<bool>> insert,
        bool holdsPeriods = true, CaretContext caret = default)
    {
        _decode = decode;
        _insert = insert;
        _joiner.HoldsPeriods = holdsPeriods;
        _joiner.Seed(caret);
    }

    /// <summary>True after <see cref="Cancel"/>.</summary>
    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>Text decoded but not inserted yet (another window was in front).</summary>
    public bool HasHeldText => _pending.Length > 0;

    /// <summary>A cut chunk (mid-recording) or the tail chunk (at stop). Silent chunks are skipped.</summary>
    public void Add(byte[] wav, bool silent, LivePieceInfo? info = null)
    {
        if (IsCancelled) return;
        _anyPieceAdded = true;
        if (silent)
        {
            Log.Info("LIVE-DICTATE: silent piece skipped");
            if (info != null) Log.Info($"LIVE-DIAG: silent piece reason={info.Reason} audioS={AudioSeconds(wav)}");
            return;
        }
        Enqueue(() => ProcessAsync(wav, info));
    }

    private static string AudioSeconds(byte[] wav) =>
        Wav.DurationSeconds(wav).ToString("0.00", CultureInfo.InvariantCulture);

    // DIAG: RMS level of the whole piece in dBFS and the quietest/loudest 0.2 s window.
    private static string LevelSummary(byte[] wav)
    {
        if (!Wav.TryGetPcm16(wav, out var pcm) || pcm.Channels != 1) return "n/a";
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(wav.AsSpan(pcm.DataOffset, pcm.DataLength & ~1));
        var win = Math.Max(1, pcm.SampleRate / 5);
        float lo = 0, hi = -160;
        bool any = false;
        for (int i = 0; i + win <= samples.Length; i += win)
        {
            var db = MeetingAudioMixer.RmsDb(samples.Slice(i, win));
            if (!any || db < lo) lo = db;
            if (db > hi) hi = db;
            any = true;
        }
        var all = MeetingAudioMixer.RmsDb(samples);
        string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        return any ? $"rmsDb={F(all)} quietWindowDb={F(lo)} loudWindowDb={F(hi)}" : $"rmsDb={F(all)}";
    }

    /// <summary>The window the user dictated into may be in front again: try to insert held text.</summary>
    public void RetryHeld()
    {
        // One retry in the chain at a time: the app's timer asks every 500 ms, and an insert that keeps
        // refusing (elevated window, a key held) must not pile up flush steps.
        if (IsCancelled || _pending.Length == 0 || _retryQueued) return;
        _retryQueued = true;
        Enqueue(async () =>
        {
            _retryQueued = false;
            await FlushAsync();
        });
    }

    /// <summary>
    /// Decodes and inserts what is left, then reports the whole dictation. <paramref name="deliveredWav"/> is the
    /// recorder's final file: when nothing was cut (a short dictation) it is the only piece; otherwise the tail
    /// already arrived through <see cref="Add"/>. Throws <see cref="OperationCanceledException"/> after <see cref="Cancel"/>.
    /// </summary>
    public async Task<LiveDictationOutcome> FinishAsync(byte[] deliveredWav)
    {
        if (!_anyPieceAdded) Add(deliveredWav, false, new LivePieceInfo("Stop", Stopwatch.GetTimestamp()));
        // The held-back period goes through the same chain as everything else, so a RetryHeld from a
        // window switch can never flush the same text a second time.
        Enqueue(async () =>
        {
            var tail = _joiner.Finish();
            if (tail.Length == 0) return;
            _pending += tail;
            await FlushAsync();
        });
        await DrainAsync();
        if (IsCancelled) throw new OperationCanceledException();
        var outcome = new LiveDictationOutcome(_joiner.Inserted, _pending, _pieces, _failedPieces);
        Log.Info($"LIVE-DICTATE: finished pieces={_pieces} failed={_failedPieces} chars={outcome.Text.Length} "
            + $"heldChars={_pending.Length} durationS={_clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");
        return outcome;
    }

    /// <summary>Stops decoding and inserting. Text already inserted stays where it is: it is in another window.</summary>
    public void Cancel()
    {
        if (IsCancelled) return;
        _cts.Cancel();
        Log.Info($"LIVE-DICTATE: session cancelled after {_pieces} piece(s); inserted text stays");
    }

    /// <summary>Waits until every step queued so far, and any queued while waiting, has run.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            var current = _chain;
            await current;
            if (ReferenceEquals(current, _chain)) return;
        }
    }

    // ---- Chain ----

    private void Enqueue(Func<Task> step) => _chain = RunAfter(_chain, step);

    private async Task RunAfter(Task previous, Func<Task> step)
    {
        await previous; // never faults: every step is guarded below
        if (IsCancelled) return;
        try
        {
            await step();
        }
        catch (OperationCanceledException)
        {
            // cancelled mid-step: nothing more to do
        }
        catch (Exception e)
        {
            Log.Warn($"LIVE-DICTATE: a step failed ({e.GetType().Name})");
        }
    }

    private async Task ProcessAsync(byte[] wav, LivePieceInfo? info)
    {
        _pieces++;
        int index = _pieces;
        var started = Stopwatch.StartNew();
        long queuedMs = info == null ? -1 : (long)Stopwatch.GetElapsedTime(info.CutTimestamp).TotalMilliseconds;
        string text;
        try
        {
            text = await _decode(wav, _cts.Token);
        }
        catch (Exception e)
        {
            if (IsCancelled || e is OperationCanceledException) return;
            _failedPieces++;
            Log.Warn($"LIVE-DICTATE: piece {index} failed to decode ({e.GetType().Name}); the whole-file fallback will cover it");
            return;
        }
        if (IsCancelled) return;
        var piece = _joiner.Next(text);
        _pending += piece;
        long decodeMs = started.ElapsedMilliseconds;
        Log.Info($"LIVE-DICTATE: piece {index} decodeMs={decodeMs} chars={piece.Length}");
        var insertWatch = Stopwatch.StartNew();
        int heldBefore = _pending.Length - piece.Length;
        await FlushAsync();
        if (info != null)
            Log.Info($"LIVE-DIAG: piece {index} reason={info.Reason} audioS={AudioSeconds(wav)} {LevelSummary(wav)} "
                + $"waitedForChainMs={Math.Max(0, queuedMs - decodeMs)} decodeMs={decodeMs} insertMs={insertWatch.ElapsedMilliseconds} "
                + $"cutToInsertedMs={(long)Stopwatch.GetElapsedTime(info.CutTimestamp).TotalMilliseconds} "
                + $"rawChars={text.Length} pieceChars={piece.Length} heldBefore={Math.Max(0, heldBefore)} "
                + $"inserted={(_pending.Length == 0).ToString().ToLowerInvariant()}");
    }

    private async Task FlushAsync()
    {
        if (_pending.Length == 0 || IsCancelled) return;
        var text = _pending;
        bool inserted;
        try
        {
            inserted = await _insert(text);
        }
        catch (Exception e)
        {
            Log.Warn($"LIVE-DICTATE: insert threw ({e.GetType().Name}); text is held");
            inserted = false;
        }
        if (inserted)
        {
            // Only what was handed to insert. The chain is serial, so pending still starts with it.
            _pending = _pending.StartsWith(text, StringComparison.Ordinal) ? _pending[text.Length..] : "";
        }
        else
        {
            Log.Info($"LIVE-DICTATE: holding {_pending.Length} chars, the dictated-into window is not in front");
        }
    }
}
