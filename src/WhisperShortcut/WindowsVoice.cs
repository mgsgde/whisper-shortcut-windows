using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace WhisperShortcut;

/// <summary>
/// Vorlesen with the Windows voices on this PC (Mac SystemTTSService: on-device voices, no internet, no key, the automatic
/// choice in Offline Mode). Emits the same raw PCM as the cloud voices (s16le, 24 kHz, mono), so playback, seeking and the
/// speed control stay the same. Chunked like the cloud path: the first, short chunk starts playback quickly.
/// </summary>
internal static class WindowsVoice
{
    private static readonly SpeechAudioFormatInfo Format = new(Core.SpeechPlaybackBuffer.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    /// <summary>Installed and enabled voices as (name, culture, gender "m"/"w"/"neutral"); empty when SAPI is unavailable.</summary>
    public static IReadOnlyList<(string Name, string Culture, string Gender)> Installed()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            return synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => (v.VoiceInfo.Name, v.VoiceInfo.Culture.Name, v.VoiceInfo.Gender switch
                {
                    VoiceGender.Male => "m",
                    VoiceGender.Female => "w",
                    _ => "neutral",
                }))
                .ToList();
        }
        catch (Exception e)
        {
            Core.Log.Error("Windows voices could not be listed", e);
            return Array.Empty<(string, string, string)>();
        }
    }

    /// <summary>The voices for the settings picker, the Windows language first.</summary>
    public static IReadOnlyList<Core.TtsVoice> Voices() => Core.ReadAloud.SystemVoices(Installed(), CultureInfo.CurrentUICulture.Name);

    /// <summary>
    /// Speaks <paramref name="text"/> chunk by chunk into PCM, in order, on a worker thread. Throws
    /// <see cref="InvalidOperationException"/> when Windows has no voice; cancellation stops after the current chunk.
    /// </summary>
    public static Task SynthesizeAsync(string storedVoice, string text, Action<byte[]> onAudio, CancellationToken ct) => Task.Run(() =>
    {
        var installed = Installed();
        var voice = Core.ReadAloud.PickSystemVoice(storedVoice, installed.Select(v => (v.Name, v.Culture)).ToList(), CultureInfo.CurrentUICulture.Name)
            ?? throw new InvalidOperationException("Windows has no speech voice installed.");
        var chunks = Core.TtsTextChunker.Split(text, Core.ReadAloud.ChunkSizeFor(Core.TtsProvider.System));
        Core.Log.Info($"Windows voice {voice}: {text.Length} chars in {chunks.Count} chunks");
        using var synth = new SpeechSynthesizer();
        synth.SelectVoice(voice);
        // Speak() blocks until the chunk is done. Synthesis runs many times faster than real time, so checking between
        // chunks stops it well before the player would have reached the next one.
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            synth.SetOutputToAudioStream(stream, Format);
            synth.Speak(chunk);
            synth.SetOutputToNull();
            ct.ThrowIfCancellationRequested();
            if (stream.Length > 0) onAudio(stream.ToArray());
        }
    }, ct);
}
