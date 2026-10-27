using WhisperShortcut.Core;

namespace WhisperShortcut;

/// <summary>Ready-made starting points when the user adds a prompt profile.</summary>
internal static class ProfileSamples
{
    /// <summary>Name and template of each built-in sample, in menu order.</summary>
    public static IReadOnlyList<(string Name, string Template)> All { get; } = new (string, string)[]
    {
        ("Praxis", QuoteTemplate.Praxis),
        ("Werkstatt", QuoteTemplate.Werkstatt),
        ("Handwerk", QuoteTemplate.Default),
    };

    /// <summary>A new profile with default instructions and quick actions; the name is made unique against <paramref name="existingNames"/>.</summary>
    public static PromptProfile Create(string name, string template, IEnumerable<string> existingNames) => new()
    {
        Id = PromptProfiles.NewId(),
        Name = PromptProfiles.UniqueName(existingNames, name),
        Instructions = "",
        Template = template,
        QuickActions = new List<string>(QuickActionList.Defaults),
    };
}
