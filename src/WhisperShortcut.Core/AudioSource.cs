using System.Buffers.Binary;

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
    /// <summary>Duration in seconds of a PCM WAV (0 when the header cannot be read).</summary>
    public static double DurationSeconds(byte[] wav)
    {
        if (wav.Length < 12) return 0;
        var pos = 12;
        uint byteRate = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id == "fmt " && body + 12 <= wav.Length)
            {
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 8, 4));
            }
            else if (id == "data")
            {
                var available = (long)wav.Length - body;
                var length = size > available ? available : size;
                return byteRate == 0 ? 0 : (double)length / byteRate;
            }
            pos = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
        }
        return 0;
    }

    /// <summary>
    /// Decodes 16-bit PCM WAV (mono or stereo, channels averaged) to float samples in -1..1.
    /// Throws <see cref="InvalidDataException"/> for other formats.
    /// </summary>
    public static float[] ReadMonoFloat(byte[] wav, out int sampleRate)
    {
        sampleRate = 0;
        if (wav.Length < 12) throw new InvalidDataException("WAV too short.");
        var pos = 12;
        int channels = 0, bits = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id == "fmt " && body + 16 <= wav.Length)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
                // 1 = PCM, 0xFFFE = extensible (PCM in practice for 16 bit).
                if (format != 1 && format != 0xFFFE) throw new InvalidDataException("WAV is not PCM.");
            }
            else if (id == "data")
            {
                if (bits != 16 || channels < 1 || sampleRate <= 0)
                    throw new InvalidDataException("Only 16-bit PCM WAV is supported.");
                var available = (long)wav.Length - body;
                var length = (int)(size > available ? available : size);
                var frames = length / (2 * channels);
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    var sum = 0;
                    for (var c = 0; c < channels; c++)
                        sum += BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + (i * channels + c) * 2, 2));
                    samples[i] = sum / (float)channels / 32768f;
                }
                return samples;
            }
            pos = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }
}
