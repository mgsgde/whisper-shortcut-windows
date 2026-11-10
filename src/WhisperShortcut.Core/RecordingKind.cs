namespace WhisperShortcut.Core;

/// <summary>What kind of take a crash-recovery checkpoint holds.</summary>
public enum RecordingKindName { Dictate, Prompt, Template, Live }

/// <summary>
/// The "mode" string of <see cref="RecordingCheckpoint"/>: "Dictate", "Prompt", "Live", or "Template:&lt;id&gt;".
/// Older builds wrote "Dictate", "Prompt" or a bare "Template" (no id); those still parse. Anything unknown is Dictate.
/// </summary>
public static class RecordingKind
{
    private const string TemplatePrefix = "Template:";

    public static string Format(RecordingKindName kind, string? templateId = null) => kind switch
    {
        RecordingKindName.Prompt => "Prompt",
        RecordingKindName.Live => "Live",
        RecordingKindName.Template => string.IsNullOrWhiteSpace(templateId) ? "Template" : TemplatePrefix + templateId.Trim(),
        _ => "Dictate",
    };

    /// <summary>Kind and template id (null unless a "Template:&lt;id&gt;" value). Unknown or empty text is Dictate.</summary>
    public static (RecordingKindName Kind, string? TemplateId) Parse(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.StartsWith(TemplatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = s[TemplatePrefix.Length..].Trim();
            return (RecordingKindName.Template, id.Length == 0 ? null : id);
        }
        if (s.Equals("Template", StringComparison.OrdinalIgnoreCase)) return (RecordingKindName.Template, null);
        if (s.Equals("Prompt", StringComparison.OrdinalIgnoreCase)) return (RecordingKindName.Prompt, null);
        if (s.Equals("Live", StringComparison.OrdinalIgnoreCase)) return (RecordingKindName.Live, null);
        return (RecordingKindName.Dictate, null);
    }
}
