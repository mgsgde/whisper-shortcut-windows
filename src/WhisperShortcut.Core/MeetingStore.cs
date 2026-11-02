using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>Where a meeting is in its life.</summary>
public enum MeetingStatus
{
    /// <summary>Audio is being recorded right now.</summary>
    Recording,

    /// <summary>Stopped normally.</summary>
    Ended,

    /// <summary>The app was killed while recording; found again at the next start.</summary>
    Interrupted,
}

/// <summary>Metadata of one stored meeting (from meeting.json plus a look at its folder).</summary>
/// <param name="Id">Folder name, e.g. "Meeting-2026-11-02-141500".</param>
/// <param name="Title">Display title.</param>
/// <param name="StartedAt">When the recording began.</param>
/// <param name="EndedAt">When it stopped; null while recording.</param>
/// <param name="Status">Recording, ended or interrupted.</param>
/// <param name="AudioMode">The audio mode string given at <see cref="MeetingStore.Create"/> (e.g. "mic+system").</param>
/// <param name="DurationSeconds">Audio recorded so far.</param>
/// <param name="TranscriptRevision">Counts rewrites of transcript.md (a late chunk spliced in); appends do not change it.</param>
/// <param name="PendingAudioChunks">Chunks whose audio is kept because transcription failed.</param>
/// <param name="HasSummary">summary.md exists.</param>
/// <param name="Folder">Absolute folder path.</param>
public sealed record MeetingInfo(string Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    MeetingStatus Status, string AudioMode, double DurationSeconds, int TranscriptRevision,
    int PendingAudioChunks, bool HasSummary, string Folder);

/// <summary>One "[mm:ss] text" block of a transcript.</summary>
/// <param name="StartSeconds">Position in the meeting.</param>
/// <param name="Text">Trimmed text of the block.</param>
public sealed record TranscriptChunk(double StartSeconds, string Text)
{
    /// <summary>"[mm:ss]", minutes not wrapped at 60.</summary>
    public string Timestamp => MeetingStore.FormatTimestamp(StartSeconds);
}

/// <summary>A search match.</summary>
/// <param name="MeetingId">Meeting the hit is in.</param>
/// <param name="Title">That meeting's title.</param>
/// <param name="StartedAt">That meeting's start.</param>
/// <param name="StartSeconds">Position of the chunk; null = hit in title or summary.</param>
/// <param name="Snippet">Short text around the match.</param>
public sealed record MeetingSearchHit(string MeetingId, string Title, DateTimeOffset StartedAt,
    double? StartSeconds, string Snippet);

/// <summary>
/// Result of <see cref="MeetingStore.ReadSince"/>. <paramref name="Restarted"/> = the transcript was rewritten since
/// the caller's revision (a late chunk was spliced in), so <paramref name="Text"/> holds the whole transcript from offset 0.
/// </summary>
/// <param name="Text">The new text.</param>
/// <param name="NextOffset">Byte offset (UTF-8) to pass next time.</param>
/// <param name="Revision">Current transcript revision; pass it back next time.</param>
/// <param name="Restarted">Text starts at offset 0 instead of the requested offset.</param>
/// <param name="IsLive">The meeting is still recording.</param>
public sealed record LiveTranscriptRead(string Text, long NextOffset, int Revision, bool Restarted, bool IsLive);

/// <summary>Audio of a chunk that could not be transcribed yet.</summary>
/// <param name="StartSeconds">Where the chunk belongs in the meeting.</param>
/// <param name="Path">The WAV file.</param>
public sealed record PendingAudio(double StartSeconds, string Path);

/// <summary>
/// On-disk store of Live Meetings: <c>root/id/meeting.json, transcript.md, summary.md, audio-pending/</c>.
/// Thread-safe within the process (one lock). Another process (the MCP server) may read while this one writes:
/// files are opened with full sharing and meeting.json is replaced atomically.
/// </summary>
public sealed class MeetingStore
{
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;
    private const string MeetingFile = "meeting.json";
    private const string TranscriptFile = "transcript.md";
    private const string SummaryFile = "summary.md";
    private const string PendingFolder = "audio-pending";

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex MarkerRegex = new(@"^\[(\d+):(\d{2})\]", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PendingNameRegex = new(@"^chunk-(\d{10})\.wav$", RegexOptions.Compiled);
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Readable for other tools: "mic+system" and umlauts in titles stay as they are.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    /// <summary>Default location: %LOCALAPPDATA%\WhisperShortcut\Meetings.</summary>
    public static string DefaultRoot => Path.Combine(AppData.LocalFolder, "Meetings");

    /// <summary>Uses (and lazily creates) <paramref name="root"/>.</summary>
    public MeetingStore(string root) => Root = root;

    /// <summary>The folder that holds one sub-folder per meeting.</summary>
    public string Root { get; }

    // ---------------------------------------------------------------- read API

    /// <summary>All meetings, newest first. Folders without a readable meeting.json are skipped.</summary>
    public IReadOnlyList<MeetingInfo> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root)) return Array.Empty<MeetingInfo>();
            var result = new List<MeetingInfo>();
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var info = Get(Path.GetFileName(dir));
                if (info != null) result.Add(info);
            }
            return result.OrderByDescending(m => m.StartedAt).ToList();
        }
    }

    /// <summary>One meeting; null for an unknown or invalid id.</summary>
    public MeetingInfo? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate)
        {
            var folder = Path.Combine(Root, id);
            var dto = ReadDto(folder);
            if (dto == null) return null;
            return ToInfo(id, folder, dto);
        }
    }

    /// <summary>The newest meeting that is recording, or null.</summary>
    public MeetingInfo? Live() => List().FirstOrDefault(m => m.Status == MeetingStatus.Recording);

    /// <summary>The whole transcript.md; null for an unknown meeting.</summary>
    public string? ReadTranscript(string id)
    {
        lock (_gate)
        {
            if (Get(id) == null) return null;
            return ReadText(Path.Combine(Root, id, TranscriptFile)) ?? "";
        }
    }

    /// <summary>The transcript parsed into blocks (empty for an unknown meeting).</summary>
    public IReadOnlyList<TranscriptChunk> ReadChunks(string id) =>
        ReadTranscript(id) is { } text ? ParseTranscript(text) : Array.Empty<TranscriptChunk>();

    /// <summary>summary.md, or null when there is none.</summary>
    public string? ReadSummary(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate) return ReadText(Path.Combine(Root, id, SummaryFile));
    }

    /// <summary>
    /// Case-insensitive search: a hit when every whitespace-separated word of <paramref name="query"/> occurs in the
    /// chunk text (or in the title / summary). Newest meeting first, chunks in time order. A blank query finds nothing.
    /// </summary>
    public IReadOnlyList<MeetingSearchHit> Search(string query, int maxResults = 50)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || maxResults <= 0) return Array.Empty<MeetingSearchHit>();
        var hits = new List<MeetingSearchHit>();
        lock (_gate)
        {
            foreach (var m in List())
            {
                if (ContainsAll(m.Title, words))
                    hits.Add(new(m.Id, m.Title, m.StartedAt, null, Snippet(m.Title, words[0])));
                if (hits.Count >= maxResults) break;

                var summary = ReadSummary(m.Id);
                if (summary != null && ContainsAll(summary, words))
                    hits.Add(new(m.Id, m.Title, m.StartedAt, null, Snippet(summary, words[0])));
                if (hits.Count >= maxResults) break;

                foreach (var c in ReadChunks(m.Id))
                {
                    if (!ContainsAll(c.Text, words)) continue;
                    hits.Add(new(m.Id, m.Title, m.StartedAt, c.StartSeconds, Snippet(c.Text, words[0])));
                    if (hits.Count >= maxResults) break;
                }
                if (hits.Count >= maxResults) break;
            }
        }
        return hits;
    }

    /// <summary>
    /// Reads transcript.md from byte <paramref name="offset"/>. An offset outside the file, or a
    /// <paramref name="revision"/> (when &gt;= 0) other than the current one, restarts at 0 and sets Restarted.
    /// The read stops at the last complete "\n", so it never splits a UTF-8 sequence or a half-written block.
    /// Unknown id: null.
    /// </summary>
    public LiveTranscriptRead? ReadSince(string id, long offset, int revision = -1)
    {
        lock (_gate)
        {
            var info = Get(id);
            if (info == null) return null;
            var isLive = info.Status == MeetingStatus.Recording;
            var path = Path.Combine(Root, id, TranscriptFile);
            byte[] bytes;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
                var length = fs.Length;
                var restarted = offset < 0 || offset > length || (revision >= 0 && revision != info.TranscriptRevision);
                var start = restarted ? 0 : offset;
                var count = (int)(length - start);
                bytes = new byte[count];
                fs.Position = start;
                var read = 0;
                while (read < count)
                {
                    var n = fs.Read(bytes, read, count - read);
                    if (n == 0) break;
                    read += n;
                }
                var cut = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, read - 1), read);
                var keep = read == 0 || cut < 0 ? 0 : cut + 1;
                return new LiveTranscriptRead(Utf8.GetString(bytes, 0, keep), start + keep,
                    info.TranscriptRevision, restarted, isLive);
            }
            catch (FileNotFoundException)
            {
                return new LiveTranscriptRead("", 0, info.TranscriptRevision, offset != 0, isLive);
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>Folder of a meeting; throws <see cref="ArgumentException"/> for an invalid id.</summary>
    public string FolderOf(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid meeting id.", nameof(id));
        return Path.Combine(Root, id);
    }

    /// <summary>
    /// Parses "[mm:ss] text" blocks: a marker is "^\[(\d+):(\d{2})\]" at the start of a line, the body runs to the next
    /// marker and is trimmed; empty bodies are skipped and anything before the first marker (the header) is ignored.
    /// </summary>
    public static IReadOnlyList<TranscriptChunk> ParseTranscript(string text)
    {
        var result = new List<TranscriptChunk>();
        if (string.IsNullOrEmpty(text)) return result;
        var matches = MarkerRegex.Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var bodyStart = m.Index + m.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = text[bodyStart..bodyEnd].Trim();
            if (body.Length == 0) continue;
            var seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            result.Add(new TranscriptChunk(seconds, body));
        }
        return result;
    }

    /// <summary>"[mm:ss]" for a position in the meeting; minutes may exceed 59 ("[75:03]").</summary>
    public static string FormatTimestamp(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"[{total / 60:00}:{total % 60:00}]");
    }

    // ---------------------------------------------------------------- write API

    /// <summary>Creates the folder (id "Meeting-yyyy-MM-dd-HHmmss", "-2", "-3" … when taken), meeting.json and the transcript header.</summary>
    public MeetingInfo Create(DateTimeOffset startedAt, string audioMode, string? title = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            var local = startedAt.ToLocalTime();
            var stem = "Meeting-" + local.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
            var id = stem;
            for (var n = 2; Directory.Exists(Path.Combine(Root, id)); n++) id = stem + "-" + n;
            var folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);

            var stamp = local.ToString("dd.MM.yyyy HH:mm", German);
            var dto = new Dto
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(title) ? "Meeting " + stamp : title.Trim(),
                StartedAt = startedAt,
                EndedAt = null,
                Status = "recording",
                AudioMode = audioMode,
                DurationSeconds = 0,
                TranscriptRevision = 0,
            };
            WriteTextAtomic(Path.Combine(folder, TranscriptFile), "# Meeting " + stamp + "\n\n");
            WriteDto(folder, dto);
            return ToInfo(id, folder, dto);
        }
    }

    /// <summary>
    /// Adds a block. At or after the last block's start it is appended; otherwise it is inserted in time order
    /// (before an existing block of the same second), transcript.md is rewritten atomically and the revision grows.
    /// Blank text does nothing.
    /// </summary>
    public void AppendChunk(string id, double startSeconds, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var folder = FolderOf(id);
        var block = FormatTimestamp(startSeconds) + " " + text.Trim() + "\n\n";
        lock (_gate)
        {
            var path = Path.Combine(folder, TranscriptFile);
            var existing = ReadText(path) ?? "";
            var chunks = ParseTranscript(existing);
            if (chunks.Count == 0 || startSeconds >= chunks[^1].StartSeconds)
            {
                // The header ends in a blank line, but a hand-edited file might not: keep blocks separated.
                var prefix = existing.Length == 0 || existing.EndsWith("\n\n", StringComparison.Ordinal) ? "" :
                    existing.EndsWith('\n') ? "\n" : "\n\n";
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, Share);
                var bytes = Utf8.GetBytes(prefix + block);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }

            var floor = Math.Floor(startSeconds);
            var at = chunks.Count;
            for (var i = 0; i < chunks.Count; i++)
            {
                if (chunks[i].StartSeconds >= floor) { at = i; break; }
            }
            var list = chunks.ToList();
            list.Insert(at, new TranscriptChunk(startSeconds, text.Trim()));

            var first = MarkerRegex.Match(existing);
            var header = first.Success ? existing[..first.Index] : existing;
            var sb = new StringBuilder(header);
            foreach (var c in list) sb.Append(FormatTimestamp(c.StartSeconds)).Append(' ').Append(c.Text).Append("\n\n");

            // Revision first: a reader holding the old one then restarts (harmless) instead of reading new text at an old offset.
            Update(id, d => d.TranscriptRevision++);
            WriteTextAtomic(path, sb.ToString());
        }
    }

    /// <summary>Writes summary.md.</summary>
    public void SaveSummary(string id, string markdown)
    {
        var folder = FolderOf(id);
        lock (_gate) WriteTextAtomic(Path.Combine(folder, SummaryFile), markdown);
    }

    /// <summary>Records how much audio the meeting holds so far.</summary>
    public void SetDuration(string id, double seconds) => Update(id, d => d.DurationSeconds = seconds);

    /// <summary>Renames the meeting.</summary>
    public void SetTitle(string id, string title) => Update(id, d => d.Title = title.Trim());

    /// <summary>Marks the meeting as ended at <paramref name="endedAt"/>.</summary>
    public void MarkEnded(string id, DateTimeOffset endedAt) => Update(id, d =>
    {
        d.Status = "ended";
        d.EndedAt = endedAt;
    });

    /// <summary>
    /// Meetings still marked Recording (except <paramref name="exceptId"/>) belong to a killed app: they become
    /// Interrupted, EndedAt is the newest write time of their files, and leftover .pcm partials turn into .wav
    /// (empty ones are deleted). Returns the meetings changed.
    /// </summary>
    public IReadOnlyList<MeetingInfo> RecoverInterrupted(string? exceptId = null)
    {
        var changed = new List<MeetingInfo>();
        lock (_gate)
        {
            foreach (var m in List())
            {
                if (m.Status != MeetingStatus.Recording || m.Id == exceptId) continue;
                // Measure before converting: the new .wav files are written now and would hide the real end.
                var lastWrite = NewestWriteTime(m.Folder);
                ConvertPartials(m.Folder);
                Update(m.Id, d =>
                {
                    d.Status = "interrupted";
                    d.EndedAt = lastWrite;
                });
                var info = Get(m.Id);
                if (info != null) changed.Add(info);
            }
        }
        if (changed.Count > 0) Log.Info($"Meetings: {changed.Count} interrupted meeting(s) recovered");
        return changed;
    }

    // ---------------------------------------------------------------- untranscribed audio

    /// <summary>Stores the audio of a chunk that is not transcribed yet (atomic; replaces a file of the same start).</summary>
    public void KeepPendingAudio(string id, double startSeconds, byte[] wav)
    {
        var dir = PendingDir(id);
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            WriteBytesAtomic(Path.Combine(dir, PendingFileName(startSeconds)), wav);
        }
    }

    /// <summary>The kept .wav chunks of a meeting, oldest first.</summary>
    public IReadOnlyList<PendingAudio> ListPendingAudio(string id)
    {
        if (!IsValidId(id)) return Array.Empty<PendingAudio>();
        var dir = PendingDir(id);
        lock (_gate)
        {
            if (!Directory.Exists(dir)) return Array.Empty<PendingAudio>();
            var result = new List<PendingAudio>();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var start = StartSecondsFromFileName(Path.GetFileName(file));
                if (start != null) result.Add(new PendingAudio(start.Value, file));
            }
            return result.OrderBy(p => p.StartSeconds).ToList();
        }
    }

    /// <summary>Deletes a chunk's WAV, and the audio-pending folder when nothing is left in it.</summary>
    public void RemovePendingAudio(string id, double startSeconds)
    {
        var dir = PendingDir(id);
        lock (_gate)
        {
            DeleteQuietly(Path.Combine(dir, PendingFileName(startSeconds)));
            DeleteDirIfEmpty(dir);
        }
    }

    /// <summary>Ids of meetings that keep untranscribed audio, oldest first.</summary>
    public IReadOnlyList<string> IdsWithPendingAudio()
    {
        lock (_gate)
        {
            return List()
                .Where(m => m.PendingAudioChunks > 0)
                .OrderBy(m => m.StartedAt)
                .Select(m => m.Id)
                .ToList();
        }
    }

    /// <summary>
    /// Opens the raw 16-bit PCM partial of the chunk being recorded (appending), so a killed app keeps its audio.
    /// The caller writes, flushes and disposes.
    /// </summary>
    public FileStream OpenPartialAudio(string id, double startSeconds)
    {
        var dir = PendingDir(id);
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            return new FileStream(Path.Combine(dir, PartialFileName(startSeconds)), FileMode.Append, FileAccess.Write, Share);
        }
    }

    /// <summary>Deletes the partial of a chunk (it was completed or discarded).</summary>
    public void DeletePartialAudio(string id, double startSeconds)
    {
        var dir = PendingDir(id);
        lock (_gate)
        {
            DeleteQuietly(Path.Combine(dir, PartialFileName(startSeconds)));
            DeleteDirIfEmpty(dir);
        }
    }

    /// <summary>File name of a kept chunk: "chunk-" and the start in milliseconds, 10 digits, ".wav".</summary>
    public static string PendingFileName(double startSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"chunk-{(long)Math.Round(Math.Max(0, startSeconds) * 1000):D10}.wav");

    /// <summary>Inverse of <see cref="PendingFileName"/>; null for any other name.</summary>
    public static double? StartSecondsFromFileName(string name)
    {
        var m = PendingNameRegex.Match(name);
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0 : null;
    }

    // ---------------------------------------------------------------- helpers

    private static string PartialFileName(double startSeconds) => Path.ChangeExtension(PendingFileName(startSeconds), ".pcm");

    private string PendingDir(string id) => Path.Combine(FolderOf(id), PendingFolder);

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && !id.Contains('/') && !id.Contains('\\') && !id.Contains("..", StringComparison.Ordinal)
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool ContainsAll(string text, string[] words) =>
        words.All(w => text.Contains(w, StringComparison.InvariantCultureIgnoreCase));

    private static string Snippet(string text, string firstWord)
    {
        const int Max = 160;
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length <= Max) return flat;
        var idx = flat.IndexOf(firstWord, StringComparison.InvariantCultureIgnoreCase);
        if (idx < 0) idx = 0;
        var start = Math.Max(0, idx - 60);
        var end = Math.Min(flat.Length, start + Max);
        start = Math.Max(0, end - Max);
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }

    private MeetingInfo ToInfo(string id, string folder, Dto dto)
    {
        var pending = 0;
        try
        {
            var dir = Path.Combine(folder, PendingFolder);
            if (Directory.Exists(dir))
                pending = Directory.EnumerateFiles(dir, "*.wav").Count(f => StartSecondsFromFileName(Path.GetFileName(f)) != null);
        }
        catch (IOException) { }
        var status = dto.Status?.ToLowerInvariant() switch
        {
            "recording" => MeetingStatus.Recording,
            "interrupted" => MeetingStatus.Interrupted,
            _ => MeetingStatus.Ended,
        };
        return new MeetingInfo(id, dto.Title ?? id, dto.StartedAt, dto.EndedAt, status, dto.AudioMode ?? "",
            dto.DurationSeconds, dto.TranscriptRevision, pending, File.Exists(Path.Combine(folder, SummaryFile)), folder);
    }

    private static Dto? ReadDto(string folder)
    {
        try
        {
            var text = ReadText(Path.Combine(folder, MeetingFile));
            return text == null ? null : JsonSerializer.Deserialize<Dto>(text, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteDto(string folder, Dto dto) =>
        WriteTextAtomic(Path.Combine(folder, MeetingFile), JsonSerializer.Serialize(dto, Json));

    private void Update(string id, Action<Dto> change)
    {
        var folder = FolderOf(id);
        lock (_gate)
        {
            var dto = ReadDto(folder) ?? throw new FileNotFoundException("Unknown meeting " + id);
            change(dto);
            WriteDto(folder, dto);
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
            using var reader = new StreamReader(fs, Utf8);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void WriteTextAtomic(string path, string text) => WriteBytesAtomic(path, Utf8.GetBytes(text));

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, Share))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Meetings: could not delete {Path.GetFileName(path)}"); }
    }

    private static void DeleteDirIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static DateTimeOffset NewestWriteTime(string folder)
    {
        var newest = DateTime.MinValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newest) newest = t;
            }
        }
        catch (IOException) { }
        return newest == DateTime.MinValue ? DateTimeOffset.UtcNow : new DateTimeOffset(DateTime.SpecifyKind(newest, DateTimeKind.Utc));
    }

    /// <summary>Turns leftover raw .pcm partials into playable .wav files; an empty partial is deleted.</summary>
    private static void ConvertPartials(string folder)
    {
        var dir = Path.Combine(folder, PendingFolder);
        if (!Directory.Exists(dir)) return;
        foreach (var pcm in Directory.EnumerateFiles(dir, "*.pcm").ToList())
        {
            try
            {
                var raw = File.ReadAllBytes(pcm);
                var wavPath = Path.ChangeExtension(pcm, ".wav");
                // A .wav of the same chunk already exists when the app died between keeping it and deleting the partial: it is complete.
                if (raw.Length >= 2 && !File.Exists(wavPath))
                {
                    var samples = new short[raw.Length / 2];
                    for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2, 2));
                    WriteBytesAtomic(wavPath, Wav.Encode16(samples, 16000));
                }
                File.Delete(pcm);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Meetings: partial {Path.GetFileName(pcm)} could not be converted");
            }
        }
        DeleteDirIfEmpty(dir);
    }

    private sealed class Dto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("audioMode")] public string? AudioMode { get; set; }
        [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }
        [JsonPropertyName("transcriptRevision")] public int TranscriptRevision { get; set; }
    }
}
