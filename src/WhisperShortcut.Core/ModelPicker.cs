namespace WhisperShortcut.Core;

/// <summary>Why a model sits in a picker's top row (Mac ModelPickerBadge). A model can carry several.</summary>
public enum PickerBadge { Recommended, Fastest, Offline }

/// <summary>
/// One entry of a model picker: where it runs and which model. <paramref name="SpeedRank"/>: lower is faster at the
/// picker's job, null = never featured as the fastest (Mac pickerSpeedRank; models on this PC compete for Offline).
/// </summary>
public sealed record PickerModel(Engine Engine, string Id, string Name, string Description, int? SpeedRank = null);

/// <summary>
/// The curated split of one picker (port of the Mac's ModelPickerCuration): at most Recommended · Fastest · Offline in
/// the top row, the current selection added when it is none of those, the rest behind „Weitere Modelle“.
/// Recommended and Fastest only go to models that can run with the keys the user has.
/// </summary>
public sealed record PickerCuration(IReadOnlyList<(PickerModel Model, IReadOnlyList<PickerBadge> Badges)> Featured, IReadOnlyList<PickerModel> More)
{
    /// <summary>Below this many models the disclosure is not worth a click: everything is in the top row (Mac).</summary>
    public const int DisclosureThreshold = 5;

    /// <param name="recommended">The picker's recommendations in order of preference (one per provider).</param>
    /// <param name="usable">Whether a model can run now (its provider has a key; models on this PC always can).</param>
    public static PickerCuration Curate(IReadOnlyList<PickerModel> models, IReadOnlyList<PickerModel> recommended,
        PickerModel? selected, Func<PickerModel, bool> usable)
    {
        // Recommended and Fastest are cloud slots (models on this PC have the Offline slot). No usable cloud model (no key
        // at all): the slots keep the global choice; an honest "Schlüssel nötig" beats an empty row.
        var cloud = models.Where(m => m.Engine != Engine.Local).ToList();
        var usableCloud = cloud.Where(usable).ToList();
        var candidates = usableCloud.Count > 0 ? usableCloud : cloud;
        var featured = new List<(PickerModel Model, List<PickerBadge> Badges)>();
        void Feature(PickerModel? model, PickerBadge badge)
        {
            if (model == null || !models.Contains(model)) return;
            int i = featured.FindIndex(f => f.Model == model);
            if (i >= 0) featured[i].Badges.Add(badge);
            else featured.Add((model, new List<PickerBadge> { badge }));
        }
        Feature(recommended.FirstOrDefault(candidates.Contains) ?? candidates.FirstOrDefault(), PickerBadge.Recommended);
        Feature(candidates.Where(m => m.SpeedRank != null).MinBy(m => m.SpeedRank), PickerBadge.Fastest);
        Feature(models.FirstOrDefault(m => m.Engine == Engine.Local), PickerBadge.Offline);
        if (selected != null && models.Contains(selected) && featured.All(f => f.Model != selected))
            featured.Add((selected, new List<PickerBadge>()));
        var rest = models.Where(m => featured.All(f => f.Model != m)).ToList();
        if (models.Count < DisclosureThreshold)
        {
            featured.AddRange(rest.Select(m => (m, new List<PickerBadge>())));
            rest.Clear();
        }
        return new PickerCuration(featured.Select(f => (f.Model, (IReadOnlyList<PickerBadge>)f.Badges)).ToList(), rest);
    }
}

/// <summary>The two pickers of the Windows app: speech recognition (Diktieren) and text processing (Dictate Prompt).</summary>
public static class ModelPickers
{
    /// <summary>The Id of the entry that runs on this PC.</summary>
    public const string LocalId = "local";

    public static IReadOnlyList<PickerModel> Transcription(string localName) =>
        Build(GeminiModels.Transcription, OpenAiModels.Transcription, m => m.TranscriptionSpeed,
            new PickerModel(Engine.Local, LocalId, localName, "Auf diesem PC, kostenlos, nichts verlässt den Rechner"));

    public static IReadOnlyList<PickerModel> Prompt(string localName) =>
        Build(GeminiModels.Prompt, OpenAiModels.Prompt, m => m.PromptSpeed,
            new PickerModel(Engine.Local, LocalId, localName, "Auf diesem PC, kostenlos, nichts verlässt den Rechner; langsamer"));

    /// <summary>The recommendations in order: Gemini first (the Windows default), then OpenAI.</summary>
    public static IReadOnlyList<PickerModel> Recommended(IReadOnlyList<PickerModel> models, string geminiDefault, string openAiDefault) =>
        new[] { Find(models, Engine.Gemini, geminiDefault), Find(models, Engine.OpenAI, openAiDefault) }
            .Where(m => m != null).Select(m => m!).ToList();

    public static PickerModel? Find(IReadOnlyList<PickerModel> models, Engine engine, string id) =>
        models.FirstOrDefault(m => m.Engine == engine && (engine == Engine.Local || m.Id == id));

    private static IReadOnlyList<PickerModel> Build(IEnumerable<ModelInfo> gemini, IEnumerable<ModelInfo> openAi,
        Func<ModelInfo, int?> speed, PickerModel local) =>
        gemini.Select(m => new PickerModel(Engine.Gemini, m.Id, m.Name, m.Description, speed(m)))
            .Concat(openAi.Select(m => new PickerModel(Engine.OpenAI, m.Id, m.Name, m.Description, speed(m))))
            .Append(local)
            .ToList();
}
