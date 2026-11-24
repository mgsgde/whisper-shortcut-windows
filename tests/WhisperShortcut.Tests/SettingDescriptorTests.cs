using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;
using WhisperShortcut.Tests.Mcp;

namespace WhisperShortcut.Tests;

/// <summary>The descriptor table (W11): one row per setting; persistence, defaults, bindings, AI-app catalog and report derive from it.</summary>
public class SettingDescriptorTests
{
    private static Settings Fresh(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "wsw-desc-" + Guid.NewGuid().ToString("N"));
        return Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
    }

    [Fact]
    public void EveryDescriptorHasAUniqueJsonKey()
    {
        var keys = Settings.Descriptors.Select(d => d.JsonKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.NotEmpty(keys);
    }

    [Fact]
    public void EveryPersistedDtoFieldHasExactlyOneDescriptor()
    {
        var dto = typeof(Settings).GetNestedType("Dto", System.Reflection.BindingFlags.NonPublic)!;
        var persisted = dto.GetProperties().Select(p => p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
            .Cast<JsonPropertyNameAttribute>().Single().Name).Order(StringComparer.Ordinal).ToList();

        var owned = Settings.Descriptors.SelectMany(d => d.JsonKeys).ToList();
        Assert.Equal(owned.Count, owned.Distinct().Count()); // no field has two owners
        Assert.Equal(persisted, owned.Order(StringComparer.Ordinal).ToList()); // no field has none, no descriptor owns a field that is not there
        Assert.Empty(Settings.UnownedJsonKeys);
    }

    [Fact]
    public void FreshSettingsEqualTheDescriptorDefaults()
    {
        var s = Fresh(out _);
        Assert.Null(Settings.NewInstallMismatch(s));
        foreach (var d in Settings.Descriptors.Where(d => d.Type != SettingType.Internal))
            Assert.True(JsonSerializer.Serialize(d.Read(s)) == JsonSerializer.Serialize(d.Default), $"{d.JsonKey}: a fresh Settings differs from the descriptor default");
    }

    [Fact]
    public void ListDefaultsAreNotSharedBetweenSettings()
    {
        var a = Fresh(out _);
        var b = Fresh(out _);
        a.QuickActions.Add("x");
        a.Glossary.Add("y");
        a.Templates.Add(new DictationTemplate());
        a.MoveOfferDismissed.Add("z");
        Assert.DoesNotContain("x", b.QuickActions);
        Assert.Empty(b.Glossary);
        Assert.Empty(b.Templates);
        Assert.Empty(b.MoveOfferDismissed);
        Assert.DoesNotContain("x", (List<string>)Settings.Descriptor("quickActions").Default!);
    }

    [Fact]
    public void SecretsCarryNoReportTitleAndNoMcpId()
    {
        var secrets = Settings.Descriptors.Where(d => d.Secret).ToList();
        Assert.Equal(new[] { "apiKeyProtected", "openAiKeyProtected" }, secrets.Select(d => d.JsonKey).Order(StringComparer.Ordinal).ToArray());
        foreach (var d in secrets)
        {
            Assert.Null(d.ReportTitle);
            Assert.Empty(d.McpIds);
            Assert.Null(d.Show(Fresh(out _)));
        }
        Assert.Throws<ArgumentException>(() => new SettingDescriptor("k", new[] { "k" }, SettingType.Secret, "", Array.Empty<string>(), "Titel",
            ControlKind.Text, SettingsPage.Privacy, null, null, null));
    }

    [Fact]
    public void ReportValuesNeverContainAnApiKey()
    {
        var s = Fresh(out _);
        s.ApiKey = "AIzaSyDESCRIPTOR_key_zzz";
        s.OpenAiKey = "sk-proj-descriptor-key-zzz";
        foreach (var d in Settings.Descriptors)
        {
            var shown = d.Show(s) ?? "";
            Assert.DoesNotContain("descriptor", shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("zzz", shown);
        }
    }

    [Fact]
    public void EveryDescriptorWithAReportTitleShowsAValue()
    {
        var s = Fresh(out _);
        foreach (var d in Settings.Descriptors.Where(d => d.ReportTitle != null))
            Assert.False(string.IsNullOrWhiteSpace(d.Show(s)), d.JsonKey);
    }

    private static readonly ConfigurationReportEnvironment Env = new("9.99", "10.0.26100", "x64");

    /// <summary>The settings AI apps may change: exactly today's set, no more (a new writable setting is a deliberate decision).</summary>
    [Fact]
    public void CatalogIsExactlyTheSetOfAiWritableSettings()
    {
        using var stack = new McpStack();
        var expected = new[]
        {
        "dictation.engine",
        "dictate_prompt.engine",
        "dictation.transcription_model",
        "dictation.openai_transcription_model",
        "dictation.punctuation",
        "dictation.mode",
        "dictation.language",
        "dictation.temperature",
        "dictation.thinking",
        "dictate_prompt.model",
        "dictate_prompt.openai_model",
        "offline.llm",
        "shortcuts.dictate",
        "shortcuts.dictate_prompt",
        "shortcuts.read_aloud",
        "shortcuts.add_to_glossary",
        "read_aloud.model",
        "read_aloud.voice.gemini",
        "read_aloud.voice.openai",
        "read_aloud.speed",
        "read_aloud.smart_rewrite",
        "clipboard.auto_paste",
        "clipboard.after_paste",
        "clipboard.restore_after_paste",
        "general.sounds",
        "general.auto_update_check",
        "general.launch_at_login",
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), stack.Catalog.Select(c => c.Id).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryCatalogIdIsBackedByADescriptor_AndEveryDescriptorMcpIdIsInTheCatalog()
    {
        using var stack = new McpStack();
        var owned = Settings.Descriptors.SelectMany(d => d.McpIds).ToList();
        Assert.Equal(owned.Count, owned.Distinct().Count());
        var catalogIds = stack.Catalog.Select(c => c.Id).ToHashSet();
        // The one catalog entry that is no settings.json field: the Windows autostart (registry), always a proposal.
        Assert.Equal(new[] { "general.launch_at_login" }, catalogIds.Except(owned).ToArray());
        Assert.Empty(owned.Except(catalogIds));
    }

    [Fact]
    public void CatalogAndReportNeverContainAnApiKey()
    {
        using var stack = new McpStack();
        stack.Settings.ApiKey = "AIzaSyCATALOG_key_qqq";
        stack.Settings.OpenAiKey = "sk-proj-catalog-key-qqq";
        var text = string.Join("\n", stack.Catalog.SelectMany(c => new[] { c.Id, c.Title, c.Description ?? "", c.Label(c.Read()) }));
        Assert.DoesNotContain("qqq", text);
        Assert.DoesNotContain(stack.Catalog, c => c.Id.Contains("key", StringComparison.OrdinalIgnoreCase));
        foreach (var catalog in new[] { stack.Catalog, Array.Empty<McpSetting>() })
        {
            var report = ConfigurationReport.Build(new ConfigurationReportOptions(true, true, true), stack.Settings, catalog, Env);
            Assert.DoesNotContain("qqq", report);
            Assert.DoesNotContain("AIzaSyCATALOG", report);
        }
    }

    [Fact]
    public void ReportListsEveryDescriptorWithAReportTitle()
    {
        using var stack = new McpStack();
        // Without a catalog (the tray builds the report before the MCP server runs) every titled descriptor is a line of its own.
        var alone = ConfigurationReport.Build(new ConfigurationReportOptions(), stack.Settings, Array.Empty<McpSetting>(), Env);
        foreach (var d in Settings.Descriptors.Where(d => d.ReportTitle != null))
            Assert.Contains($"\n{d.ReportTitle}: {d.Show(stack.Settings)}", alone);
        // With the catalog, the settings it lists keep its lines and the rest follows under "other".
        var withCatalog = ConfigurationReport.Build(new ConfigurationReportOptions(), stack.Settings, stack.Catalog, Env);
        foreach (var d in Settings.Descriptors.Where(d => d.ReportTitle != null && d.McpIds.Count == 0))
            Assert.Contains($"\n{d.ReportTitle}: {d.Show(stack.Settings)}", withCatalog);
        foreach (var c in stack.Catalog) Assert.Contains($"\n{c.Title}: ", withCatalog);
    }

    [Fact]
    public void ReportGainsTheSettingsTheCatalogNeverExposed()
    {
        using var stack = new McpStack();
        var report = ConfigurationReport.Build(new ConfigurationReportOptions(), stack.Settings, stack.Catalog, Env);
        foreach (var title in new[] { "Meetings: Spracherkennung (Anbieter und Modelle)", "Meetings: Zusammenfassung (Anbieter und Modelle)",
            "Chat: Anbieter und Modelle", "Bildschirmfoto mitschicken", "Chat: Websuche", "Tastenkürzel Chat", "Tastenkürzel Bildschirmfoto",
            "Update-Kanal", "Mikrofon" })
            Assert.Contains($"\n{title}: ", report);
    }
}
