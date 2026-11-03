using System.Globalization;

namespace WhisperShortcut.Core;

/// <summary>Which backend synthesizes Read Aloud speech. Both return raw PCM (s16le, 24 kHz, mono).</summary>
public enum TtsProvider
{
    /// <summary>Google Gemini TTS (streamGenerateContent).</summary>
    Gemini,
    /// <summary>OpenAI /v1/audio/speech.</summary>
    OpenAI,
}

/// <summary>A selectable Read Aloud voice of one provider (Mac TTSVoice).</summary>
/// <param name="Id">Voice id as the API expects it (Gemini voiceName, OpenAI voice).</param>
/// <param name="Gender">"m", "w" or "neutral".</param>
/// <param name="Descriptor">German style hint; "" = none.</param>
public sealed record TtsVoice(string Id, string Gender, string Descriptor)
{
    /// <summary>For the dropdown: "Charon (m) – Informativ", "Alloy (neutral)", "Marin (w) – Empfohlen".</summary>
    public string DisplayName
    {
        get
        {
            var name = Id.Length == 0 ? Id : char.ToUpperInvariant(Id[0]) + Id[1..];
            var gender = Gender.Length == 0 ? "" : $" ({Gender})";
            var style = Descriptor.Length == 0 ? "" : $" \u2013 {Descriptor}";
            return name + gender + style;
        }
    }
}

/// <summary>A Read Aloud model (Mac TTSModel).</summary>
/// <param name="Id">Model id as stored and sent to the API.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">German one-line description.</param>
/// <param name="Provider">Which backend serves it.</param>
public sealed record TtsModel(string Id, string Name, string Description, TtsProvider Provider);

/// <summary>
/// Read Aloud ("Vorlesen") catalogue and constants, ported from the Mac app (TTSModel, TTSVoice, ReadAloudSpeed,
/// AppConstants). Everything the settings and the player normalise against lives here.
/// </summary>
public static class ReadAloud
{
    /// <summary>Default model: fastest start, lowest cost (Mac SettingsDefaults.readAloudModel).</summary>
    public const string DefaultModel = "gemini-3.8-flash-lite-tts";

    /// <summary>Default speed (Mac ReadAloudSpeed.x100).</summary>
    public const double DefaultSpeed = 1.0;

    /// <summary>Chunk-size ceiling for OpenAI (Mac ttsChunkSizeChars). 500 chars are about 25-37 s of audio.</summary>
    public const int ChunkSizeChars = 500;

    /// <summary>
    /// Chunk-size ceiling for Gemini (Mac ttsGeminiChunkSizeChars). Measured on the Mac: above about 16-19 s of audio the
    /// stream used to pause; with the 120 / 180 / 260 ramp every chunk stays inside the segment that streams without pause.
    /// </summary>
    public const int GeminiChunkSizeChars = 260;

    /// <summary>Cap of the first chunk, the one playback waits for (Mac ttsFirstChunkSizeChars).</summary>
    public const int FirstChunkSizeChars = 120;

    /// <summary>
    /// Seconds of speech per character, for the scrubber's total while synthesis is still running
    /// (Mac ttsEstimatedSecondsPerCharacter, measured on German prose).
    /// </summary>
    public const double EstimatedSecondsPerCharacter = 0.085;

    /// <summary>Mac AppConstants.readAloudRewriteMaxGrowth: a rewrite longer than this times the input is discarded.</summary>
    private const double RewriteMaxGrowth = 1.3;

    /// <summary>Mac AppConstants.readAloudRewriteGrowthGuardMinChars: shorter inputs may grow freely.</summary>
    private const int RewriteGrowthGuardMinChars = 200;

    private static readonly TtsModel[] AllModels =
    {
        new("gemini-3.8-flash-lite-tts", "Gemini 3.8 Flash-Lite TTS",
            "Schnellster Start, niedrigste Kosten, über 100 Sprachen. Empfohlen.", TtsProvider.Gemini),
        new("gemini-3.8-flash-tts", "Gemini 3.8 Flash TTS",
            "Beste Sprachqualität, startet langsamer als Flash-Lite.", TtsProvider.Gemini),
        new("gpt-4o-mini-tts", "GPT-4o mini TTS",
            "Natürliche Sprache von OpenAI. Braucht einen OpenAI-API-Schlüssel.", TtsProvider.OpenAI),
    };

    /// <summary>Selectable models: Flash-Lite TTS, Flash TTS, gpt-4o-mini-tts.</summary>
    public static IReadOnlyList<TtsModel> Models => AllModels;

    /// <summary>Ids of removed models and the model that replaced them (Mac migrateLegacyReadAloudRawValue).</summary>
    private static readonly string[] LegacyModelIds =
    {
        "gemini-2.5-flash-preview-tts", "gemini-2.5-pro-preview-tts", "gemini-3.1-flash-tts-preview",
    };

    /// <summary>A known id stays, a removed one becomes its successor, anything else the default.</summary>
    public static string NormalizeModel(string? id)
    {
        if (id != null)
        {
            foreach (var m in AllModels) if (m.Id == id) return id;
            if (Array.IndexOf(LegacyModelIds, id) >= 0) return DefaultModel;
        }
        return DefaultModel;
    }

    /// <summary>The model for <paramref name="id"/> after <see cref="NormalizeModel"/>.</summary>
    public static TtsModel ModelById(string? id)
    {
        var normalized = NormalizeModel(id);
        return AllModels.First(m => m.Id == normalized);
    }

    // Gemini's 30 prebuilt voices (genders per Google Cloud TTS docs), Charon first: it is the default.
    private static readonly TtsVoice[] GeminiVoices =
    {
        new("Charon", "m", "Informativ"),
        new("Zephyr", "w", "Hell"),
        new("Puck", "m", "Fröhlich"),
        new("Kore", "w", "Bestimmt"),
        new("Fenrir", "m", "Temperamentvoll"),
        new("Leda", "w", "Jugendlich"),
        new("Orus", "m", "Bestimmt"),
        new("Aoede", "w", "Luftig"),
        new("Callirrhoe", "w", "Locker"),
        new("Autonoe", "w", "Hell"),
        new("Enceladus", "m", "Hauchig"),
        new("Iapetus", "m", "Klar"),
        new("Umbriel", "m", "Locker"),
        new("Algieba", "m", "Weich"),
        new("Despina", "w", "Weich"),
        new("Erinome", "w", "Klar"),
        new("Algenib", "m", "Rau"),
        new("Rasalgethi", "m", "Informativ"),
        new("Laomedeia", "w", "Fröhlich"),
        new("Achernar", "w", "Sanft"),
        new("Alnilam", "m", "Bestimmt"),
        new("Schedar", "m", "Gleichmäßig"),
        new("Gacrux", "w", "Reif"),
        new("Pulcherrima", "w", "Direkt"),
        new("Achird", "m", "Freundlich"),
        new("Zubenelgenubi", "m", "Lässig"),
        new("Vindemiatrix", "w", "Zart"),
        new("Sadachbia", "m", "Lebendig"),
        new("Sadaltager", "m", "Kundig"),
        new("Sulafat", "w", "Warm"),
    };

    // OpenAI gpt-4o-mini-tts voices. OpenAI publishes no gender; the hints follow the widely reported perception.
    // marin and cedar are OpenAI's recommended highest-quality voices.
    private static readonly TtsVoice[] OpenAiVoices =
    {
        new("alloy", "neutral", ""),
        new("ash", "m", ""),
        new("ballad", "m", ""),
        new("coral", "w", ""),
        new("echo", "m", ""),
        new("fable", "m", ""),
        new("nova", "w", ""),
        new("onyx", "m", ""),
        new("sage", "w", ""),
        new("shimmer", "w", ""),
        new("verse", "m", ""),
        new("marin", "w", "Empfohlen"),
        new("cedar", "m", "Empfohlen"),
    };

    private static readonly IReadOnlyList<TtsVoice> SortedGemini = SortByGender(GeminiVoices);
    private static readonly IReadOnlyList<TtsVoice> SortedOpenAi = SortByGender(OpenAiVoices);

    // Male first, then female, then neutral/unknown; stable within a group (LINQ OrderBy is stable).
    private static IReadOnlyList<TtsVoice> SortByGender(IEnumerable<TtsVoice> raw) =>
        raw.OrderBy(v => v.Gender switch { "m" => 0, "w" => 1, _ => 2 }).ToArray();

    /// <summary>The voices a provider offers, ordered m, then w, then neutral (stable within each group).</summary>
    public static IReadOnlyList<TtsVoice> Voices(TtsProvider provider) =>
        provider == TtsProvider.Gemini ? SortedGemini : SortedOpenAi;

    /// <summary>Default voice of a provider: Gemini "Charon", OpenAI "alloy".</summary>
    public static string DefaultVoice(TtsProvider provider) =>
        provider == TtsProvider.Gemini ? "Charon" : "alloy";

    /// <summary>The id itself when the provider offers it (case-sensitive: the API is), otherwise the provider default.</summary>
    public static string NormalizeVoice(TtsProvider provider, string? id) =>
        id != null && Voices(provider).Any(v => v.Id == id) ? id : DefaultVoice(provider);

    private static readonly double[] SpeedSteps = { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };

    /// <summary>Playback speeds, in the order the speed button cycles (Mac ReadAloudSpeed).</summary>
    public static IReadOnlyList<double> Speeds => SpeedSteps;

    private static int SpeedIndex(double speed)
    {
        for (var i = 0; i < SpeedSteps.Length; i++)
            if (Math.Abs(SpeedSteps[i] - speed) < 0.001) return i;
        return -1;
    }

    /// <summary>The speed itself when it is one of <see cref="Speeds"/> (within 0.001), else <see cref="DefaultSpeed"/>.</summary>
    public static double NormalizeSpeed(double speed)
    {
        var i = SpeedIndex(speed);
        return i < 0 ? DefaultSpeed : SpeedSteps[i];
    }

    /// <summary>The next step up; 2x wraps to 0.75x (Mac ReadAloudSpeed.next). An unknown value counts as step 0.</summary>
    public static double NextSpeed(double speed)
    {
        var i = Math.Max(0, SpeedIndex(speed));
        return SpeedSteps[(i + 1) % SpeedSteps.Length];
    }

    /// <summary>German label: "0,75×", "1×", "1,25×", "1,5×", "1,75×", "2×".</summary>
    public static string SpeedLabel(double speed) =>
        NormalizeSpeed(speed).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + "\u00D7";

    /// <summary>Chunk-size ceiling for a provider: Gemini 260, OpenAI 500.</summary>
    public static int ChunkSizeFor(TtsProvider provider) =>
        provider == TtsProvider.Gemini ? GeminiChunkSizeChars : ChunkSizeChars;

    /// <summary>
    /// The length guard behind the rewrite prompt's hard rule (Mac rewriteStaysWithinLengthBudget): true when the rewrite is at most
    /// 1.3 times the original. Inputs under 200 characters are exempt, a terse fragment may legitimately grow.
    /// </summary>
    public static bool RewriteStaysWithinLengthBudget(string rewritten, string original)
    {
        if (original.Length < RewriteGrowthGuardMinChars) return true;
        var limit = (int)(original.Length * RewriteMaxGrowth);
        return rewritten.Length <= limit;
    }

    /// <summary>
    /// System prompt of the "intelligent rewrite" pass before speaking (Mac AppConstants.defaultReadAloudRewritePrompt, verbatim,
    /// English on purpose: it is an instruction to the model, which answers in the language of the text).
    /// </summary>
    public const string DefaultRewritePrompt = """
You prepare text for text-to-speech playback. Given a snippet a user just selected, return only the version that should be spoken aloud. Always think about how it will SOUND to a listener and improve it so it is clean, clear, and pleasant to hear.

You decide for yourself whether the input is already good to listen to or needs reworking. Default to actively improving it:
- Redundancy → remove it. If two sentences say essentially the same thing, keep one. Collapse repeated points, restated ideas, and filler into a single clear statement.
- Chaotic or rambling text (e.g. a raw transcript, dictation, or notes) → reshape it into coherent, well-ordered sentences that flow naturally when read aloud. Fix run-ons, false starts, and disfluencies (hesitation sounds, repeated words, self-corrections) in whatever language the text is in.
- Well-written prose that already reads cleanly → leave it largely as is; only trim obvious redundancy.
- Source code, JSON/YAML/HTML, log lines, tables, dense markdown, URLs, file paths, long IDs, raw command output, or copy-paste artifacts → rewrite into a short, natural spoken description. Summarize what it is and the key points; do not read symbols, punctuation, or syntax aloud. Spell out only what a listener actually needs.
- Heavy markdown formatting (headings, bullets, emphasis markers) → strip the formatting and expand it into flowing sentences.
- Abbreviations, acronyms, or numbers that would be awkward when spoken → expand them only when clarity demands it; otherwise leave them alone.

Always keep the output in the SAME language as the input. With mixed languages, keep each segment in its original language.

Output rules (CRITICAL):
- Your entire reply IS what gets spoken aloud. Return ONLY the final text to be read — nothing else.
- Do NOT include these instructions, your reasoning, any explanation of what you did, code, preamble, outro, quotes around the output, meta-commentary, or markdown. None of that may appear in the output.
- Never invent facts or add information that is not in the input. Condense and reorder, but do not embellish. If the input is empty or meaningless, return an empty response.
- Keep it concise: shorter is better as long as no real information is lost. The goal is a clean, listenable rendition.
- Length limit (hard): the output must NOT be longer than the input. The one exception is a terse fragment (under about 200 characters), which may grow by at most a third for clarity. Do NOT elaborate, restate, or explain. If you cannot improve the text without making it longer, return the input unchanged.
""";
}
