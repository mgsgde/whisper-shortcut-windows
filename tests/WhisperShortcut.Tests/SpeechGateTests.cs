using System.Buffers.Binary;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SpeechGateTests
{
    private const int Rate = 16000;

    // Mono 16-bit PCM WAV made of segments: (milliseconds, amplitude 0..1, frequency; 0 Hz = DC click).
    private static byte[] Wav(params (int Ms, double Amp, double Hz)[] segments)
    {
        var samples = new List<short>();
        foreach (var (ms, amp, hz) in segments)
        {
            var n = Rate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var v = hz == 0 ? amp : amp * Math.Sin(2 * Math.PI * hz * i / Rate);
                samples.Add((short)Math.Round(v * 32767));
            }
        }
        var data = samples.Count * 2;
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        for (var i = 0; i < samples.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), samples[i]);
        return wav;
    }

    // Peak amplitude of a sine at a given RMS level.
    private static double SineAmp(double rmsDbfs) => Math.Pow(10, rmsDbfs / 20) * Math.Sqrt(2);

    [Fact]
    public void DigitalSilenceHasNoSpeech()
    {
        var level = SpeechGate.Analyze(Wav((2000, 0, 0)));
        Assert.False(SpeechGate.HasSpeech(level));
        Assert.Equal(0, level.LoudFrames);
        Assert.True(level.Frames >= 60);
    }

    [Fact]
    public void NearSilentNoiseFloorHasNoSpeech() =>
        // -63 dBFS RMS, like a quiet room.
        Assert.False(SpeechGate.HasSpeech(Wav((2000, SineAmp(-63), 200))));

    [Fact]
    public void OneShortClickIsNotSpeech() =>
        Assert.False(SpeechGate.HasSpeech(Wav((1000, 0, 0), (20, 0.5, 0), (1000, 0, 0))));

    [Fact]
    public void HundredMillisecondClickIsNotSpeech() =>
        Assert.False(SpeechGate.HasSpeech(Wav((1000, 0, 0), (100, 0.5, 0), (1000, 0, 0))));

    [Fact]
    public void ThreeHundredMillisecondToneIsSpeech() =>
        Assert.True(SpeechGate.HasSpeech(Wav((500, 0, 0), (300, SineAmp(-30), 220), (500, 0, 0))));

    [Fact]
    public void ToneBelowThresholdIsNotSpeech() =>
        Assert.False(SpeechGate.HasSpeech(Wav((1000, SineAmp(-50), 220))));

    [Fact]
    public void NonPcmInputFailsOpen()
    {
        var wav = Wav((500, 0, 0));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 8); // bits per sample
        Assert.True(SpeechGate.HasSpeech(wav));
        Assert.True(SpeechGate.HasSpeech(new byte[10]));
    }

    [Fact]
    public void FileAudioSourceBytesAreJudgedLikeTheMic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gate-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, Wav((2000, 0, 0)));
        try
        {
            var bytes = new FileAudioSource(path).StopAsync().GetAwaiter().GetResult();
            Assert.False(SpeechGate.HasSpeech(bytes));
        }
        finally { File.Delete(path); }
    }
}
