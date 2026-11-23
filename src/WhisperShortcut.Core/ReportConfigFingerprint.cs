using System.Security.Cryptography;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>A short hash of the prompts and templates a dictation ran with, to tell whether they changed since (Mac ReportConfigFingerprint).</summary>
public static class ReportConfigFingerprint
{
    /// <summary>First 12 lowercase hex characters of the SHA-256 of <paramref name="text"/> (UTF-8).</summary>
    public static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..12];

    /// <summary>Hash of the prompt sections, the glossary and every template, in a stable order.</summary>
    public static string Current(Settings s)
    {
        var parts = new List<string>
        {
            "dictation", s.DictateSystemPrompt.Trim(),
            "dictate_prompt", s.PromptModeSystemPrompt.Trim(),
            "template", s.DictatePromptTemplate.Trim(),
            "glossary", string.Join("\n", s.Glossary).Trim(),
        };
        foreach (var t in s.Templates.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            parts.Add(t.Name);
            parts.Add(t.Instructions);
        }
        // Length-prefixed, so a separator inside a prompt cannot make two configurations hash the same.
        return ShortHash(string.Concat(parts.Select(p => $"{Encoding.UTF8.GetByteCount(p)}:{p}")));
    }
}
