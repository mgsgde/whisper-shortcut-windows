namespace WhisperShortcut.Core;

/// <summary>
/// One saved set of Dictate Prompt settings, e.g. for one customer (a practice, a workshop).
/// The user switches between them in the tray menu or in the settings.
/// </summary>
public sealed class PromptProfile
{
    /// <summary>Stable id (see <see cref="PromptProfiles.NewId"/>); the name can change, the id does not.</summary>
    public string Id { get; set; } = "";

    /// <summary>Shown in the tray menu, the settings and the recording overlay.</summary>
    public string Name { get; set; } = "";

    /// <summary>The Dictate Prompt system prompt ("Anweisungen"); "" = <see cref="Prompts.DefaultPromptModeSystemPrompt"/>.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>"Vorlage für neue Texte": appended to the instructions, applied only when nothing is selected; "" = none.</summary>
    public string Template { get; set; } = "";

    /// <summary>Quick instructions offered while recording (see <see cref="QuickActionList"/>).</summary>
    public List<string> QuickActions { get; set; } = new(QuickActionList.Defaults);
}

/// <summary>Helpers for <see cref="PromptProfile"/> lists.</summary>
public static class PromptProfiles
{
    /// <summary>Name of the profile created for a fresh install or migrated from a pre-profile settings file.</summary>
    public const string DefaultName = "Standard";

    /// <summary>A new unique profile id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// <paramref name="wanted"/> if no existing name equals it (ignoring case), otherwise "wanted 2", "wanted 3" …
    /// A blank wish becomes "Profil".
    /// </summary>
    public static string UniqueName(IEnumerable<string> existing, string wanted)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var baseName = string.IsNullOrWhiteSpace(wanted) ? "Profil" : wanted.Trim();
        if (!taken.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>A deep copy with a new id, named "&lt;Name&gt; (Kopie)", made unique against <paramref name="existingNames"/> (default: just the source's name).</summary>
    public static PromptProfile Duplicate(PromptProfile source, IEnumerable<string>? existingNames = null) => new()
    {
        Id = NewId(),
        Name = UniqueName(existingNames ?? new[] { source.Name }, $"{source.Name} (Kopie)"),
        Instructions = source.Instructions,
        Template = source.Template,
        QuickActions = new List<string>(source.QuickActions),
    };
}
