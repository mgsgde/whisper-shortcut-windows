using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>The fingerprint decides whether the settings window built in advance may be shown (TrayApp.TakeSpareSettings).</summary>
public class SettingsFingerprintTests
{
    private static Settings Fresh() =>
        Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"), new PlainSecretProtector());

    [Fact]
    public void SameSettings_SameFingerprint()
    {
        var s = Fresh();
        Assert.Equal(s.Fingerprint(), s.Fingerprint());
    }

    [Fact]
    public void AnyChange_ChangesTheFingerprint()
    {
        var s = Fresh();
        var before = s.Fingerprint();
        s.Sounds = !s.Sounds;
        Assert.NotEqual(before, s.Fingerprint());
        s.Sounds = !s.Sounds;
        Assert.Equal(before, s.Fingerprint());
        s.ApiKey = "AIza-test-key";
        Assert.NotEqual(before, s.Fingerprint());
    }

    [Fact]
    public void Fingerprint_NeverContainsTheKey()
    {
        var s = Fresh();
        s.ApiKey = "AIza-secret-123";
        s.OpenAiKey = "sk-secret-456";
        var print = s.Fingerprint();
        Assert.DoesNotContain("AIza-secret-123", print);
        Assert.DoesNotContain("sk-secret-456", print);
    }
}
