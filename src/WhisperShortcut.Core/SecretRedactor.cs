using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Masks API keys and bearer tokens so they never reach the log file or a problem report.
/// Lengths and surrounding text stay, the secret itself does not.
/// </summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{20,}")]
    private static partial Regex GeminiKey();

    // sk-…, sk-proj-…, sk-svcacct-…, sk-admin-… (OpenAI). 20+ payload chars: short "sk-abc" stays.
    [GeneratedRegex(@"sk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9_\-]{20,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"(?i)(?<=(?:Bearer|x-goog-api-key|x-api-key)\s+)[^\s,;]+")]
    private static partial Regex AuthToken();

    /// <summary>Returns <paramref name="text"/> with recognised secrets replaced by an ellipsis form.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = GeminiKey().Replace(text, "AIza…");
        s = OpenAiKey().Replace(s, "sk-…");
        return AuthToken().Replace(s, "…");
    }
}
