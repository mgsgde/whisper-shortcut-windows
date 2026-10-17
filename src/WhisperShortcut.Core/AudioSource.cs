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
/// Raised by a microphone source when the whole recording is near-silent (peak below 1 %),
/// which usually means Windows blocks the microphone for the app.
/// </summary>
public sealed class AudioSilentException : Exception
{
    /// <summary>Creates the error with the German user message.</summary>
    public AudioSilentException()
        : base("Mikrofon liefert Stille. Bitte in den Windows-Einstellungen unter Datenschutz > Mikrofon den Zugriff erlauben.") { }
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
}
