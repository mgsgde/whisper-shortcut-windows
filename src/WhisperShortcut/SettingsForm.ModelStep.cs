namespace WhisperShortcut;

/// <summary>
/// The controls of one model step (Diktieren, Dictate Prompt, meeting transcription, meeting summary, Chat): the provider combo
/// and the Gemini and OpenAI model combos. They are the step's stored state; the model picker tiles
/// (<see cref="SettingsForm.ModelPicker"/>) set them, and saving reads <see cref="Selected"/>.
/// </summary>
internal sealed partial class SettingsForm
{
    private sealed class ModelStepControls
    {
        public ModelStepControls(Core.ModelStep step, Core.ModelSelection shown, string engineName, string localLabel)
        {
            Step = step;
            Engine = EngineChoice(shown.Engine, engineName, localLabel);
            Gemini = ModelChoice(Core.ModelSteps.GeminiCatalog(step), shown.Gemini);
            OpenAi = ModelChoice(Core.ModelSteps.OpenAiCatalog(step), shown.OpenAi);
        }

        public Core.ModelStep Step { get; }
        public FluentCombo Engine { get; }
        public FluentCombo Gemini { get; }
        public FluentCombo OpenAi { get; }

        public IEnumerable<FluentCombo> Combos => new[] { Engine, Gemini, OpenAi };

        public Core.Engine SelectedEngine => SettingsForm.SelectedEngine(Engine);

        /// <summary>What the combos show, as a settings value.</summary>
        public Core.ModelSelection Selected => new(SelectedEngine, SelectedModel(Gemini), SelectedModel(OpenAi));

        /// <summary>The state for the window's change detection.</summary>
        public string Snapshot => string.Join("\u0002", SelectedEngine, SelectedModel(Gemini), SelectedModel(OpenAi));

        /// <summary>The three combos as hidden state holders (a step without its own cards).</summary>
        public Panel Hide() => Hidden(Engine, Gemini, OpenAi);
    }
}
