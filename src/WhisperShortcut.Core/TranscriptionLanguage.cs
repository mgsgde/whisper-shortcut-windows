namespace WhisperShortcut.Core;

/// <summary>
/// The spoken language for OpenAI transcription (Mac WhisperLanguage, Diktieren → „Sprache“). "auto" lets the model
/// detect it. Gemini and Parakeet take no language parameter and ignore it.
/// </summary>
public static class TranscriptionLanguages
{
    /// <summary>Stored value for "Automatisch erkennen".</summary>
    public const string Auto = "auto";

    /// <summary>Code (ISO 639-1, as the OpenAI API takes it) and German name, in the Mac's order.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All = new[]
    {
        (Auto, "Automatisch erkennen"),
        ("en", "Englisch"), ("de", "Deutsch"), ("fr", "Französisch"), ("es", "Spanisch"), ("it", "Italienisch"),
        ("pt", "Portugiesisch"), ("ru", "Russisch"), ("ja", "Japanisch"), ("ko", "Koreanisch"), ("zh", "Chinesisch"),
        ("nl", "Niederländisch"), ("pl", "Polnisch"), ("tr", "Türkisch"), ("sv", "Schwedisch"), ("da", "Dänisch"),
        ("no", "Norwegisch"), ("fi", "Finnisch"), ("cs", "Tschechisch"), ("hu", "Ungarisch"), ("ro", "Rumänisch"),
        ("el", "Griechisch"), ("ar", "Arabisch"), ("hi", "Hindi"),
    };

    /// <summary>A known code, lower case; anything else (missing, unknown) is <see cref="Auto"/>.</summary>
    public static string Normalize(string? code)
    {
        var c = code?.Trim().ToLowerInvariant() ?? "";
        return All.Any(l => l.Code == c) ? c : Auto;
    }

    /// <summary>The <c>language</c> field for an OpenAI transcription request, or null for "auto" (the field is left out).</summary>
    public static string? ForRequest(string? code)
    {
        var c = Normalize(code);
        return c == Auto ? null : c;
    }

    /// <summary>German name of a stored code.</summary>
    public static string Name(string? code)
    {
        var c = Normalize(code);
        return All.First(l => l.Code == c).Name;
    }
}
