using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SettingsTests
{
    private sealed class Reverse : ISecretProtector
    {
        public string Protect(string plain) => new(plain.Reverse().ToArray());
        public string? Unprotect(string stored) => new(stored.Reverse().ToArray());
    }

    [Fact]
    public void RoundTrip_KeyIsNotPlainOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new Reverse());
        Assert.Equal("Ctrl+Shift+D", s.DictateHotkey);
        Assert.Equal("Ctrl+Shift+P", s.PromptHotkey);
        Assert.Equal("Ctrl+Shift+G", s.GlossaryHotkey);
        Assert.Equal("gemini-3.1-flash-lite", s.TranscriptionModel);
        Assert.Equal("gemini-3.8-flash", s.PromptModel);

        s.ApiKey = "abc123SECRET";
        s.Glossary = new() { "Kubernetes", "Müller" };
        s.PromptSystemPrompt = "Mein Prompt";
        s.DictateHotkey = "Ctrl+Shift+X";
        s.GlossaryHotkey = "Ctrl+Alt+G";
        s.Save();

        Assert.DoesNotContain("abc123SECRET", File.ReadAllText(path));
        // '+' is written as \u002B, so the raw hotkey text is not in the file. The name is.
        Assert.Contains("\"glossaryHotkey\":", File.ReadAllText(path));
        var t = Settings.Load(path, new Reverse());
        Assert.Equal("abc123SECRET", t.ApiKey);
        Assert.Equal(new[] { "Kubernetes", "Müller" }, t.Glossary);
        Assert.Equal("Mein Prompt", t.PromptSystemPrompt);
        Assert.Equal("Ctrl+Shift+X", t.DictateHotkey);
        Assert.Equal("Ctrl+Alt+G", t.GlossaryHotkey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void OfflineSettings_DefaultsAndRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var old = Settings.Load(path, new PlainSecretProtector());
        Assert.False(old.OfflineMode);
        Assert.Equal("", old.OfflineLlmId);
        Assert.Equal("Ctrl+Shift+G", old.GlossaryHotkey);

        old.OfflineMode = true;
        old.OfflineLlmId = "qwen3-4b-instruct-2507";
        old.Save();
        Assert.Contains("\"offlineMode\": true", File.ReadAllText(path));
        Assert.Contains("\"offlineLlmId\": \"qwen3-4b-instruct-2507\"", File.ReadAllText(path));
        Assert.Contains("\"glossaryHotkey\":", File.ReadAllText(path));
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.True(t.OfflineMode);
        Assert.Equal("qwen3-4b-instruct-2507", t.OfflineLlmId);
        Assert.Equal("Ctrl+Shift+G", t.GlossaryHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptFile_GivesDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ nope");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.ApiKey);
        Assert.True(File.Exists(path + ".bak"));
        Directory.Delete(dir, true);
    }

    private static string Tmp() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void RoundTrip_CustomPrompts()
    {
        var path = Tmp();
        var s = Settings.Load(path, new Reverse());
        s.DictateSystemPrompt = "Schreib alles klein.";
        s.PromptModeSystemPrompt = "Formuliere knapp.";
        s.Save();
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("Schreib alles klein.", l.DictateSystemPrompt);
        Assert.Equal("Formuliere knapp.", l.PromptModeSystemPrompt);
    }

    [Fact]
    public void OldFile_WithoutPromptFields_LoadsEmpty()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"promptSystemPrompt\":\"Vorlage\"}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("", l.DictateSystemPrompt);
        Assert.Equal("", l.PromptModeSystemPrompt);
        Assert.Equal("Vorlage", l.PromptSystemPrompt);
    }

    [Fact]
    public void Load_NormalizesModels()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"promptModel\":\"gemini-3.7-flash\",\"transcriptionModel\":\"quatsch\"}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("gemini-3.8-flash", l.PromptModel);
        Assert.Equal(Settings.DefaultTranscriptionModel, l.TranscriptionModel);
    }

    [Fact]
    public void CustomOrEmpty_Cases()
    {
        var def = "Zeile eins\nZeile zwei";
        Assert.Equal("", Settings.CustomOrEmpty("Zeile eins\r\nZeile zwei\r\n", def));
        Assert.Equal("", Settings.CustomOrEmpty("  \r\n ", def));
        Assert.Equal("", Settings.CustomOrEmpty(null, def));
        Assert.Equal("a\nb", Settings.CustomOrEmpty("  a\r\nb \r\n", def));
    }
}

public class LogAndAudioTests
{
    [Fact]
    public async Task FileAudioSource_ReturnsFileBytes_AndWavDuration()
    {
        var path = Paths.TestAudio("diktat.wav");
        if (!File.Exists(path)) return;
        var src = new FileAudioSource(path);
        src.Start();
        var wav = await src.StopAsync();
        Assert.Equal(File.ReadAllBytes(path), wav);
        Assert.InRange(Wav.DurationSeconds(wav), 0.5, 120);
    }

    [Fact]
    public void Log_WritesLines_AndIsNoopBeforeInit()
    {
        Log.Init(null);
        Log.Info("ignored");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "wsw.log");
        Log.Init(path);
        Log.Info("hello");
        Log.Error("bad", new InvalidOperationException("boom"));
        Log.Init(null);
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("INFO hello", lines[0]);
        Assert.Contains("ERROR bad (InvalidOperationException: boom)", lines[1]);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class QuickActionTests
{
    private static string TempFile(string? json)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        if (json != null) File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void OldFile_GetsDefaults()
    {
        var path = TempFile("{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(QuickActionList.Defaults, s.QuickActions);
        Assert.Equal(5, s.QuickActions.Count);
    }

    private static readonly string[] OldDefaults =
    {
        "Als Anamnese, Befund und Therapie gliedern",
        "In Stichpunkten zusammenfassen",
        "Rechtschreibung und Grammatik korrigieren",
        "Kürzer fassen",
        "Freundlicher formulieren",
    };

    [Fact]
    public void OldMedicalDefaults_MigrateToNewDefaults()
    {
        var path = TempFile(System.Text.Json.JsonSerializer.Serialize(new { quickActions = OldDefaults }));
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(QuickActionList.Defaults, s.QuickActions);
        Assert.DoesNotContain("Als Anamnese, Befund und Therapie gliedern", s.QuickActions);
    }

    [Fact]
    public void CustomisedOldDefaults_AreKept()
    {
        var custom = (string[])OldDefaults.Clone();
        custom[4] = "Förmlicher formulieren";
        var path = TempFile(System.Text.Json.JsonSerializer.Serialize(new { quickActions = custom }));
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(custom, s.QuickActions);
    }

    [Fact]
    public void RoundTrip_KeepsCustomList()
    {
        var path = TempFile(null);
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(QuickActionList.Defaults, s.QuickActions);
        s.QuickActions = new() { "Eins", "Zwei" };
        s.Save();
        Assert.Contains("\"quickActions\":", File.ReadAllText(path));
        Assert.Equal(new[] { "Eins", "Zwei" }, Settings.Load(path, new PlainSecretProtector()).QuickActions);
    }

    [Fact]
    public void ExplicitEmptyList_StaysEmpty()
    {
        var path = TempFile(null);
        var s = Settings.Load(path, new PlainSecretProtector());
        s.QuickActions = new();
        s.Save();
        Assert.Empty(Settings.Load(path, new PlainSecretProtector()).QuickActions);
    }

    [Fact]
    public void Clean_TrimsCollapsesSkipsDedupesAndCaps()
    {
        var cleaned = QuickActionList.Clean(new string?[]
        {
            "  Kürzer   fassen ", "", "   ", null, "kürzer fassen", new string('x', 81), new string('y', 80),
            "A", "B", "C", "D",
        });
        Assert.Equal(new[] { "Kürzer fassen", new string('y', 80), "A", "B", "C" }, cleaned);
    }
}

public class QuickActionRequestTests
{
    [Fact]
    public void SelectionAndBlankTranscript_UsesActionAlone() =>
        Assert.Equal(("Text", "Kürzer fassen"), QuickActionRequest.Build("Kürzer fassen", "Text", "  ")!.Value);

    [Fact]
    public void SelectionAndTranscript_AppendsWhatWasSaid() =>
        Assert.Equal(("Text", "Kürzer fassen\n\nZusätzlich gesagt: bitte knapp"),
            QuickActionRequest.Build("Kürzer fassen", "Text", " bitte knapp ")!.Value);

    [Fact]
    public void NoSelection_DictationBecomesTheText() =>
        Assert.Equal(("Patient klagt", "Kürzer fassen"),
            QuickActionRequest.Build("Kürzer fassen", null, " Patient klagt ")!.Value);

    [Fact]
    public void NothingToApplyTo_IsNull()
    {
        Assert.Null(QuickActionRequest.Build("Kürzer fassen", "", " "));
        Assert.Null(QuickActionRequest.Build("Kürzer fassen", null, ""));
    }

    [Fact]
    public void Microphone_DefaultsEmptyAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.Microphone);

        s.Microphone = "Headset (USB Audio)";
        s.Save();
        Assert.Contains("\"microphone\": \"Headset (USB Audio)\"", File.ReadAllText(path));
        Assert.Equal("Headset (USB Audio)", Settings.Load(path, new PlainSecretProtector()).Microphone);
        Directory.Delete(dir, true);
    }
}
