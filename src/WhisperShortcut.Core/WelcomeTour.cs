namespace WhisperShortcut.Core;

/// <summary>The steps of the welcome tour, in order (Mac Onboarding/WelcomeView.swift: WelcomeStep).</summary>
public enum WelcomeStep
{
    /// <summary>"Willkommen bei WhisperShortcut".</summary>
    Intro,

    /// <summary>"Wie soll das Diktat laufen?": Offline or Cloud.</summary>
    Setup,

    /// <summary>The microphone (Windows: is it allowed and does it hear something?).</summary>
    Permissions,

    /// <summary>"Jetzt ausprobieren": the first dictation with the dictate shortcut.</summary>
    TryIt,

    /// <summary>The active shortcuts, then "Fertig".</summary>
    Done,
}

/// <summary>Where dictation should run, the one decision of the tour (Mac DictationChoice).</summary>
public enum DictationChoice
{
    /// <summary>Parakeet on this PC.</summary>
    Offline,

    /// <summary>Gemini or OpenAI with a key.</summary>
    Cloud,
}

/// <summary>Rules of the welcome tour, apart from its window (Mac WelcomeStep, OnboardingTranscriptionChoice).</summary>
public static class WelcomeTour
{
    /// <summary>All steps in order.</summary>
    public static readonly IReadOnlyList<WelcomeStep> Steps = Enum.GetValues<WelcomeStep>();

    /// <summary>The step to resume on from a stored index; anything out of range starts over (Mac: resume(from:)).</summary>
    public static WelcomeStep Resume(int stored) =>
        stored >= 0 && stored < Steps.Count ? Steps[stored] : WelcomeStep.Intro;

    /// <summary>"2 von 5".</summary>
    public static string IndexLabel(WelcomeStep step) => $"{(int)step + 1} von {Steps.Count}";

    /// <summary>
    /// An install that ran before the tour existed counts as done when it was already set up (a key or offline mode),
    /// so an update does not show the tour to people who use the app every day. A fresh install has no file: not done.
    /// </summary>
    public static bool CompletedForExistingFile(string apiKey, string openAiKey, bool offlineMode) =>
        apiKey.Trim().Length > 0 || openAiKey.Trim().Length > 0 || offlineMode;

    /// <summary>Whether a dictation can run with these settings (the engine it uses has its key or its model).</summary>
    public static bool TranscriptionReady(Settings s, bool speechModelInstalled) => s.EffectiveSpeechEngine is var engine
        && (engine == Engine.Local ? speechModelInstalled : s.MissingKeyProvider(engine) == null);

    /// <summary>"Weiter" is enabled (Mac canAdvance): the setup step needs a dictation that can run.</summary>
    public static bool CanAdvance(WelcomeStep step, bool transcriptionReady) =>
        step != WelcomeStep.Setup || transcriptionReady;

    /// <summary>What a revisited tour shows as chosen; null on a fresh install (Mac DictationChoice.inferred).</summary>
    public static DictationChoice? Inferred(Settings s, bool speechModelInstalled)
    {
        if (s.OfflineMode) return DictationChoice.Offline;
        if (s.ApiKey.Trim().Length > 0 || s.OpenAiKey.Trim().Length > 0) return DictationChoice.Cloud;
        if (speechModelInstalled) return DictationChoice.Offline;
        return null;
    }

    /// <summary>
    /// The user picked Offline: dictation runs on this PC. The offline-mode switch stays the user's own. Meetings follow
    /// dictation again (Mac R202), so their audio stays on this PC too; an earlier explicit cloud pick is dropped for that.
    /// </summary>
    public static void ChooseOffline(Settings s)
    {
        s.Dictate = s.Dictate with { Engine = Engine.Local };
        s.MeetingSpeechEngineExplicit = false;
    }

    /// <summary>
    /// The user picked Cloud or saved a key (Mac OnboardingTranscriptionChoice.preferCloud): offline mode goes off
    /// (it would hide the cloud), and every step that cannot run moves to a provider that has a key, Google first.
    /// A local step moves too: picking Cloud was the answer.
    /// </summary>
    public static void ChooseCloud(Settings s)
    {
        s.OfflineMode = false;
        var preferred = s.ApiKey.Trim().Length > 0 ? Engine.Gemini : s.OpenAiKey.Trim().Length > 0 ? Engine.OpenAI : (Engine?)null;
        if (preferred == null) return;
        if (!HasKey(s, s.Dictate.Engine)) s.Dictate = s.Dictate with { Engine = preferred.Value };
        if (!HasKey(s, s.Prompt.Engine)) s.Prompt = s.Prompt with { Engine = preferred.Value };
    }

    private static bool HasKey(Settings s, Engine e) => e != Engine.Local && s.MissingKeyProvider(e) == null;

    /// <summary>The closing line of the tour (Mac #346: "You can revisit this tour any time from Welcome Tour… in the menu bar.").</summary>
    public const string RevisitHint = "Du findest die Einführung jederzeit wieder unter „Einführung …“ im Menü des WhisperShortcut-Symbols unten rechts.";
}
