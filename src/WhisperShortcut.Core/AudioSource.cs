using System.Buffers.Binary;
using System.Runtime.InteropServices;

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

/// <summary>16-bit PCM payload location inside a WAV file. The microphone records this format.</summary>
public readonly struct WavPcm16
{
    /// <summary>Creates the description of a 16-bit PCM <c>data</c> chunk.</summary>
    public WavPcm16(int sampleRate, int channels, int dataOffset, int dataLength)
    {
        SampleRate = sampleRate;
        Channels = channels;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }

    /// <summary>Samples per second.</summary>
    public int SampleRate { get; }
    /// <summary>1 = mono, 2 = stereo.</summary>
    public int Channels { get; }
    /// <summary>Byte offset of the first PCM sample.</summary>
    public int DataOffset { get; }
    /// <summary>PCM byte count (may be shorter than the chunk size if the file is truncated).</summary>
    public int DataLength { get; }
    /// <summary>Bytes per frame (all channels).</summary>
    public int FrameBytes => Channels * 2;
    /// <summary>Bytes per second.</summary>
    public int ByteRate => SampleRate * FrameBytes;
}

/// <summary>Helpers for WAV bytes.</summary>
public static class Wav
{
    // Little-endian fourccs: avoids Encoding.ASCII.GetString on every chunk of a long file.
    private const uint IdFmt = 0x20746D66; // "fmt "
    private const uint IdData = 0x61746164; // "data"

    /// <summary>
    /// Locates a 16-bit PCM (or WAVEFORMATEXTENSIBLE PCM) payload. False when the header is missing
    /// or the format is not 16-bit PCM — the same cases <see cref="ReadMonoFloat"/> rejects.
    /// </summary>
    public static bool TryGetPcm16(byte[] wav, out WavPcm16 pcm)
    {
        pcm = default;
        if (wav.Length < 12) return false;
        var pos = 12;
        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        var haveFmt = false;
        var haveData = false;
        var dataOffset = 0;
        var dataLength = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id == IdFmt && !haveFmt)
            {
                if (body + 16 > wav.Length) return false;
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
                // 1 = PCM, 0xFFFE = extensible (PCM in practice for 16 bit).
                if ((format != 1 && format != 0xFFFE) || bits != 16 || channels < 1 || sampleRate <= 0)
                    return false;
                haveFmt = true;
            }
            else if (id == IdData && !haveData)
            {
                var available = (long)wav.Length - body;
                if (available < 0) return false;
                var length = size > available ? available : size;
                if (length > int.MaxValue) return false;
                dataOffset = body;
                dataLength = (int)length;
                haveData = true;
            }

            var step = (long)size + (size & 1);
            if (step > wav.Length - body) break;
            pos = body + (int)step;
        }
        if (!haveFmt || !haveData) return false;
        pcm = new WavPcm16(sampleRate, channels, dataOffset, dataLength);
        return true;
    }

    /// <summary>Duration in seconds of a PCM WAV (0 when the header cannot be read).</summary>
    public static double DurationSeconds(byte[] wav)
    {
        if (TryGetPcm16(wav, out var pcm))
            return pcm.ByteRate == 0 ? 0 : (double)pcm.DataLength / pcm.ByteRate;
        if (wav.Length < 12) return 0;
        var pos = 12;
        uint byteRate = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id == IdFmt && body + 12 <= wav.Length)
            {
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 8, 4));
            }
            else if (id == IdData)
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
        if (!TryGetPcm16(wav, out var pcm))
        {
            sampleRate = 0;
            throw new InvalidDataException(wav.Length < 12 ? "WAV too short." : "Only 16-bit PCM WAV is supported.");
        }

        sampleRate = pcm.SampleRate;
        var frames = pcm.DataLength / pcm.FrameBytes;
        var samples = new float[frames];
        var data = wav.AsSpan(pcm.DataOffset, frames * pcm.FrameBytes);
        // The microphone writes 16 kHz mono little-endian; avoid per-sample Span slices on that path.
        if (pcm.Channels == 1 && BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, short>(data);
            const float scale = 1f / 32768f;
            for (var i = 0; i < frames; i++) samples[i] = src[i] * scale;
            return samples;
        }

        for (var i = 0; i < frames; i++)
        {
            var sum = 0;
            for (var c = 0; c < pcm.Channels; c++)
                sum += BinaryPrimitives.ReadInt16LittleEndian(data.Slice((i * pcm.Channels + c) * 2, 2));
            samples[i] = sum / (float)pcm.Channels / 32768f;
        }
        return samples;
    }
}
