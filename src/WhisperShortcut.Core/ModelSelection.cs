namespace WhisperShortcut.Core;

/// <summary>The two kinds of model step a provider and model are chosen for: speech to text, and text to text.</summary>
public enum ModelStep
{
    /// <summary>Speech recognition (Diktieren, meeting transcription).</summary>
    Transcription,

    /// <summary>Text processing (Dictate Prompt, meeting summary, Chat).</summary>
    Text,
}

/// <summary>What a <see cref="ModelStep"/> offers per provider and how stored ids map onto it.</summary>
public static class ModelSteps
{
    /// <summary>The Gemini models offered for the step.</summary>
    public static IReadOnlyList<ModelInfo> GeminiCatalog(this ModelStep step) =>
        step == ModelStep.Transcription ? GeminiModels.Transcription : GeminiModels.Prompt;

    /// <summary>The OpenAI models offered for the step.</summary>
    public static IReadOnlyList<ModelInfo> OpenAiCatalog(this ModelStep step) =>
        step == ModelStep.Transcription ? OpenAiModels.Transcription : OpenAiModels.Prompt;

    /// <summary>A valid Gemini model id for the step: the id itself, its successor, or the default.</summary>
    public static string NormalizeGemini(this ModelStep step, string? id) =>
        step == ModelStep.Transcription ? GeminiModels.NormalizeTranscription(id) : GeminiModels.NormalizePrompt(id);

    /// <summary>A valid OpenAI model id for the step: the id itself, its successor, or the default.</summary>
    public static string NormalizeOpenAi(this ModelStep step, string? id) =>
        step == ModelStep.Transcription ? OpenAiModels.NormalizeTranscription(id) : OpenAiModels.NormalizePrompt(id);
}

/// <summary>
/// One step's choice: the provider and the model id of each cloud provider (the id of the other provider stays stored, so
/// switching back keeps it). Immutable; Settings holds one per step (Diktieren, Dictate Prompt, meeting transcription,
/// meeting summary, Chat).
/// </summary>
public sealed record ModelSelection(Engine Engine, string Gemini, string OpenAi)
{
    /// <summary>The factory choice of a step: Gemini with the step's default models.</summary>
    public static ModelSelection Default(ModelStep step) => step == ModelStep.Transcription
        ? new(Engine.Gemini, Settings.DefaultTranscriptionModel, OpenAiModels.DefaultTranscriptionModel)
        : new(Engine.Gemini, Settings.DefaultPromptModel, OpenAiModels.DefaultPromptModel);

    /// <summary>The model id of <paramref name="engine"/>: its Gemini or OpenAI id, <see cref="ModelPickers.LocalId"/> on this PC.</summary>
    public string ModelFor(Engine engine) => engine switch
    {
        Engine.Gemini => Gemini,
        Engine.OpenAI => OpenAi,
        _ => ModelPickers.LocalId,
    };

    /// <summary>What actually runs: offline mode (nothing leaves this PC) overrides the provider with <see cref="Engine.Local"/>.</summary>
    public (Engine Engine, string Model) Effective(bool offline)
    {
        var engine = offline ? Engine.Local : Engine;
        return (engine, ModelFor(engine));
    }

    /// <summary>The choice after picking <paramref name="id"/> of <paramref name="engine"/> (a picker tile): that provider, and its model id when it is a cloud one.</summary>
    public ModelSelection Choose(ModelStep step, Engine engine, string id) => engine switch
    {
        Engine.Gemini => this with { Engine = engine, Gemini = step.NormalizeGemini(id) },
        Engine.OpenAI => this with { Engine = engine, OpenAi = step.NormalizeOpenAi(id) },
        _ => this with { Engine = engine },
    };

    /// <summary>Both model ids mapped onto the step's current catalogs (renamed ids to their successor, unknown to the default).</summary>
    public ModelSelection Normalize(ModelStep step) => this with { Gemini = step.NormalizeGemini(Gemini), OpenAi = step.NormalizeOpenAi(OpenAi) };

    /// <summary>
    /// The choice read from a settings file: provider token and model ids, each missing value taken from
    /// <paramref name="fallback"/> (the same step's factory choice, or the choice an older file's meetings and chat followed).
    /// </summary>
    internal static ModelSelection Load(ModelStep step, string? engine, string? gemini, string? openAi, ModelSelection fallback) =>
        new ModelSelection(engine != null ? Choices.Engine.Parse(engine) : fallback.Engine, gemini ?? fallback.Gemini, openAi ?? fallback.OpenAi)
            .Normalize(step);
}
