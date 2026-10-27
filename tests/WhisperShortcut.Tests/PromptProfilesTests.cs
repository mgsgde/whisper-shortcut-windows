using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class PromptProfilesTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static Settings Load(string path) => Settings.Load(path, new PlainSecretProtector());

    [Fact]
    public void OldFile_MigratesToOneStandardProfile()
    {
        var path = TempPath();
        File.WriteAllText(path, "{\"promptModeSystemPrompt\":\"Mein Stil\",\"promptSystemPrompt\":\"Vorlage A\",\"quickActions\":[\"Kürzer\",\"Länger\"]}");
        var s = Load(path);
        var p = Assert.Single(s.Profiles);
        Assert.Equal("Standard", p.Name);
        Assert.Equal("Mein Stil", p.Instructions);
        Assert.Equal("Vorlage A", p.Template);
        Assert.Equal(new[] { "Kürzer", "Länger" }, p.QuickActions);
        Assert.Equal(p.Id, s.ActiveProfileId);
        Assert.Equal("Mein Stil", s.PromptModeSystemPrompt);
    }

    [Fact]
    public void Save_MirrorsActiveProfileIntoOldFields()
    {
        var path = TempPath();
        var s = Load(path);
        s.PromptSystemPrompt = "T";
        s.Save();
        Assert.Contains("\"promptSystemPrompt\": \"T\"", File.ReadAllText(path));
        Assert.Contains("\"profiles\":", File.ReadAllText(path));
    }

    [Fact]
    public void SeveralProfilesAndActiveId_RoundTrip()
    {
        var path = TempPath();
        var s = Load(path);
        s.Profiles.Add(new PromptProfile { Id = "b", Name = "Werkstatt", Instructions = "Kurz", Template = "Vorlage W", QuickActions = new() { "Eins" } });
        s.ActiveProfileId = "b";
        s.Save();
        var t = Load(path);
        Assert.Equal(2, t.Profiles.Count);
        Assert.Equal("b", t.ActiveProfileId);
        Assert.Equal("Werkstatt", t.ActiveProfile.Name);
        Assert.Equal("Kurz", t.PromptModeSystemPrompt);
        Assert.Equal("Vorlage W", t.PromptSystemPrompt);
        Assert.Equal(new[] { "Eins" }, t.QuickActions);
        // The old fields follow the active profile.
        Assert.Contains("\"promptSystemPrompt\": \"Vorlage W\"", File.ReadAllText(path));
    }

    [Fact]
    public void Accessors_WriteIntoActiveProfileOnly()
    {
        var s = Load(TempPath());
        s.Profiles.Add(new PromptProfile { Id = "b", Name = "Zwei" });
        s.ActiveProfileId = "b";
        s.PromptModeSystemPrompt = "X";
        s.PromptSystemPrompt = "Y";
        s.QuickActions = new() { "Q" };
        Assert.Equal("X", s.Profiles[1].Instructions);
        Assert.Equal("Y", s.Profiles[1].Template);
        Assert.Equal(new[] { "Q" }, s.Profiles[1].QuickActions);
        Assert.Equal("", s.Profiles[0].Instructions);
        Assert.Equal("", s.Profiles[0].Template);
    }

    [Fact]
    public void UnknownActiveId_FallsBackToFirst()
    {
        var path = TempPath();
        File.WriteAllText(path, "{\"profiles\":[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"b\",\"name\":\"B\"}],\"activeProfileId\":\"zzz\"}");
        var s = Load(path);
        Assert.Equal("a", s.ActiveProfileId);
        Assert.Equal("A", s.ActiveProfile.Name);

        s.ActiveProfileId = "gone";
        Assert.Equal("A", s.ActiveProfile.Name);
    }

    [Fact]
    public void EmptyProfileList_BecomesStandard()
    {
        var path = TempPath();
        File.WriteAllText(path, "{\"profiles\":[],\"activeProfileId\":\"x\"}");
        var s = Load(path);
        var p = Assert.Single(s.Profiles);
        Assert.Equal("Standard", p.Name);
        Assert.Equal(p.Id, s.ActiveProfile.Id);
        Assert.False(string.IsNullOrEmpty(p.Id));
    }

    [Fact]
    public void Load_NormalizesPerProfile()
    {
        var path = TempPath();
        File.WriteAllText(path, "{\"profiles\":[{\"id\":\"a\",\"name\":\"A\",\"instructions\":\"  \"},{\"id\":\"a\",\"name\":\"a\",\"quickActions\":[\" x   y \",\"X Y\",\"\"]}]}");
        var s = Load(path);
        Assert.Equal(2, s.Profiles.Count);
        Assert.NotEqual(s.Profiles[0].Id, s.Profiles[1].Id);
        Assert.Equal("a 2", s.Profiles[1].Name);
        Assert.Equal("", s.Profiles[0].Instructions);
        Assert.Equal(QuickActionList.Defaults, s.Profiles[0].QuickActions);
        Assert.Equal(new[] { "x y" }, s.Profiles[1].QuickActions);
    }

    [Fact]
    public void UniqueName_AppendsNumber()
    {
        Assert.Equal("Praxis", PromptProfiles.UniqueName(new[] { "Werkstatt" }, "Praxis"));
        Assert.Equal("Praxis 2", PromptProfiles.UniqueName(new[] { "Praxis" }, "Praxis"));
        Assert.Equal("Praxis 3", PromptProfiles.UniqueName(new[] { "Praxis", "praxis 2" }, "Praxis"));
        Assert.Equal("Profil", PromptProfiles.UniqueName(Array.Empty<string>(), " "));
    }

    [Fact]
    public void Duplicate_IsDeepCopyWithNewIdAndKopieName()
    {
        var src = new PromptProfile { Id = "a", Name = "Praxis", Instructions = "I", Template = "T", QuickActions = new() { "Q" } };
        var copy = PromptProfiles.Duplicate(src);
        Assert.NotEqual("a", copy.Id);
        Assert.Equal("Praxis (Kopie)", copy.Name);
        Assert.Equal("I", copy.Instructions);
        Assert.Equal("T", copy.Template);
        copy.QuickActions.Add("Neu");
        Assert.Single(src.QuickActions);
        Assert.Equal("Praxis (Kopie) 2", PromptProfiles.Duplicate(src, new[] { "Praxis", "Praxis (Kopie)" }).Name);
    }
}
