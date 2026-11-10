using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ModelPickerTests
{
    private static IReadOnlyList<PickerModel> Speech => ModelPickers.Transcription("Parakeet");

    private static PickerCuration Curate(Func<PickerModel, bool> usable, PickerModel? selected = null)
    {
        var models = Speech;
        var rec = ModelPickers.Recommended(models, Settings.DefaultTranscriptionModel, OpenAiModels.DefaultTranscriptionModel);
        return PickerCuration.Curate(models, rec, selected, usable);
    }

    [Fact]
    public void GeminiKeyOnly_RecommendedAndFastestAreGemini_OfflineIsLocal()
    {
        var c = Curate(m => m.Engine != Engine.OpenAI);
        Assert.Equal(PickerBadge.Recommended, c.Featured[0].Badges[0]);
        Assert.Equal(Engine.Gemini, c.Featured[0].Model.Engine);
        Assert.All(c.Featured.Where(f => f.Badges.Contains(PickerBadge.Fastest)), f => Assert.Equal(Engine.Gemini, f.Model.Engine));
        Assert.Contains(c.Featured, f => f.Model.Engine == Engine.Local && f.Badges.Contains(PickerBadge.Offline));
        Assert.True(c.Featured.Count <= 3);
        Assert.Equal(Speech.Count, c.Featured.Count + c.More.Count);
    }

    [Fact]
    public void OpenAiKeyOnly_RecommendsTheOpenAiDefault()
    {
        var c = Curate(m => m.Engine != Engine.Gemini);
        var rec = c.Featured.Single(f => f.Badges.Contains(PickerBadge.Recommended)).Model;
        Assert.Equal((Engine.OpenAI, OpenAiModels.DefaultTranscriptionModel), (rec.Engine, rec.Id));
    }

    [Fact]
    public void SameModelRecommendedAndFastest_MergesBadges()
    {
        // Gemini 3.1 Flash-Lite is the Windows default and the fastest speech model.
        var c = Curate(m => m.Engine != Engine.OpenAI);
        Assert.Equal(new[] { PickerBadge.Recommended, PickerBadge.Fastest }, c.Featured[0].Badges);
    }

    [Fact]
    public void SelectionOutsideTheTopRow_IsAddedWithoutBadge()
    {
        var pick = Speech.Single(m => m.Id == "gpt-transcribe");
        var c = Curate(_ => true, pick);
        Assert.Contains(c.Featured, f => f.Model == pick && f.Badges.Count == 0);
        Assert.DoesNotContain(pick, c.More);
    }

    [Fact]
    public void NoKeyAtAll_StillFillsTheRow()
    {
        var c = Curate(m => m.Engine == Engine.Local);
        Assert.Contains(c.Featured, f => f.Badges.Contains(PickerBadge.Recommended) && f.Model.Engine == Engine.Gemini);
    }

    [Fact]
    public void ShortList_ShowsEverything()
    {
        var models = Speech.Where(m => m.Engine != Engine.OpenAI).ToList();
        var c = PickerCuration.Curate(models, ModelPickers.Recommended(models, Settings.DefaultTranscriptionModel, "x"), null, _ => true);
        Assert.Empty(c.More);
        Assert.Equal(models.Count, c.Featured.Count);
    }

    [Fact]
    public void Prompt_FastestIsFlashLite35_WithGemini()
    {
        var models = ModelPickers.Prompt("Qwen3 4B");
        var c = PickerCuration.Curate(models, ModelPickers.Recommended(models, Settings.DefaultPromptModel, OpenAiModels.DefaultPromptModel),
            null, m => m.Engine != Engine.OpenAI);
        Assert.Equal("gemini-3.5-flash-lite", c.Featured.Single(f => f.Badges.Contains(PickerBadge.Fastest)).Model.Id);
    }

    [Fact]
    public void SpeedRanks_matchTheValuesTheMapsHadBeforeTheyMovedOntoModelInfo()
    {
        var transcription = new Dictionary<string, int>
        {
            ["gemini-3.1-flash-lite"] = 1, ["gpt-4o-mini-transcribe"] = 2, ["gemini-3.5-flash-lite"] = 3,
            ["gpt-4o-transcribe"] = 4, ["gpt-transcribe"] = 5, ["gemini-3.8-flash"] = 6,
        };
        var prompt = new Dictionary<string, int>
        {
            ["gemini-3.5-flash-lite"] = 1, ["gemini-3.1-flash-lite"] = 2, ["gpt-6-luna"] = 3, ["gpt-5.4-mini"] = 4,
            ["gemini-3.8-flash"] = 5, ["gpt-6-sol"] = 6,
        };
        foreach (var (models, expected) in new[] { (ModelPickers.Transcription("x"), transcription), (ModelPickers.Prompt("x"), prompt) })
            foreach (var m in models)
                Assert.Equal(expected.TryGetValue(m.Id, out var r) ? r : (int?)null, m.SpeedRank);
    }
}
