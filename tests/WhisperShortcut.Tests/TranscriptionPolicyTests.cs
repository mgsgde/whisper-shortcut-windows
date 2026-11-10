using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// The table of what runs around a transcription, as the callers in TrayApp did it when it moved into Core (R3).
/// A row changing here is a behaviour change: say so in the commit. Inconsistencies between rows are known and
/// deliberately kept (see the report of R3); do not "fix" them while editing this file.
/// </summary>
public class TranscriptionPolicyTests
{
    // purpose, engine, glossary backstops, echo strip, keywords, speech gate, punctuation from settings
    public static TheoryData<TranscribePurpose, Engine, bool, bool, bool, bool, bool> Table => new()
    {
        { TranscribePurpose.Dictation, Engine.Local, true, true, false, false, true },
        { TranscribePurpose.Dictation, Engine.Gemini, true, true, true, true, true },
        { TranscribePurpose.Dictation, Engine.OpenAI, true, true, true, true, true },

        { TranscribePurpose.Instruction, Engine.Local, false, false, false, false, false },
        { TranscribePurpose.Instruction, Engine.Gemini, false, false, true, false, false },
        { TranscribePurpose.Instruction, Engine.OpenAI, false, false, true, false, false },

        { TranscribePurpose.TemplateBody, Engine.Local, true, false, false, false, false },
        { TranscribePurpose.TemplateBody, Engine.Gemini, true, true, true, false, false },
        { TranscribePurpose.TemplateBody, Engine.OpenAI, true, true, true, false, false },

        { TranscribePurpose.QuickActionTranscript, Engine.Local, true, false, false, false, false },
        { TranscribePurpose.QuickActionTranscript, Engine.Gemini, true, true, true, false, false },
        { TranscribePurpose.QuickActionTranscript, Engine.OpenAI, true, true, true, false, false },

        { TranscribePurpose.MeetingChunk, Engine.Local, true, true, false, false, false },
        { TranscribePurpose.MeetingChunk, Engine.Gemini, true, true, false, false, false },
        { TranscribePurpose.MeetingChunk, Engine.OpenAI, true, true, false, false, false },

        // Live diktieren decodes on this PC only; the engine argument does not matter.
        { TranscribePurpose.LivePiece, Engine.Local, true, true, false, false, true },
        { TranscribePurpose.LivePiece, Engine.Gemini, true, true, false, false, true },
        { TranscribePurpose.LivePiece, Engine.OpenAI, true, true, false, false, true },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void For_IsTodaysBehaviour(TranscribePurpose purpose, Engine engine, bool backstops, bool echoStrip, bool keywords, bool gate, bool punctuation)
    {
        var p = TranscriptionPolicy.For(purpose, engine);
        Assert.Equal(new TranscriptionPolicy(backstops, echoStrip, keywords, gate, punctuation), p);
    }

    [Fact]
    public void Table_CoversEveryPurposeAndEngine()
    {
        var rows = Table.Select(r => ((TranscribePurpose)r[0], (Engine)r[1])).ToHashSet();
        foreach (var purpose in Enum.GetValues<TranscribePurpose>())
            foreach (var engine in Enum.GetValues<Engine>())
                Assert.Contains((purpose, engine), rows);
    }

    [Fact]
    public void Punctuation_StoredModeOnlyOnThisPc()
    {
        var dictation = TranscriptionPolicy.For(TranscribePurpose.Dictation, Engine.Local);
        Assert.Equal(PunctuationMode.SpokenOnly, dictation.Punctuation(PunctuationMode.SpokenOnly, Engine.Local));
        Assert.Equal(PunctuationMode.Automatic, TranscriptionPolicy.For(TranscribePurpose.Dictation, Engine.Gemini).Punctuation(PunctuationMode.SpokenOnly, Engine.Gemini));
        Assert.Equal(PunctuationMode.SpokenOnly, TranscriptionPolicy.For(TranscribePurpose.LivePiece, Engine.Local).Punctuation(PunctuationMode.SpokenOnly, Engine.Local));
        foreach (var purpose in new[] { TranscribePurpose.Instruction, TranscribePurpose.TemplateBody, TranscribePurpose.QuickActionTranscript, TranscribePurpose.MeetingChunk })
            Assert.Equal(PunctuationMode.Automatic, TranscriptionPolicy.For(purpose, Engine.Local).Punctuation(PunctuationMode.SpokenOnly, Engine.Local));
    }

    private static Settings NewSettings() =>
        Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"), new PlainSecretProtector());

    [Fact]
    public void MissingKeyProvider_NamesTheProviderWithoutKey()
    {
        var s = NewSettings();
        s.ApiKey = "";
        s.OpenAiKey = "  ";
        Assert.Equal("Gemini", s.MissingKeyProvider(Engine.Gemini));
        Assert.Equal("OpenAI", s.MissingKeyProvider(Engine.OpenAI));
        Assert.Null(s.MissingKeyProvider(Engine.Local));

        s.ApiKey = "AIza-test";
        Assert.Null(s.MissingKeyProvider(Engine.Gemini));
        Assert.Equal("OpenAI", s.MissingKeyProvider(Engine.OpenAI));

        s.OpenAiKey = "sk-test";
        Assert.Null(s.MissingKeyProvider(Engine.OpenAI));
    }
}
