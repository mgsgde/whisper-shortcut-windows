using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ModelSelectionTests
{
    private static readonly ModelSelection Sample = new(Engine.OpenAI, "gemini-3.5-flash-lite", "gpt-6-luna");

    [Fact]
    public void ModelFor_GivesTheIdOfThatProvider()
    {
        Assert.Equal("gemini-3.5-flash-lite", Sample.ModelFor(Engine.Gemini));
        Assert.Equal("gpt-6-luna", Sample.ModelFor(Engine.OpenAI));
        Assert.Equal(ModelPickers.LocalId, Sample.ModelFor(Engine.Local));
    }

    [Theory]
    [InlineData(Engine.Gemini, "gemini-3.5-flash-lite")]
    [InlineData(Engine.OpenAI, "gpt-6-luna")]
    [InlineData(Engine.Local, "local")]
    public void Effective_Online_IsTheChosenProviderAndItsModel(Engine engine, string model)
    {
        Assert.Equal((engine, model), (Sample with { Engine = engine }).Effective(offline: false));
    }

    [Theory]
    [InlineData(Engine.Gemini)]
    [InlineData(Engine.OpenAI)]
    [InlineData(Engine.Local)]
    public void Effective_Offline_IsAlwaysLocal(Engine engine)
    {
        Assert.Equal((Engine.Local, ModelPickers.LocalId), (Sample with { Engine = engine }).Effective(offline: true));
    }

    [Fact]
    public void Default_IsGeminiWithTheStepDefaults()
    {
        Assert.Equal(new ModelSelection(Engine.Gemini, Settings.DefaultTranscriptionModel, OpenAiModels.DefaultTranscriptionModel),
            ModelSelection.Default(ModelStep.Transcription));
        Assert.Equal(new ModelSelection(Engine.Gemini, Settings.DefaultPromptModel, OpenAiModels.DefaultPromptModel),
            ModelSelection.Default(ModelStep.Text));
    }

    [Fact]
    public void Normalize_Transcription_MapsOldIdsAndKeepsTheProvider()
    {
        var n = new ModelSelection(Engine.Local, "gemini-4-argon", "whisper-1").Normalize(ModelStep.Transcription);
        Assert.Equal(new ModelSelection(Engine.Local, Settings.DefaultTranscriptionModel, OpenAiModels.DefaultTranscriptionModel), n);
        Assert.Equal("gemini-3.8-flash", new ModelSelection(Engine.Gemini, "gemini-3.7-flash", "").Normalize(ModelStep.Transcription).Gemini);
    }

    [Fact]
    public void Normalize_Text_AllowsArgonAndMapsOldIds()
    {
        var n = new ModelSelection(Engine.Gemini, "gemini-4-argon", "gpt-4o").Normalize(ModelStep.Text);
        Assert.Equal(new ModelSelection(Engine.Gemini, "gemini-4-argon", OpenAiModels.DefaultPromptModel), n);
        Assert.Equal("gpt-5.4-mini", new ModelSelection(Engine.OpenAI, "x", "gpt-5-mini").Normalize(ModelStep.Text).OpenAi);
        Assert.Equal(Settings.DefaultPromptModel, new ModelSelection(Engine.OpenAI, "unknown", "x").Normalize(ModelStep.Text).Gemini);
    }

    [Fact]
    public void Normalize_AlreadyValid_IsUnchanged()
    {
        foreach (var step in new[] { ModelStep.Transcription, ModelStep.Text })
        {
            var valid = new ModelSelection(Engine.OpenAI, step.GeminiCatalog()[^1].Id, step.OpenAiCatalog()[^1].Id);
            Assert.Equal(valid, valid.Normalize(step));
        }
    }

    [Fact]
    public void Choose_SetsTheProviderAndOnlyItsModel()
    {
        Assert.Equal(new ModelSelection(Engine.Gemini, "gemini-3.8-flash", "gpt-6-luna"),
            Sample.Choose(ModelStep.Text, Engine.Gemini, "gemini-3.7-flash"));
        Assert.Equal(new ModelSelection(Engine.OpenAI, "gemini-3.5-flash-lite", "gpt-6-sol"),
            (Sample with { Engine = Engine.Gemini }).Choose(ModelStep.Text, Engine.OpenAI, "gpt-6-sol"));
        Assert.Equal(Sample with { Engine = Engine.Local }, Sample.Choose(ModelStep.Text, Engine.Local, ModelPickers.LocalId));
    }

    // ---- Load: a file from before the meetings and chat pages carries only the Diktieren and Dictate Prompt keys ----

    private static Settings LoadJson(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);
            return Settings.Load(path, new PlainSecretProtector());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private const string OldFile = """
        { "speechEngine": "openai", "textEngine": "local",
          "transcriptionModel": "gemini-3.8-flash", "openAiTranscriptionModel": "gpt-4o-mini-transcribe",
          "promptModel": "gemini-3.5-flash-lite", "openAiPromptModel": "gpt-6-luna" }
        """;

    [Fact]
    public void Load_ReadsDictateAndPromptFromTheirKeys()
    {
        var s = LoadJson(OldFile);
        Assert.Equal(new ModelSelection(Engine.OpenAI, "gemini-3.8-flash", "gpt-4o-mini-transcribe"), s.Dictate);
        Assert.Equal(new ModelSelection(Engine.Local, "gemini-3.5-flash-lite", "gpt-6-luna"), s.Prompt);
    }

    [Fact]
    public void Load_OlderFile_MeetingTranscriptionFollowsDictate()
    {
        var s = LoadJson(OldFile);
        Assert.Equal(s.Dictate, s.MeetingTranscription);
        Assert.False(s.MeetingSpeechEngineExplicit);
    }

    [Fact]
    public void Load_OlderFile_MeetingSummaryFollowsPromptButKeepsItsOwnGeminiDefault()
    {
        var s = LoadJson(OldFile);
        Assert.Equal(new ModelSelection(Engine.Local, MeetingOptions.DefaultSummaryModel, "gpt-6-luna"), s.MeetingText);
    }

    [Fact]
    public void Load_OlderFile_ChatFollowsPromptButKeepsItsOwnGeminiDefault()
    {
        var s = LoadJson(OldFile);
        Assert.Equal(new ModelSelection(Engine.Local, Settings.DefaultPromptModel, "gpt-6-luna"), s.Chat);
    }

    [Fact]
    public void Load_OwnKeys_WinOverTheFallbacks_AndAreNormalized()
    {
        var s = LoadJson("""
            { "speechEngine": "openai", "textEngine": "openai",
              "meetingSpeechEngine": "gemini", "meetingTranscriptionModel": "gemini-3.7-flash", "meetingOpenAiTranscriptionModel": "whisper-1",
              "meetingTextEngine": "local", "meetingSummaryModel": "gemini-4-argon", "meetingOpenAiSummaryModel": "gpt-5.4-mini",
              "chatEngine": "gemini", "chatModel": "gemini-2.5-flash", "chatOpenAiModel": "gpt-4o" }
            """);
        Assert.Equal(new ModelSelection(Engine.Gemini, "gemini-3.8-flash", OpenAiModels.DefaultTranscriptionModel), s.MeetingTranscription);
        Assert.Equal(new ModelSelection(Engine.Local, "gemini-4-argon", "gpt-5.4-mini"), s.MeetingText);
        Assert.Equal(new ModelSelection(Engine.Gemini, "gemini-3.8-flash", OpenAiModels.DefaultPromptModel), s.Chat);
    }

    [Fact]
    public void Load_EmptyFile_GivesTheDefaults()
    {
        var s = LoadJson("{}");
        Assert.Equal(ModelSelection.Default(ModelStep.Transcription), s.Dictate);
        Assert.Equal(ModelSelection.Default(ModelStep.Text), s.Prompt);
        Assert.Equal(ModelSelection.Default(ModelStep.Transcription), s.MeetingTranscription);
        Assert.Equal(ModelSelection.Default(ModelStep.Text) with { Gemini = MeetingOptions.DefaultSummaryModel }, s.MeetingText);
        Assert.Equal(ModelSelection.Default(ModelStep.Text), s.Chat);
    }

    [Fact]
    public void Settings_Effective_FollowOfflineMode()
    {
        var s = LoadJson("""{ "speechEngine": "openai", "textEngine": "gemini", "chatEngine": "openai", "chatOpenAiModel": "gpt-6-luna" }""");
        Assert.Equal((Engine.OpenAI, "gpt-6-luna"), s.EffectiveChat);
        s.OfflineMode = true;
        Assert.Equal((Engine.Local, ModelPickers.LocalId), s.EffectiveChat);
        Assert.Equal(Engine.Local, s.EffectiveSpeechEngine);
        Assert.Equal(Engine.Local, s.EffectiveTextEngine);
        Assert.Equal(Engine.Local, s.EffectiveMeetingTextEngine);
    }
}
