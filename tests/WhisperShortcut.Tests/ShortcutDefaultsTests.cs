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
        Assert.Equal("Alt+1", s.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Alt+2", s.ActiveKey(HotkeyAction.Prompt));
        Assert.Equal("Alt+3", s.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("Alt+0", s.ActiveKey(HotkeyAction.OpenSettings));
        // Live diktieren and Glossar: suggested, switched off (Mac isEnabled: false).
        Assert.Equal("Alt+5", s.Key(HotkeyAction.LiveDictate));
        Assert.False(s.IsOn(HotkeyAction.LiveDictate));
        Assert.Equal("", s.ActiveKey(HotkeyAction.LiveDictate));
        Assert.Equal("Alt+7", s.Key(HotkeyAction.Glossary));
        Assert.False(s.IsOn(HotkeyAction.Glossary));
        Assert.Equal("", s.ActiveKey(HotkeyAction.Glossary));
        Assert.True(s.RightCtrlDictation);
        // Alt+4 is Read Aloud (Mac ⌃⌥4), on, and no other function uses it.
        Assert.Equal("Alt+4", s.ActiveKey(HotkeyAction.ReadAloud));
        Assert.Equal(HotkeySlots.Of(HotkeyAction.ReadAloud).NewDefault, s.Key(HotkeyAction.ReadAloud));
        Assert.DoesNotContain("Alt+4", new[] { s.Key(HotkeyAction.Dictate), s.Key(HotkeyAction.Prompt), s.Key(HotkeyAction.Screenshot), s.Key(HotkeyAction.OpenSettings),
            s.Key(HotkeyAction.LiveDictate), s.Key(HotkeyAction.Glossary) });
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
        Assert.Equal("Alt+1", t.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Alt+3", t.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("Alt+0", t.ActiveKey(HotkeyAction.OpenSettings));
        Assert.Equal("Alt+7", t.Key(HotkeyAction.Glossary));
        Assert.False(t.IsOn(HotkeyAction.Glossary));
        Assert.Equal("Alt+5", t.Key(HotkeyAction.LiveDictate));
        Assert.False(t.IsOn(HotkeyAction.LiveDictate));
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
        Assert.Equal("Ctrl+Shift+D", s.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Ctrl+Shift+P", s.ActiveKey(HotkeyAction.Prompt));
        Assert.Equal("Ctrl+Shift+K", s.ActiveKey(HotkeyAction.Glossary));
        Assert.Equal("", s.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("", s.ActiveKey(HotkeyAction.LiveDictate));
        Assert.Equal("", s.ActiveKey(HotkeyAction.OpenSettings));

        // After the next save the file carries the pin and the explicit old values: a reload keeps them.
        s.Save();
        var t = Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+D", t.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Ctrl+Shift+P", t.ActiveKey(HotkeyAction.Prompt));
        Assert.Equal("Ctrl+Shift+K", t.ActiveKey(HotkeyAction.Glossary));
        Assert.Equal("", t.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("", t.ActiveKey(HotkeyAction.LiveDictate));
        Assert.Equal("", t.ActiveKey(HotkeyAction.OpenSettings));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ExistingInstall_CustomKeysAreUntouched()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"dictateHotkey\":\"Ctrl+Shift+X\",\"promptHotkey\":\"Alt+Shift+Y\",\"glossaryHotkey\":\"Ctrl+Shift+G\","
            + "\"screenshotHotkey\":\"Ctrl+Shift+S\",\"liveDictateHotkey\":\"Ctrl+Shift+L\",\"meetingHotkey\":\"Ctrl+Shift+M\"}");
        Assert.Equal("Ctrl+Shift+X", s.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Alt+Shift+Y", s.ActiveKey(HotkeyAction.Prompt));
        Assert.Equal("Ctrl+Shift+G", s.ActiveKey(HotkeyAction.Glossary));
        Assert.Equal("Ctrl+Shift+S", s.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("Ctrl+Shift+L", s.ActiveKey(HotkeyAction.LiveDictate));
        Assert.Equal("Ctrl+Shift+M", s.Key(HotkeyAction.Meeting));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_MissingFields_GetTheNewDefaults()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"promptHotkey\":\"Ctrl+Shift+P\"}");
        Assert.Equal("Alt+1", s.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Ctrl+Shift+P", s.ActiveKey(HotkeyAction.Prompt));
        Assert.Equal("Alt+3", s.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("Alt+0", s.ActiveKey(HotkeyAction.OpenSettings));
        Assert.Equal("", s.ActiveKey(HotkeyAction.Glossary));
        Assert.Equal("", s.ActiveKey(HotkeyAction.LiveDictate));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_EmptyStringStaysUnassigned()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"screenshotHotkey\":\"\",\"openSettingsHotkey\":\"\"}");
        Assert.Equal("", s.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("", s.ActiveKey(HotkeyAction.OpenSettings));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void EnabledFlags_And_RightCtrl_RoundTrip()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.SetOn(HotkeyAction.Glossary, true);
        s.SetOn(HotkeyAction.LiveDictate, true);
        s.SetOn(HotkeyAction.Screenshot, false);
        s.SetOn(HotkeyAction.OpenSettings, false);
        s.SetOn(HotkeyAction.Dictate, false);
        s.RightCtrlDictation = false;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Alt+7", t.ActiveKey(HotkeyAction.Glossary));
        Assert.Equal("Alt+5", t.ActiveKey(HotkeyAction.LiveDictate));
        Assert.Equal("", t.ActiveKey(HotkeyAction.Screenshot));
        Assert.Equal("Alt+3", t.Key(HotkeyAction.Screenshot));
        Assert.Equal("", t.ActiveKey(HotkeyAction.OpenSettings));
        Assert.Equal("", t.ActiveKey(HotkeyAction.Dictate));
        Assert.True(t.IsOn(HotkeyAction.Prompt));
        Assert.False(t.RightCtrlDictation);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptFile_CountsAsExistingInstall()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{ nope");
        Assert.Equal("Ctrl+Shift+D", s.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Ctrl+Shift+K", s.ActiveKey(HotkeyAction.Glossary));
        Assert.Equal("", s.ActiveKey(HotkeyAction.Screenshot));
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
        Assert.Equal("", s.Key(HotkeyAction.ReadAloud));
        Assert.Equal("", s.ActiveKey(HotkeyAction.ReadAloud));

        s.Save();
        var t = Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
        Assert.Equal("", t.ActiveKey(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_WithoutReadAloudField_GetsAlt4On()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"dictateHotkey\":\"Alt+1\"}");
        Assert.Equal("Alt+4", s.Key(HotkeyAction.ReadAloud));
        Assert.True(s.IsOn(HotkeyAction.ReadAloud));
        Assert.Equal("Alt+4", s.ActiveKey(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_WithoutReadAloudField_TemplateOnAlt4_TurnsReadAloudOff()
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"templates\":[{\"id\":\"t1\",\"name\":\"Mail\",\"instructions\":\"x\",\"hotkey\":\"alt+4\"}]}");
        Assert.Equal("Alt+4", s.Key(HotkeyAction.ReadAloud));
        Assert.False(s.IsOn(HotkeyAction.ReadAloud));
        Assert.Equal("", s.ActiveKey(HotkeyAction.ReadAloud));
        Assert.Equal("alt+4", s.Templates.Single().Hotkey);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("dictateHotkey")]
    [InlineData("promptHotkey")]
    [InlineData("screenshotHotkey")]
    [InlineData("openSettingsHotkey")]
    [InlineData("meetingHotkey")]
    [InlineData("templateHotkey")]
    public void PinnedFile_WithoutReadAloudField_AnyOtherStoredAlt4_TurnsReadAloudOff(string field)
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"" + field + "\":\"Alt+4\"}");
        Assert.False(s.IsOn(HotkeyAction.ReadAloud));
        Assert.Equal("", s.ActiveKey(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("glossaryHotkey")]
    [InlineData("liveDictateHotkey")]
    public void PinnedFile_WithoutReadAloudField_SwitchedOnAlt4_TurnsReadAloudOff_SwitchedOffDoesNotCount(string field)
    {
        var dir = NewDir();
        var on = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"" + field + "\":\"Alt+4\",\"" + field + "Enabled\":true}");
        Assert.False(on.IsOn(HotkeyAction.ReadAloud));
        // These two ship switched off: a stored key behind a switched-off shortcut is not taken (what the tray registers).
        var off = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"" + field + "\":\"Alt+4\"}");
        Assert.True(off.IsOn(HotkeyAction.ReadAloud));
        Assert.Equal("Alt+4", off.ActiveKey(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PinnedFile_WithReadAloudField_IsNeverOverruled()
    {
        var dir = NewDir();
        // The user (or a former save) chose Alt+4 for both; only a missing field triggers the guard.
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"readAloudHotkey\":\"Alt+4\",\"readAloudHotkeyEnabled\":true,"
            + "\"templateHotkey\":\"Alt+4\"}");
        Assert.Equal("Alt+4", s.ActiveKey(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ReadAloudHotkey_RoundTrips_AndEmptyStaysUnassigned()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.SetKey(HotkeyAction.ReadAloud, "Ctrl+Shift+V");
        s.SetOn(HotkeyAction.ReadAloud, false);
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+V", t.Key(HotkeyAction.ReadAloud));
        Assert.False(t.IsOn(HotkeyAction.ReadAloud));
        Assert.Equal("", t.ActiveKey(HotkeyAction.ReadAloud));

        t.SetKey(HotkeyAction.ReadAloud, "");
        t.SetOn(HotkeyAction.ReadAloud, true);
        t.Save();
        var u = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", u.Key(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("alt + 6")]
    [InlineData("Alt+6")]
    [InlineData("ALT+6")]
    public void FileWithoutChatField_TemplateWithSameKeyInAnySpelling_TurnsChatOff(string spelling)
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"templates\":[{\"id\":\"t1\",\"name\":\"Mail\",\"instructions\":\"x\",\"hotkey\":\"" + spelling + "\"}]}");
        Assert.False(s.IsOn(HotkeyAction.Chat));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("alt + 4")]
    [InlineData("Alt+4")]
    public void PinnedFile_WithoutReadAloudField_TemplateWithSameKeyInAnySpelling_TurnsReadAloudOff(string spelling)
    {
        var dir = NewDir();
        var s = LoadJson(dir, "{\"shortcutDefaultsAltV1\":true,\"templates\":[{\"id\":\"t1\",\"name\":\"Mail\",\"instructions\":\"x\",\"hotkey\":\"" + spelling + "\"}]}");
        Assert.False(s.IsOn(HotkeyAction.ReadAloud));
        Directory.Delete(dir, true);
    }
}
