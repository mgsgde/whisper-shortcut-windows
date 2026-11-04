using System.Text.Json.Nodes;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Mac-digit defaults for new installs, and the one-time pin that keeps an existing install's shortcuts.</summary>
public class ShortcutDefaultsTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Settings LoadJson(string dir, string json)
    {
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return Settings.Load(path, new PlainSecretProtector());
    }

    [Fact]
    public void FreshInstall_GetsMacDigitDefaults()
    {
        var dir = NewDir();
        var s = Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
        Assert.Equal("Alt+1", s.ActiveDictateHotkey);
        Assert.Equal("Alt+2", s.ActivePromptHotkey);
        Assert.Equal("Alt+3", s.ActiveScreenshotHotkey);
        Assert.Equal("Alt+0", s.ActiveOpenSettingsHotkey);
        // Live diktieren and Glossar: suggested, switched off (Mac isEnabled: false).
        Assert.Equal("Alt+5", s.LiveDictateHotkey);
        Assert.False(s.LiveDictateHotkeyEnabled);
        Assert.Equal("", s.ActiveLiveDictateHotkey);
        Assert.Equal("Alt+7", s.GlossaryHotkey);
        Assert.False(s.GlossaryHotkeyEnabled);
        Assert.Equal("", s.ActiveGlossaryHotkey);
        Assert.True(s.RightCtrlDictation);
        // Alt+4 is Read Aloud (Mac ⌃⌥4), on, and no other function uses it.
        Assert.Equal("Alt+4", s.ActiveReadAloudHotkey);
        Assert.Equal(Settings.DefaultReadAloudHotkey, s.ReadAloudHotkey);
        Assert.DoesNotContain("Alt+4", new[] { s.DictateHotkey, s.PromptHotkey, s.ScreenshotHotkey, s.OpenSettingsHotkey,
            s.LiveDictateHotkey, s.GlossaryHotkey });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void FreshInstall_SavedAndReloaded_KeepsNewDefaults()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        Settings.Load(path, new PlainSecretProtector()).Save();
        Assert.True(JsonNode.Parse(File.ReadAllText(path))!["shortcutDefaultsAltV1"]!.GetValue<bool>());
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Alt+1", t.ActiveDictateHotkey);
        Assert.Equal("Alt+3", t.ActiveScreenshotHotkey);
        Assert.Equal("Alt+0", t.ActiveOpenSettingsHotkey);
        Assert.Equal("Alt+7", t.GlossaryHotkey);
        Assert.False(t.GlossaryHotkeyEnabled);
        Assert.Equal("Alt+5", t.LiveDictateHotkey);
        Assert.False(t.LiveDictateHotkeyEnabled);
        Directory.Delete(dir, true);
    }

    [Theory]
    // Older files: hotkeys missing (never saved), or written with the old defaults, or the pre-0.5 blank form.
    [InlineData("{}")]
    [InlineData("{\"apiKeyProtected\":\"\"}")]
    [InlineData("{\"dictateHotkey\":\"Ctrl+Shift+D\",\"promptHotkey\":\"Ctrl+Shift+P\",\"glossaryHotkey\":\"Ctrl+Shift+K\",\"screenshotHotkey\":\"\",\"liveDictateHotkey\":\"\"}")]
    [InlineData("{\"dictateHotkey\":\"\",\"promptHotkey\":\" \",\"glossaryHotkey\":null}")]
    public void ExistingInstall_IsPinnedToTheOldShortcuts(string json)
    {
        var dir = NewDir();
        var s = LoadJson(dir, json);
        Assert.Equal("Ctrl+Shift+D", s.ActiveDictateHotkey);
        Assert.Equal("Ctrl+Shift+P", s.ActivePromptHotkey);
        Assert.Equal("Ctrl+Shift+K", s.ActiveGlossaryHotkey);
        Assert.Equal("", s.ActiveScreenshotHotkey);
        Assert.Equal("", s.ActiveLiveDictateHotkey);
        Assert.Equal("", s.ActiveOpenSettingsHotkey);

        // After the next save the file carries the pin and the explicit old values: a reload keeps them.
        s.Save();
        var t = Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+D", t.ActiveDictateHotkey);
        Assert.Equal("Ctrl+Shift+P", t.ActivePromptHotkey);
        Assert.Equal("Ctrl+Shift+K", t.ActiveGlossaryHotkey);
        Assert.Equal("", t.ActiveScreenshotHotkey);
        Assert.Equal("", t.ActiveLiveDictateHotkey);
        Assert.Equal("", t.ActiveOpenSettingsHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ExistingInstall_CustomKeysAreUntouched()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"dictateHotkey\":\"Ctrl+Shift+X\",\"promptHotkey\":\"Alt+Shift+Y\",\"glossaryHotkey\":\"Ctrl+Shift+G\","
            + "\"screenshotHotkey\":\"Ctrl+Shift+S\",\"liveDictateHotkey\":\"Ctrl+Shift+L\",\"meetingHotkey\":\"Ctrl+Shift+M\"}");
        Assert.Equal("Ctrl+Shift+X", s.ActiveDictateHotkey);
        Assert.Equal("Alt+Shift+Y", s.ActivePromptHotkey);
        Assert.Equal("Ctrl+Shift+G", s.ActiveGlossaryHotkey);
        Assert.Equal("Ctrl+Shift+S", s.ActiveScreenshotHotkey);
        Assert.Equal("Ctrl+Shift+L", s.ActiveLiveDictateHotkey);
        Assert.Equal("Ctrl+Shift+M", s.MeetingHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_MissingFields_GetTheNewDefaults()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"promptHotkey\":\"Ctrl+Shift+P\"}");
        Assert.Equal("Alt+1", s.ActiveDictateHotkey);
        Assert.Equal("Ctrl+Shift+P", s.ActivePromptHotkey);
        Assert.Equal("Alt+3", s.ActiveScreenshotHotkey);
        Assert.Equal("Alt+0", s.ActiveOpenSettingsHotkey);
        Assert.Equal("", s.ActiveGlossaryHotkey);
        Assert.Equal("", s.ActiveLiveDictateHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_EmptyStringStaysUnassigned()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"screenshotHotkey\":\"\",\"openSettingsHotkey\":\"\"}");
        Assert.Equal("", s.ActiveScreenshotHotkey);
        Assert.Equal("", s.ActiveOpenSettingsHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void EnabledFlags_And_RightCtrl_RoundTrip()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.GlossaryHotkeyEnabled = true;
        s.LiveDictateHotkeyEnabled = true;
        s.ScreenshotHotkeyEnabled = false;
        s.OpenSettingsHotkeyEnabled = false;
        s.DictateHotkeyEnabled = false;
        s.RightCtrlDictation = false;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Alt+7", t.ActiveGlossaryHotkey);
        Assert.Equal("Alt+5", t.ActiveLiveDictateHotkey);
        Assert.Equal("", t.ActiveScreenshotHotkey);
        Assert.Equal("Alt+3", t.ScreenshotHotkey);
        Assert.Equal("", t.ActiveOpenSettingsHotkey);
        Assert.Equal("", t.ActiveDictateHotkey);
        Assert.True(t.PromptHotkeyEnabled);
        Assert.False(t.RightCtrlDictation);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptFile_CountsAsExistingInstall()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{ nope");
        Assert.Equal("Ctrl+Shift+D", s.ActiveDictateHotkey);
        Assert.Equal("Ctrl+Shift+K", s.ActiveGlossaryHotkey);
        Assert.Equal("", s.ActiveScreenshotHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ExistingInstall_RightCtrlDictationDefaultsOn()
    {
        var dir = NewDir();
        Assert.True(LoadJson(dir, "{}").RightCtrlDictation);
        Assert.False(LoadJson(dir, "{\"rightCtrlDictation\":false}").RightCtrlDictation);
        Directory.Delete(dir, true);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"dictateHotkey\":\"Ctrl+Shift+D\"}")]
    public void ExistingInstall_ReadAloudStaysUnassigned(string json)
    {
        var dir = NewDir();
        var s = LoadJson(dir, json);
        Assert.Equal("", s.ReadAloudHotkey);
        Assert.Equal("", s.ActiveReadAloudHotkey);

        s.Save();
        var t = Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
        Assert.Equal("", t.ActiveReadAloudHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_WithoutReadAloudField_GetsAlt4On()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"dictateHotkey\":\"Alt+1\"}");
        Assert.Equal("Alt+4", s.ReadAloudHotkey);
        Assert.True(s.ReadAloudHotkeyEnabled);
        Assert.Equal("Alt+4", s.ActiveReadAloudHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_WithoutReadAloudField_TemplateOnAlt4_TurnsReadAloudOff()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"templates\":[{\"id\":\"t1\",\"name\":\"Mail\",\"instructions\":\"x\",\"hotkey\":\"alt+4\"}]}");
        Assert.Equal("Alt+4", s.ReadAloudHotkey);
        Assert.False(s.ReadAloudHotkeyEnabled);
        Assert.Equal("", s.ActiveReadAloudHotkey);
        Assert.Equal("alt+4", s.Templates.Single().Hotkey);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("dictateHotkey")]
    [InlineData("promptHotkey")]
    [InlineData("glossaryHotkey")]
    [InlineData("screenshotHotkey")]
    [InlineData("liveDictateHotkey")]
    [InlineData("openSettingsHotkey")]
    [InlineData("meetingHotkey")]
    [InlineData("templateHotkey")]
    public void PinnedFile_WithoutReadAloudField_AnyOtherStoredAlt4_TurnsReadAloudOff(string field)
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"" + field + "\":\"Alt+4\"}");
        Assert.False(s.ReadAloudHotkeyEnabled);
        Assert.Equal("", s.ActiveReadAloudHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_WithReadAloudField_IsNeverOverruled()
    {
        var dir = NewDir();
        // The user (or a former save) chose Alt+4 for both; only a missing field triggers the guard.
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"readAloudHotkey\":\"Alt+4\",\"readAloudHotkeyEnabled\":true,"
            + "\"templateHotkey\":\"Alt+4\"}");
        Assert.Equal("Alt+4", s.ActiveReadAloudHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ReadAloudHotkey_RoundTrips_AndEmptyStaysUnassigned()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.ReadAloudHotkey = "Ctrl+Shift+V";
        s.ReadAloudHotkeyEnabled = false;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+V", t.ReadAloudHotkey);
        Assert.False(t.ReadAloudHotkeyEnabled);
        Assert.Equal("", t.ActiveReadAloudHotkey);

        t.ReadAloudHotkey = "";
        t.ReadAloudHotkeyEnabled = true;
        t.Save();
        var u = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", u.ReadAloudHotkey);
        Directory.Delete(dir, true);
    }
}
