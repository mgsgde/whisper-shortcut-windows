using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WhisperShortcut.Core;

/// <summary>A leftover recording after a crash or kill, ready to transcribe again.</summary>
public sealed record RecoveredRecording(byte[] Wav, string Mode, DateTimeOffset StartedUtc, double Seconds);

/// <summary>
/// Writes the live recording to a WAV on disk so a crash or a kill does not lose it.
/// One take only: overwritten on the next start, deleted after success, Esc, or a leftover shorter
/// than <see cref="MinKeep"/>. Patient audio: the log gets sizes, never content.
/// </summary>
public sealed class RecordingCheckpoint : IDisposable
{
    /// <summary>WAV of the take that is still running or was interrupted.</summary>
    public const string FileName = "in-progress.wav";

    /// <summary>JSON next to the WAV: mode and start time.</summary>
    public const string MetaFileName = "in-progress.json";

    /// <summary>Shorter leftovers are discarded: the same 0.5 s floor as a double-press.</summary>
    public static readonly TimeSpan MinKeep = TimeSpan.FromMilliseconds(500);

    /// <summary>%LOCALAPPDATA%\WhisperShortcut\unfinished.</summary>
    public static string DefaultDirectory => Path.Combine(AppData.LocalFolder, "unfinished");

    private FileStream? _stream;
    private readonly string _directory;
    private readonly int _sampleRate;
    private readonly int _channels;
    private int _pcmBytes;
    private bool _failed;
    private bool _discarded;

    private RecordingCheckpoint(FileStream stream, string directory, int sampleRate, int channels)
    {
        _stream = stream;
        _directory = directory;
        _sampleRate = sampleRate;
        _channels = channels;
    }

    /// <summary>True after an append failed (usually a full disk); further appends are skipped.</summary>
    public bool Failed => _failed;

    /// <summary>
    /// Opens and overwrites the spill file. Null when the disk refuses the write: the caller keeps going in memory.
    /// </summary>
    public static RecordingCheckpoint? TryBegin(string directory, string mode, int sampleRate = 16000, int channels = 1)
    {
        if (sampleRate <= 0 || channels <= 0) return null;
        FileStream? fs = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            WriteHeader(fs, sampleRate, channels, 0);
            fs.Flush(flushToDisk: true);
            var meta = new MetaDto
            {
                Mode = string.IsNullOrWhiteSpace(mode) ? "Dictate" : mode.Trim(),
                StartedUtc = DateTime.UtcNow,
            };
            File.WriteAllText(Path.Combine(directory, MetaFileName), JsonSerializer.Serialize(meta));
            var created = new RecordingCheckpoint(fs, directory, sampleRate, channels);
            fs = null;
            return created;
        }
        catch (Exception ex)
        {
            fs?.Dispose();
            Log.Error("Recording checkpoint: begin failed", ex);
            return null;
        }
    }

    /// <summary>Appends PCM. False on disk-full or IO error: further appends are skipped.</summary>
    public bool TryAppend(ReadOnlySpan<byte> pcm)
    {
        if (_failed || _stream == null) return false;
        if (pcm.Length == 0) return true;
        try
        {
            _stream.Write(pcm);
            _pcmBytes += pcm.Length;
            _stream.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Error($"Recording checkpoint: append failed after {_pcmBytes} bytes", ex);
            return false;
        }
    }

    /// <summary>Updates the RIFF sizes and closes the file; the WAV stays for a later recover.</summary>
    public void Dispose()
    {
        var stream = _stream;
        _stream = null;
        if (stream == null) return;
        try
        {
            if (!_discarded) WriteHeader(stream, _sampleRate, _channels, _pcmBytes);
            stream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Recording checkpoint: close failed", ex);
            try { stream.Dispose(); } catch { /* already closing */ }
        }
    }

    /// <summary>Closes and deletes this take.</summary>
    public void Discard()
    {
        _discarded = true;
        Dispose();
        DeleteFiles(_directory);
    }

    /// <summary>Deletes the leftover in <paramref name="directory"/> (default: <see cref="DefaultDirectory"/>).</summary>
    public static void DiscardAll(string? directory = null) => DeleteFiles(directory ?? DefaultDirectory);

    /// <summary>
    /// Reads a leftover take. Repairs a header left at 0 because the process died before <see cref="Dispose"/>.
    /// Deletes and returns null when the file is missing, unreadable or shorter than <see cref="MinKeep"/>.
    /// A usable leftover is kept: the caller owns it until the retry succeeds.
    /// </summary>
    public static RecoveredRecording? TryRecover(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
        {
            DeleteFiles(dir);
            return null;
        }

        try
        {
            var wav = Repair(File.ReadAllBytes(path));
            if (wav == null)
            {
                Log.Info("Recording checkpoint: leftover unreadable, discarded");
                DeleteFiles(dir);
                return null;
            }

            var seconds = Wav.DurationSeconds(wav);
            if (seconds < MinKeep.TotalSeconds)
            {
                Log.Info($"Recording checkpoint: leftover {seconds:0.00} s, too short, discarded");
                DeleteFiles(dir);
                return null;
            }

            var mode = "Dictate";
            var started = DateTimeOffset.UtcNow;
            var metaPath = Path.Combine(dir, MetaFileName);
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<MetaDto>(File.ReadAllText(metaPath));
                    if (!string.IsNullOrWhiteSpace(meta?.Mode)) mode = meta!.Mode.Trim();
                    if (meta?.StartedUtc is { Year: > 2000 } t)
                        started = new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc));
                }
                catch (JsonException) { /* mode stays Dictate */ }
            }

            Log.Info($"Recording checkpoint: recovered {seconds:0.0} s, mode {mode}, {wav.Length} bytes");
            return new RecoveredRecording(wav, mode, started, seconds);
        }
        catch (Exception ex)
        {
            Log.Error("Recording checkpoint: recover failed", ex);
            DeleteFiles(dir);
            return null;
        }
    }

    /// <summary>
    /// Fixes RIFF/data sizes from the real file length. Null when this is not a 44-byte 16-bit PCM header.
    /// </summary>
    public static byte[]? Repair(byte[] raw)
    {
        if (raw.Length < 44) return null;
        if (Encoding.ASCII.GetString(raw, 0, 4) != "RIFF" || Encoding.ASCII.GetString(raw, 8, 4) != "WAVE")
            return null;
        if (Encoding.ASCII.GetString(raw, 12, 4) != "fmt ") return null;
        var format = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(20, 2));
        var channels = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(22, 2));
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(24, 4));
        var bits = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(34, 2));
        if ((format != 1 && format != 0xFFFE) || bits != 16 || channels < 1 || sampleRate <= 0)
            return null;
        if (Encoding.ASCII.GetString(raw, 36, 4) != "data") return null;

        var pcm = raw.Length - 44;
        if ((pcm & 1) != 0) pcm--;
        if (pcm < 0) return null;
        var wav = new byte[44 + pcm];
        raw.AsSpan(0, 44 + pcm).CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + pcm);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm);
        return wav;
    }

    private static void DeleteFiles(string directory)
    {
        try
        {
            var wav = Path.Combine(directory, FileName);
            var meta = Path.Combine(directory, MetaFileName);
            if (File.Exists(wav)) File.Delete(wav);
            if (File.Exists(meta)) File.Delete(meta);
        }
        catch (Exception ex)
        {
            Log.Error("Recording checkpoint: delete failed", ex);
        }
    }

    private static void WriteHeader(Stream stream, int sampleRate, int channels, int pcmBytes)
    {
        var header = new byte[44];
        var blockAlign = channels * 2;
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + pcmBytes);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), pcmBytes);
        var pos = stream.Position;
        stream.Position = 0;
        stream.Write(header);
        stream.Position = pos < 44 ? 44 : pos;
    }

    private sealed class MetaDto
    {
        public string Mode { get; set; } = "Dictate";
        public DateTime StartedUtc { get; set; }
    }
}
