using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperShortcut.Core;

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
}
