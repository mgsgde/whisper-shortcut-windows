using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// The dictation / prompt-from-transcript path lives in TrayApp (WinForms). These tests replay
/// that composition with Core types and a mocked speech provider so a regression in the speech
/// gate, glossary echo retry, backstops or cleanup is caught without a Windows UI.
/// Keep in step with TrayApp.DictateAsync / TranscribeOnlineAsync / GlossaryBackstops / Reply.
/// </summary>
public class TranscriptionPipelineTests
{
    private sealed class ScriptedSpeech : ISpeechProvider
    {
        public List<IReadOnlyList<string>> GlossaryCalls { get; } = new();
        public Queue<string> Answers { get; } = new();
        public Exception? Throw { get; set; }

        public Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default)
        {
            if (Throw != null) throw Throw;
            GlossaryCalls.Add(glossary.ToList());
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : "");
        }
    }

    private static bool HasSpeech(byte[] wav) => SpeechGate.HasSpeech(wav);

    // TrayApp.TranscribeOnlineAsync: skip silent chunks; retry once without the glossary when
    // DiscardGlossaryEcho wiped a non-empty answer (the model filled quiet audio with vocabulary).
    private static Task<string> TranscribeOnlineAsync(ISpeechProvider speech, byte[] wav,
        IReadOnlyList<string> glossary, CancellationToken ct = default)
    {
        var echoTerms = GlossaryPrompt.EchoTerms(glossary);
        return ChunkedTranscription.TranscribeAsync(
            speech is OpenAiClient ? WavChunker.SplitForOpenAi(wav) : WavChunker.Split(wav),
            async (chunk, c) =>
            {
                if (!HasSpeech(chunk)) return "";
                var text = await speech.TranscribeAsync(chunk, glossary, c);
                if (text.Trim().Length == 0
                    || TextCleanup.DiscardGlossaryEcho(text, Wav.DurationSeconds(chunk), echoTerms).Length > 0)
                    return text;
                return await speech.TranscribeAsync(chunk, Array.Empty<string>(), c);
            }, maxParallel: 4, ct);
    }

    // TrayApp.GlossaryBackstops
    private static string GlossaryBackstops(string raw, IReadOnlyList<string> glossary)
    {
        var stripped = TextCleanup.StripTrailingGlossaryEcho(raw, GlossaryPrompt.EchoTerms(glossary));
        var corrected = GlossaryPrompt.CorrectRejectedSpellings(stripped, glossary);
        return GlossaryPrompt.ApplyTermSpellings(corrected, GlossaryPrompt.EchoTerms(glossary));
    }

    // TrayApp.DictateAsync after TranscribeAsync
    private static string Dictate(string raw, double seconds, IReadOnlyList<string> glossary)
    {
        raw = GlossaryBackstops(raw, glossary);
        var text = TextCleanup.DiscardImplausible(TextCleanup.ForDictation(raw), seconds);
        if (TextCleanup.LooksLikeRefusal(text)) text = "";
        return text;
    }

    // TrayApp.Reply
    private static string Reply(string raw, string?[]? sources, out IReadOnlyList<string> unsupported)
    {
        var text = TextCleanup.StripMarkdown(TextCleanup.Normalize(raw));
        unsupported = sources == null || text.Length == 0
            ? Array.Empty<string>()
            : DictationFidelityCheck.UnsupportedWords(text, sources);
        return text;
    }

    [Fact]
    public async Task SilentChunk_DoesNotCallTheSpeechProvider()
    {
        var speech = new ScriptedSpeech();
        speech.Answers.Enqueue("should not be used");
        var text = await TranscribeOnlineAsync(speech, TestWav.Silence(2000), new[] { "Ramipril" });
        Assert.Equal("", text);
        Assert.Empty(speech.GlossaryCalls);
    }

    [Fact]
    public async Task GlossaryEcho_RetriesOnceWithoutTheGlossary()
    {
        var speech = new ScriptedSpeech();
        speech.Answers.Enqueue("sabaki.dance");
        speech.Answers.Enqueue("bitte den Termin verschieben");
        var glossary = new[] { "Sabaki", "Dance" };

        var text = await TranscribeOnlineAsync(speech, TestWav.Speech(), glossary);

        Assert.Equal("bitte den Termin verschieben", text);
        Assert.Equal(2, speech.GlossaryCalls.Count);
        Assert.Equal(glossary, speech.GlossaryCalls[0]);
        Assert.Empty(speech.GlossaryCalls[1]);
    }

    [Fact]
    public async Task EmptyAnswer_IsNotRetried()
    {
        var speech = new ScriptedSpeech();
        speech.Answers.Enqueue("  ");
        speech.Answers.Enqueue("hallo");
        var text = await TranscribeOnlineAsync(speech, TestWav.Speech(), new[] { "Ramipril" });
        Assert.Equal("  ", text);
        Assert.Single(speech.GlossaryCalls);
    }

    [Fact]
    public async Task RealSpeechWithGlossaryTerms_IsKeptWithoutRetry()
    {
        var speech = new ScriptedSpeech();
        speech.Answers.Enqueue("Bitte Ramipril 5 mg verordnen.");
        var text = await TranscribeOnlineAsync(speech, TestWav.Speech(), new[] { "Ramipril" });
        Assert.Equal("Bitte Ramipril 5 mg verordnen.", text);
        Assert.Single(speech.GlossaryCalls);
    }

    [Fact]
    public async Task ProviderFailure_SurfacesWithoutASecondCall()
    {
        var speech = new ScriptedSpeech { Throw = new GeminiException(GeminiErrorKind.Quota, "Kontingent erschöpft.") };
        var ex = await Assert.ThrowsAsync<GeminiException>(() =>
            TranscribeOnlineAsync(speech, TestWav.Speech(), Array.Empty<string>()));
        Assert.Equal(GeminiErrorKind.Quota, ex.Kind);
        Assert.Empty(speech.GlossaryCalls);
    }

    [Fact]
    public void GlossaryBackstops_StripCorrectAndRespell()
    {
        var glossary = new[] { "Claude CLI (not \"Cloud CLI\")", "goedde", "Joschka", "SumUp" };
        var raw = "Herr Gödde nutzt die Cloud CLI. Joschka SumUp";
        Assert.Equal("Herr Goedde nutzt die Claude CLI.", GlossaryBackstops(raw, glossary));
    }

    [Fact]
    public void Dictate_Capitalises_DropsImplausibleAndRefusals()
    {
        var glossary = Array.Empty<string>();
        Assert.Equal("Das ist ein Test.", Dictate("das ist ein Test", 3, glossary));
        Assert.Equal("", Dictate(new string('a', 200), 1, glossary));
        Assert.Equal("", Dictate("Please provide the audio file.", 3, glossary));
    }

    [Fact]
    public async Task OfflinePath_AppliesSpokenPunctuationAfterChunks()
    {
        var speech = new ScriptedSpeech();
        speech.Answers.Enqueue("Anamnese Doppelpunkt Patient klagt, neuer Absatz, Befund Doppelpunkt unauffällig");
        var raw = await ChunkedTranscription.TranscribeAsync(
            WavChunker.SplitForLocal(TestWav.Speech()),
            (chunk, c) => speech.TranscribeAsync(chunk, Array.Empty<string>(), c),
            maxParallel: 1, CancellationToken.None);
        Assert.Equal("Anamnese: Patient klagt.\n\nBefund: unauffällig", SpokenPunctuation.Apply(raw));
    }

    [Fact]
    public void PromptFromTranscript_BareEditWithoutSelection_IsRefused()
    {
        Assert.True(ShouldRefuseBareEdit(null, "Korrigiere."));
        Assert.False(ShouldRefuseBareEdit(null, "Schreib Herrn Meier ein Angebot."));
        Assert.False(ShouldRefuseBareEdit("markierter Text", "Korrigiere."));
    }

    // TrayApp.PromptFromTranscriptAsync / PromptAsync: a lone "korrigiere" with nothing selected
    // would make the model invent the text it "edits".
    private static bool ShouldRefuseBareEdit(string? selection, string instruction) =>
        selection == null && BareEditCommand.Matches(instruction);

    [Fact]
    public void QuickAction_NothingToApply_IsNull()
    {
        Assert.Null(QuickActionRequest.Build("Kürzer fassen", null, "  "));
        Assert.Equal(("Patient klagt", "Kürzer fassen", true),
            QuickActionRequest.Build("Kürzer fassen", null, " Patient klagt ")!.Value);
    }

    [Fact]
    public void Reply_StripsMarkdown_AndFlagsInventedWords()
    {
        var text = Reply("## Befund\n**Psoas** links\n\nTherapie:\nMobilisation",
            new[] { "Psoas links" }, out var unsupported);
        Assert.Equal("Befund:\nPsoas links\n\nTherapie:\nMobilisation", text);
        Assert.Contains("Mobilisation", unsupported);
        Assert.DoesNotContain("Psoas", unsupported);
    }

    [Fact]
    public void EngineRouting_OfflineModeOverridesBothChoices()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.SpeechEngine = Engine.OpenAI;
        s.TextEngine = Engine.Gemini;
        Assert.Equal(Engine.OpenAI, s.EffectiveSpeechEngine);
        Assert.Equal(Engine.Gemini, s.EffectiveTextEngine);
        s.OfflineMode = true;
        Assert.Equal(Engine.Local, s.EffectiveSpeechEngine);
        Assert.Equal(Engine.Local, s.EffectiveTextEngine);
    }

    [Fact]
    public async Task OpenAiClient_UsesTheLargerOpenAiSplit()
    {
        // A short recording stays one piece either way; the dispatch is `speech is OpenAiClient`.
        var h = new FakeHandler();
        h.Responses.Enqueue((System.Net.HttpStatusCode.OK, "{\"text\":\"ok\"}"));
        var openAi = new OpenAiClient(() => "sk", () => "gpt-4o-transcribe", () => "gpt-6-sol", new HttpClient(h))
        { RetryDelay = TimeSpan.Zero };
        var text = await TranscribeOnlineAsync(openAi, TestWav.Speech(), Array.Empty<string>());
        Assert.Equal("ok", text);
        Assert.Single(h.Calls);
    }
}
