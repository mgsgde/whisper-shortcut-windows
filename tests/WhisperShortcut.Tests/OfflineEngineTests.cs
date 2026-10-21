using System.Net;
using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public sealed class ParakeetFactAttribute : FactAttribute
{
    public ParakeetFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSW_PARAKEET_DIR")))
            Skip = "WSW_PARAKEET_DIR not set";
    }
}

public sealed class LlamaFactAttribute : FactAttribute
{
    public LlamaFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSW_LLAMA_SERVER"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSW_LLM_GGUF")))
            Skip = "WSW_LLAMA_SERVER / WSW_LLM_GGUF not set";
    }
}

public class ParakeetTests
{
    [ParakeetFact]
    public void Diktat_ContainsHerrMeier_StilleIsEmpty()
    {
        using var t = ParakeetTranscriber.Load(Environment.GetEnvironmentVariable("WSW_PARAKEET_DIR")!, 4);
        var text = t.Transcribe(File.ReadAllBytes(Paths.TestAudio("diktat.wav")));
        Assert.True(text.Contains("Herr Meier") || text.Contains("Herr Meyer"), text);
        Assert.Equal("", t.Transcribe(File.ReadAllBytes(Paths.TestAudio("stille.wav"))));
    }

    [ParakeetFact]
    public void Hotwords_FixGlossaryTerms_AndDisposeIsSafe()
    {
        var work = Path.Combine(Path.GetTempPath(), "wsw-hot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = ParakeetTranscriber.Load(Environment.GetEnvironmentVariable("WSW_PARAKEET_DIR")!, 4,
                new[] { "Goedde", "Ramipril" }, work);
            Assert.Equal(new[] { "Goedde", "Ramipril" }, t.Hotwords);
            var text = t.Transcribe(File.ReadAllBytes(Paths.TestAudio("glossar-goedde.wav")));
            Assert.Contains("Goedde", text);
            Assert.Contains("Ramipril", text);
            t.Dispose();
            t.Dispose();
            Assert.Throws<ObjectDisposedException>(() => t.Transcribe(File.ReadAllBytes(Paths.TestAudio("diktat.wav"))));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Load_HotwordsWithoutWorkDir_Throws()
    {
        Assert.Throws<ArgumentException>(() => ParakeetTranscriber.Load("nowhere", 1, new[] { "x" }));
    }

    [Fact]
    public void FromGlossary_CleansDedupesAndLimits()
    {
        var r = ParakeetHotwords.FromGlossary(new[]
        {
            "Bachata (not \"Batchata\")", "  ", "", "Re:Zero  Anime", "goedde", "Goedde", "GOEDDE",
            new string('x', GlossaryEdit.MaxTermLength + 1), "Ramipril",
        });
        Assert.Equal(new[] { "Bachata", "Re Zero Anime", "goedde", "Ramipril" }, r);
    }

    [Fact]
    public void FromGlossary_CapsAtMaxTerms()
    {
        var r = ParakeetHotwords.FromGlossary(Enumerable.Range(0, ParakeetHotwords.MaxTerms + 20).Select(i => "t" + i));
        Assert.Equal(ParakeetHotwords.MaxTerms, r.Count);
    }

    [Fact]
    public void WriteBpeVocab_NegatesIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wsw-vocab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tokens = Path.Combine(dir, "tokens.txt");
            File.WriteAllText(tokens, "<unk> 0\n\u2581o 299\n\u2581a b 7\n");
            var vocab = Path.Combine(dir, "bpe.vocab");
            ParakeetHotwords.WriteBpeVocab(tokens, vocab);
            var bytes = File.ReadAllBytes(vocab);
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal("<unk>\t0\n\u2581o\t-299\n\u2581a b\t-7\n", Encoding.UTF8.GetString(bytes));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadMonoFloat_MonoAndStereo()
    {
        static byte[] Make(short[] pcm, int channels, int rate)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2);
            w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length * 2);
            foreach (var s in pcm) w.Write(s);
            return ms.ToArray();
        }
        var mono = Wav.ReadMonoFloat(Make(new short[] { 16384, -16384 }, 1, 16000), out var r1);
        Assert.Equal(16000, r1);
        Assert.Equal(new[] { 0.5f, -0.5f }, mono);
        var stereo = Wav.ReadMonoFloat(Make(new short[] { 16384, 0, -16384, -16384 }, 2, 44100), out var r2);
        Assert.Equal(44100, r2);
        Assert.Equal(new[] { 0.25f, -0.5f }, stereo);
        Assert.Throws<InvalidDataException>(() => Wav.ReadMonoFloat(new byte[4], out _));
    }
}

public class LocalLlmTests
{
    private static (LocalLlmClient Client, FakeHandler Handler) Make()
    {
        var h = new FakeHandler();
        return (new LocalLlmClient(new HttpClient(h)), h);
    }

    private static string Reply(string content, string finish = "stop") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content }, finish_reason = finish } },
        });

    [Fact]
    public async Task RequestShape_AndThinkStripping()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Reply("<think>\nhmm\n</think>\n\n  Hallo Welt \n")));
        var text = await c.CompleteAsync(new Uri("http://127.0.0.1:1234/"), "SYS", "USER", CancellationToken.None);
        Assert.Equal("Hallo Welt", text);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", h.Calls[0].Request.RequestUri!.ToString());
        var j = h.Json(0);
        Assert.Equal("system", (string?)j["messages"]![0]!["role"]);
        Assert.Equal("SYS", (string?)j["messages"]![0]!["content"]);
        Assert.Equal("USER", (string?)j["messages"]![1]!["content"]);
        Assert.Equal(0, (int?)j["temperature"]);
        Assert.Equal(4096, (int?)j["max_tokens"]);
        Assert.False((bool?)j["chat_template_kwargs"]!["enable_thinking"]);
    }

    [Fact]
    public async Task LengthFinish_StillReturns()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.OK, Reply("abgeschnitten", "length")));
        Assert.Equal("abgeschnitten", await c.CompleteAsync(new Uri("http://x/"), "s", "u", CancellationToken.None));
    }

    [Fact]
    public async Task ErrorsMapToLocalLlmException()
    {
        var (c, h) = Make();
        h.Responses.Enqueue((HttpStatusCode.InternalServerError, "boom"));
        h.Responses.Enqueue((HttpStatusCode.OK, "not json"));
        var u = new Uri("http://x/");
        var e1 = await Assert.ThrowsAsync<LocalLlmException>(() => c.CompleteAsync(u, "s", "u", CancellationToken.None));
        Assert.Equal("Das lokale Sprachmodell hat nicht geantwortet. Details im Protokoll.", e1.Message);
        await Assert.ThrowsAsync<LocalLlmException>(() => c.CompleteAsync(u, "s", "u", CancellationToken.None));
        var down = new LocalLlmClient(new HttpClient());
        await Assert.ThrowsAsync<LocalLlmException>(() =>
            down.CompleteAsync(new Uri("http://127.0.0.1:1/"), "s", "u", CancellationToken.None));
    }

    [Fact]
    public void PromptText_MatchesGeminiBuilding()
    {
        Assert.Equal(Prompts.DefaultPromptModeSystemPrompt + Prompts.PromptModeOutputRule, DictatePromptText.SystemPrompt(null));
        Assert.Equal("Eigener" + Prompts.PromptModeOutputRule, DictatePromptText.SystemPrompt("  Eigener \n"));
        Assert.Equal(Prompts.DictatePromptComposeMarker, DictatePromptText.SelectionBlock(""));
        Assert.Equal(Prompts.ClipboardSelectionHeader + "\n\nText", DictatePromptText.SelectionBlock("Text"));
        Assert.Equal(Prompts.ClipboardSelectionHeader + "\n\nText\n\nVOICE INSTRUCTION:\nmach kürzer",
            DictatePromptText.LocalUserTurn("Text", "mach kürzer"));
    }

    [LlamaFact]
    public async Task Live_HostStarts_AndAnswers()
    {
        using var host = new LlamaServerHost(
            Environment.GetEnvironmentVariable("WSW_LLAMA_SERVER")!,
            Environment.GetEnvironmentVariable("WSW_LLM_GGUF")!);
        Assert.False(host.IsRunning);
        await host.EnsureStartedAsync(CancellationToken.None);
        await host.EnsureStartedAsync(CancellationToken.None);
        Assert.True(host.IsRunning);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var answer = await new LocalLlmClient(http).CompleteAsync(host.BaseUrl!,
            DictatePromptText.SystemPrompt(null),
            DictatePromptText.LocalUserTurn("Hallo, das Teil kommt nächste Woche.", "mach das freundlicher"),
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(answer));
        Console.WriteLine("LLM answer: " + answer);
        host.Stop();
        Assert.False(host.IsRunning);
        Assert.Null(host.BaseUrl);
    }

    [Fact]
    public async Task Host_BadExecutable_ThrowsLocalLlmException()
    {
        using var host = new LlamaServerHost("/definitely/not/here", "x.gguf");
        await Assert.ThrowsAsync<LocalLlmException>(() => host.EnsureStartedAsync(CancellationToken.None));
    }
}
