using System.Buffers.Binary;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>A recorder: Start, later StopAsync returns the whole recording as an in-memory WAV file.</summary>
public interface IAudioSource
{
    /// <summary>Starts recording.</summary>
    void Start();

    /// <summary>Stops and returns the recording as WAV bytes (RIFF header included).</summary>
    Task<byte[]> StopAsync();
}

/// <summary>
/// Raised by a microphone source when the whole recording is digital silence (peak below ~-60 dBFS),
/// which usually means Windows blocks the microphone for the app.
/// </summary>
public sealed class AudioSilentException : Exception
{
    /// <summary>Creates the error with the German user message.</summary>
    public AudioSilentException()
        : base("Mikrofon liefert Stille. Bitte in den Windows-Einstellungen unter Datenschutz (und Sicherheit) → Mikrofon → Desktop-Apps den Zugriff erlauben.") { }
}

/// <summary>Test source: StopAsync returns the bytes of a given WAV file instead of recording.</summary>
public sealed class FileAudioSource : IAudioSource
{
    private readonly string _path;

    /// <summary>Creates a source that replays <paramref name="wavPath"/>.</summary>
    public FileAudioSource(string wavPath) => _path = wavPath;

    /// <inheritdoc />
    public void Start() { }

    /// <inheritdoc />
    public Task<byte[]> StopAsync() => File.ReadAllBytesAsync(_path);
}

/// <summary>Helpers for WAV bytes.</summary>
public static class Wav
{
    /// <summary>
    /// Reads a 16-bit PCM WAV (classic or extensible). False when the file is not that format.
    /// Chunk sizes are walked with 64-bit math so a forged 4 GB chunk cannot wrap the cursor
    /// backwards or throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public static bool TryGetPcm(byte[] wav, out int sampleRate, out int channels, out int dataOffset, out int dataLength)
    {
        sampleRate = channels = dataOffset = dataLength = 0;
        if (wav.Length < 12) return false;
        var pos = 12;
        var haveFmt = false;
        var haveData = false;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id == "fmt " && !haveFmt)
            {
                if (body + 16 > wav.Length) return false;
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
                // 1 = PCM, 0xFFFE = extensible (PCM in practice for 16 bit).
                if ((format != 1 && format != 0xFFFE) || bits != 16 || channels < 1 || sampleRate <= 0)
                    return false;
                haveFmt = true;
            }
            else if (id == "data" && !haveData)
            {
                var available = (long)wav.Length - body;
                if (available < 0) return false;
                var length = size > available ? available : size;
                if (length > int.MaxValue) return false;
                dataOffset = body;
                dataLength = (int)length;
                haveData = true;
            }

            // size is uint: size+1 can wrap. Never add it as 32-bit into pos.
            var step = (long)size + (size & 1);
            if (step > wav.Length - body) break;
            pos = body + (int)step;
        }
        return haveFmt && haveData;
    }

    /// <summary>
    /// Duration in seconds of a 16-bit PCM WAV (0 when the header cannot be read).
    /// Uses the true PCM rate (sampleRate × channels × 2), not the header byteRate, and never throws.
    /// </summary>
    public static double DurationSeconds(byte[] wav)
    {
        if (!TryGetPcm(wav, out var sampleRate, out var channels, out _, out var dataLength)) return 0;
        var byteRate = (long)sampleRate * channels * 2;
        return byteRate <= 0 ? 0 : (double)dataLength / byteRate;
    }

    /// <summary>
    /// Decodes 16-bit PCM WAV (mono or stereo, channels averaged) to float samples in -1..1.
    /// Throws <see cref="InvalidDataException"/> for other formats; never
    /// <see cref="ArgumentOutOfRangeException"/> on a malformed chunk size.
    /// </summary>
    public static float[] ReadMonoFloat(byte[] wav, out int sampleRate)
    {
        if (!TryGetPcm(wav, out sampleRate, out var channels, out var dataOffset, out var dataLength))
            throw new InvalidDataException(wav.Length < 12 ? "WAV too short." : "Only 16-bit PCM WAV is supported.");
        var frames = dataLength / (2 * channels);
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++)
                sum += BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(dataOffset + (i * channels + c) * 2, 2));
            samples[i] = sum / (float)channels / 32768f;
        }
        return samples;
    }
}
