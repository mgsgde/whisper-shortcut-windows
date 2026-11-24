using System.Buffers.Binary;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class DictationPipelineTests
{
    private const int Rate = 16000;

    private static byte[] Wav(int ms, double amp, double hz)
    {
        var n = Rate * ms / 1000;
        var data = n * 2;
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
        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), (short)Math.Round(amp * Math.Sin(2 * Math.PI * hz * i / Rate) * 32767));
        return wav;
    }

    private static readonly byte[] Loud = Wav(2000, 0.3, 300);
    private static readonly byte[] Silence = Wav(2000, 0, 0);

    private sealed class FakeText : ITextProvider
    {
        public string Reply = "Antwort.";
        public string? Selection, System, User;
        public ScreenshotImage? Image;
        public int Calls;
        public Task PrewarmConnection() => Task.CompletedTask;
        public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, CancellationToken ct = default,
            IReadOnlyList<PromptTurn>? history = null) => PromptTextAsync(selection, instruction, customSystemPrompt, ct, history, null);
        public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt, CancellationToken ct,
            IReadOnlyList<PromptTurn>? history, ScreenshotImage? image)
        {
            Calls++; Selection = selection; User = instruction; System = customSystemPrompt; Image = image;
            return Task.FromResult(Reply);
        }
        public Task<string> CompleteTextAsync(string systemPrompt, string userText, ScreenshotImage? image, CancellationToken ct = default)
        {
            Calls++; System = systemPrompt; User = userText; Image = image;
            return Task.FromResult(Reply);
        }
    }

    private sealed class FakeHost : IPipelineHost
    {
        public bool FakeGemini { get; set; }
        public string PromptModeSystemPrompt { get; set; } = "";
        public string DictatePromptTemplate { get; set; } = "";
        public Engine TextEngine { get; set; } = Engine.Gemini;
        public IReadOnlyList<string> QuickActions { get; set; } = Array.Empty<string>();
        public double PromptInlineLimitSeconds => 300;
        public Func<TranscribePurpose, CancellationToken, string> Transcript = (_, _) => "";
        public FakeText Text = new();
        public string AudioReply = "Gemini-Antwort.";
        public ScreenshotImage? AudioImage;
        public string? AudioSystem;
        public List<(string? Selection, string Instruction, string Response)> Remembered = new();

        public Task<string> TranscribeAsync(TranscribePurpose purpose, byte[] wav, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Transcript(purpose, ct));
        }
        public PunctuationMode PunctuationFor(TranscribePurpose purpose) => PunctuationMode.Automatic;
        public string Backstops(TranscribePurpose purpose, string raw) => raw;
        public ITextProvider PromptProvider(IProgress<string> progress) => Text;
        public Task<string> PromptWithAudioAsync(byte[] wav, string? selection, string systemPrompt, ScreenshotImage? image,
            IReadOnlyList<PromptTurn>? history, CancellationToken ct)
        {
            AudioImage = image; AudioSystem = systemPrompt;
            return Task.FromResult(AudioReply);
        }
        public IReadOnlyList<PromptTurn>? HistoryFor(string? selection) => null;
        public void RememberPrompt(string? selection, string instruction, string response, string? reportInstruction = null) =>
            Remembered.Add((selection, instruction, response));
    }

    private static Task<TakeResult> Run(FakeHost host, TakeInput input, CancellationToken ct = default) =>
        new DictationPipeline(host).RunAsync(input, new SyncProgress<string>(_ => { }), ct);

    private static TakeInput Input(RecordingKindName kind, byte[]? wav = null, double seconds = 2, string? selection = null,
        bool retry = false, DictationTemplate? template = null, string? quick = null, ScreenshotImage? shot = null,
        bool noNewText = false, Func<bool>? late = null) =>
        new(kind, wav ?? Loud, seconds, selection, retry, template, noNewText, quick, shot, late);

    private static void AssertStop(TakeResult r, string title, string text, bool errorSound)
    {
        Assert.Equal("", r.Text);
        var stop = Assert.IsType<Presentation>(r.Stop);
        Assert.Equal(PresentationKind.Toast, stop.Kind);
        Assert.Equal(Severity.Info, stop.Icon);
        Assert.Equal(title, stop.Title);
        Assert.Equal(text, stop.Text);
        Assert.Equal(errorSound, stop.ErrorSound);
    }

    private const string NoInstruction = "In der Aufnahme war keine Anweisung zu hören. Sprich sie noch einmal.";

    [Fact]
    public async Task Dictation_WithoutWords_StopsWithNoSpeech_AndKeepsTheRawText()
    {
        var host = new FakeHost { Transcript = (_, _) => "" };
        var r = await Run(host, Input(RecordingKindName.Dictate));
        AssertStop(r, UserCopy.NoSpeechTitle, UserCopy.NoSpeechText, false);
        Assert.Equal("", r.RawText);
    }

    [Fact]
    public async Task Dictation_ReturnsCleanedTextAndRaw()
    {
        var host = new FakeHost { Transcript = (_, _) => "hallo welt" };
        var r = await Run(host, Input(RecordingKindName.Dictate));
        Assert.Null(r.Stop);
        Assert.Equal("hallo welt", r.RawText);
        Assert.StartsWith("Hallo welt", r.Text);
    }

    [Fact]
    public async Task Prompt_OnSilence_StopsWithNoInstruction()
    {
        var r = await Run(new FakeHost(), Input(RecordingKindName.Prompt, Silence, selection: "Text"));
        AssertStop(r, UserCopy.NoSpeechTitle, NoInstruction, false);
    }

    [Fact]
    public async Task Prompt_FillerInstruction_StopsWithNoInstruction()
    {
        var host = new FakeHost { Transcript = (_, _) => "Mhm." };
        var r = await Run(host, Input(RecordingKindName.Prompt, selection: "Text"));
        AssertStop(r, UserCopy.NoSpeechTitle, NoInstruction, false);
        Assert.Empty(host.Remembered);
    }

    [Fact]
    public async Task Prompt_BareEditCommandWithoutSelection_StopsWithNothingSelected_AndErrorSound()
    {
        var host = new FakeHost { Transcript = (_, _) => "korrigiere" };
        var r = await Run(host, Input(RecordingKindName.Prompt));
        AssertStop(r, UserCopy.NothingSelectedTitle, UserCopy.NothingSelectedText, true);
    }

    [Fact]
    public async Task PromptFromTranscript_Empty_Filler_And_BareEdit_Stop()
    {
        var host = new FakeHost { TextEngine = Engine.OpenAI, Transcript = (_, _) => "" };
        AssertStop(await Run(host, Input(RecordingKindName.Prompt, selection: "Text")), UserCopy.NoSpeechTitle, NoInstruction, false);
        host.Transcript = (_, _) => "Äh.";
        AssertStop(await Run(host, Input(RecordingKindName.Prompt, selection: "Text")), UserCopy.NoSpeechTitle, NoInstruction, false);
        host.Transcript = (_, _) => "mach das freundlicher";
        AssertStop(await Run(host, Input(RecordingKindName.Prompt)), UserCopy.NothingSelectedTitle, UserCopy.NothingSelectedText, true);
        Assert.Equal(0, host.Text.Calls);
    }

    [Fact]
    public async Task Template_NoTranscriptAndNoSelection_StopsWithNoSpeech()
    {
        var host = new FakeHost { Transcript = (_, _) => "" };
        var t = new DictationTemplate { Name = "Befund", Instructions = "Formatiere." };
        var r = await Run(host, Input(RecordingKindName.Template, template: t));
        AssertStop(r, UserCopy.NoSpeechTitle, UserCopy.NoSpeechText, false);
        Assert.Equal(0, host.Text.Calls);
    }

    [Fact]
    public async Task QuickAction_WithNothingToApplyItTo_StopsWithErrorSound()
    {
        var host = new FakeHost { Transcript = (_, _) => "" };
        var r = await Run(host, Input(RecordingKindName.Prompt, Array.Empty<byte>(), seconds: 0, quick: "Fasse zusammen"));
        AssertStop(r, "Nichts zum Anwenden", $"Erst Text markieren oder etwas diktieren, dann {QuickActionCard.KeyRange} drücken.", true);
    }

    [Fact]
    public async Task Template_UsesTheInputsSelectionAndScreenshot()
    {
        var host = new FakeHost { Transcript = (_, _) => "Kopfschmerzen seit drei Tagen" };
        var shot = new ScreenshotImage(new byte[] { 1, 2, 3 }, "image/png");
        var t = new DictationTemplate { Name = "Befund", Instructions = "Formatiere den Befund." };
        var r = await Run(host, Input(RecordingKindName.Template, selection: "MARKIERTER TEXT", template: t, shot: shot));
        Assert.Null(r.Stop);
        Assert.Same(shot, host.Text.Image);
        Assert.Contains("MARKIERTER TEXT", host.Text.User);
        Assert.StartsWith("Formatiere den Befund.", host.Text.System);
    }

    [Fact]
    public async Task QuickAction_UsesTheInputsQuickActionSelectionAndScreenshot()
    {
        var host = new FakeHost { Transcript = (_, _) => "", PromptModeSystemPrompt = "Eigener Prompt" };
        var shot = new ScreenshotImage(new byte[] { 9 }, "image/png");
        var r = await Run(host, Input(RecordingKindName.Prompt, Array.Empty<byte>(), 0, selection: "Alt", quick: "Kürzer", shot: shot));
        Assert.Null(r.Stop);
        Assert.Equal("Alt", host.Text.Selection);
        Assert.Equal("Kürzer", host.Text.User);
        Assert.Same(shot, host.Text.Image);
        Assert.Equal(("Alt", "Kürzer", "Antwort."), host.Remembered.Single());
    }

    [Fact]
    public async Task Prompt_WithAudio_SendsTheInputsScreenshot_AndNoNewTextTemplateOmitsTheTemplate()
    {
        var host = new FakeHost { Transcript = (_, _) => "mach es kürzer", DictatePromptTemplate = "NEUER-TEXT-VORLAGE" };
        var shot = new ScreenshotImage(new byte[] { 7 }, "image/png");
        var withTemplate = await Run(host, Input(RecordingKindName.Prompt, selection: "Text", shot: shot));
        Assert.Null(withTemplate.Stop);
        Assert.Same(shot, host.AudioImage);
        Assert.Contains("NEUER-TEXT-VORLAGE", host.AudioSystem);
        await Run(host, Input(RecordingKindName.Prompt, selection: "Text", noNewText: true));
        Assert.DoesNotContain("NEUER-TEXT-VORLAGE", host.AudioSystem);
        Assert.Null(host.AudioImage);
    }

    [Fact]
    public async Task Prompt_LateCopyWithoutSelection_ThrowsUnlessItIsARetry()
    {
        var host = new FakeHost { Transcript = (_, _) => "schreib einen text" };
        await Assert.ThrowsAsync<SelectionUnreadableException>(() => Run(host, Input(RecordingKindName.Prompt, late: () => true)));
        var r = await Run(host, Input(RecordingKindName.Prompt, retry: true, late: () => true));
        Assert.Null(r.Stop);
    }

    [Fact]
    public async Task Unsupported_WordsAreReturnedWithTheResult()
    {
        var host = new FakeHost { Transcript = (_, _) => "mach den befund", AudioReply = "Befund mit Ibuprofen 400 mg." };
        var r = await Run(host, Input(RecordingKindName.Prompt));
        Assert.True(r.HasUnsupported);
    }

    [Fact]
    public async Task Cancellation_PropagatesFromTheEngine_AndFromTheEnd()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost { Transcript = (_, c) => { cts.Cancel(); c.ThrowIfCancellationRequested(); return ""; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host, Input(RecordingKindName.Dictate), cts.Token));

        using var late = new CancellationTokenSource();
        var host2 = new FakeHost { Transcript = (_, _) => { late.Cancel(); return "hallo"; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host2, Input(RecordingKindName.Dictate), late.Token));
    }
}
