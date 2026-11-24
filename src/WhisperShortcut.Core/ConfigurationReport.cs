using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Core;

/// <summary>What the report may contain beyond the base snapshot.</summary>
/// <param name="IncludeGlossary">Append the glossary terms.</param>
/// <param name="IncludePrompts">Append the system prompt texts (and the template when set).</param>
/// <param name="IncludeTemplates">Append the dictation templates with their instructions.</param>
public sealed record ConfigurationReportOptions(bool IncludeGlossary = false, bool IncludePrompts = false, bool IncludeTemplates = false);

/// <summary>Where the app runs; shown in the report header.</summary>
/// <param name="AppVersion">App version, e.g. "1.2.3".</param>
/// <param name="WindowsVersion">Windows version, e.g. "10.0.26100".</param>
/// <param name="Architecture">CPU architecture, e.g. "x64".</param>
public sealed record ConfigurationReportEnvironment(string AppVersion, string WindowsVersion, string Architecture);

/// <summary>
/// A paste-able plain-text snapshot of the configuration that a tester sends to the developer
/// (port of the Mac's ConfigurationReport). API keys are never read; free text appears only when asked for.
/// </summary>
public static class ConfigurationReport
{
    /// <summary>Builds the report text; lines are joined with "\n".</summary>
    public static string Build(ConfigurationReportOptions options, Settings settings,
        IReadOnlyList<McpSetting> catalog, ConfigurationReportEnvironment env)
    {
        var lines = new List<string>
        {
            "WhisperShortcut configuration",
            $"App {env.AppVersion} ({env.Architecture}) · Windows {env.WindowsVersion}",
            $"Offline mode: {(settings.OfflineMode ? "On" : "Off")}",
            "",
            "## Settings",
        };

        foreach (var group in catalog.GroupBy(s => s.Category))
        {
            lines.Add("");
            lines.Add($"### {group.Key}");
            foreach (var s in group) lines.Add($"{s.Title}: {s.Label(s.Read())}");
        }

        // Every other setting that has a report title in the descriptor table (meeting and chat models, screenshots, web search,
        // the shortcuts AI apps cannot change ...): the report is not limited to what AI apps can change. A setting the catalog
        // already lists is not repeated. Secrets have no title, so they cannot be listed.
        var catalogIds = catalog.Select(c => c.Id).ToHashSet();
        var others = Settings.Descriptors
            .Where(d => d.ReportTitle != null && !d.Secret && !d.McpIds.Any(catalogIds.Contains))
            .ToList();
        if (others.Count > 0)
        {
            lines.Add("");
            lines.Add("### other");
            foreach (var d in others) lines.Add($"{d.ReportTitle}: {d.Show(settings)}");
        }

        var dictation = Effective(settings.DictateSystemPrompt, Prompts.DefaultTranscriptionSystemPrompt, out var dictationCustom);
        var dictatePrompt = Effective(settings.PromptModeSystemPrompt, Prompts.DefaultPromptModeSystemPrompt, out var promptCustom);
        var template = settings.DictatePromptTemplate.Trim();

        lines.Add("");
        lines.Add("## System prompts");
        lines.Add($"dictation: {(dictationCustom ? "customized" : "default")}, {dictation.Length} chars");
        lines.Add($"dictate_prompt: {(promptCustom ? "customized" : "default")}, {dictatePrompt.Length} chars");
        lines.Add(template.Length > 0 ? $"template: customized, {template.Length} chars" : "template: none");
        lines.Add($"glossary: {settings.Glossary.Count} terms, {settings.Glossary.Sum(l => l.Length)} chars");
        lines.Add($"quick_instructions: {settings.QuickActions.Count}");
        lines.Add($"dictation_templates: {settings.Templates.Count}");

        if (options.IncludePrompts)
        {
            lines.Add("");
            lines.Add("## System prompt texts");
            AddBlock(lines, "dictation", dictation);
            AddBlock(lines, "dictate_prompt", dictatePrompt);
            if (template.Length > 0) AddBlock(lines, "template", template);
        }

        if (options.IncludeGlossary)
        {
            lines.Add("");
            lines.Add("## Glossary");
            lines.Add("```");
            lines.Add(SecretRedactor.Redact(string.Join("\n", settings.Glossary)));
            lines.Add("```");
        }

        if (options.IncludeTemplates)
        {
            lines.Add("");
            lines.Add("## Templates");
            if (settings.Templates.Count == 0) lines.Add("none");
            foreach (var t in settings.Templates)
            {
                lines.Add("");
                lines.Add($"### {t.DisplayName}  (shortcut: {(string.IsNullOrWhiteSpace(t.Hotkey) ? "none" : t.Hotkey)})");
                lines.Add("```");
                lines.Add(SecretRedactor.Redact(t.Instructions));
                lines.Add("```");
            }
        }

        return string.Join("\n", lines);
    }

    private static string Effective(string stored, string fallback, out bool customized)
    {
        customized = !string.IsNullOrWhiteSpace(stored);
        return customized ? stored : fallback;
    }

    private static void AddBlock(List<string> lines, string name, string text)
    {
        lines.Add("");
        lines.Add($"### {name}");
        lines.Add("```");
        lines.Add(SecretRedactor.Redact(text));
        lines.Add("```");
    }
}
