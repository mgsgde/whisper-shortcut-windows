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
        Assert.Equal("gemini-3.1-flash-lite", s.TranscriptionModel);
        Assert.Equal("gemini-3.8-flash", s.PromptModel);

        s.ApiKey = "abc123SECRET";
        s.Glossary = new() { "Kubernetes", "Müller" };
        s.PromptSystemPrompt = "Mein Prompt";
        s.DictateHotkey = "Ctrl+Shift+X";
        s.Save();

        Assert.DoesNotContain("abc123SECRET", File.ReadAllText(path));
        var t = Settings.Load(path, new Reverse());
        Assert.Equal("abc123SECRET", t.ApiKey);
        Assert.Equal(new[] { "Kubernetes", "Müller" }, t.Glossary);
        Assert.Equal("Mein Prompt", t.PromptSystemPrompt);
        Assert.Equal("Ctrl+Shift+X", t.DictateHotkey);
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

        old.OfflineMode = true;
        old.OfflineLlmId = "qwen3-4b-instruct-2507";
        old.Save();
        Assert.Contains("\"offlineMode\": true", File.ReadAllText(path));
        Assert.Contains("\"offlineLlmId\": \"qwen3-4b-instruct-2507\"", File.ReadAllText(path));
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.True(t.OfflineMode);
        Assert.Equal("qwen3-4b-instruct-2507", t.OfflineLlmId);
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
