using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SettingsDeltaTests
{
    private sealed class Plain : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string? Unprotect(string protectedValue) => protectedValue;
    }

    private static Settings Fresh() =>
        Settings.Load(Path.Combine(Path.GetTempPath(), "ws-delta-" + Guid.NewGuid().ToString("N") + ".json"), new Plain());

    private static SettingsDelta After(Action<Settings> change)
    {
        var s = Fresh();
        var before = SettingsDelta.Snapshot.Of(s);
        change(s);
        return SettingsDelta.Between(before, s);
    }

    [Fact]
    public void Identical_IsEmpty() => Assert.Equal(default, After(_ => { }));

    [Fact]
    public void Glossary_FlipsOnlyGlossary() =>
        Assert.Equal(new SettingsDelta(true, false, false), After(s => s.Glossary = new() { "Gödde" }));

    [Fact]
    public void OfflineMode_FlipsOnlyOffline() =>
        Assert.Equal(new SettingsDelta(false, true, false), After(s => s.OfflineMode = !s.OfflineMode));

    [Theory]
    [InlineData("dictate")]
    [InlineData("dictate-off")]
    [InlineData("meeting")]
    [InlineData("template-picker")]
    [InlineData("right-ctrl")]
    [InlineData("template-key")]
    public void Hotkeys_FlipOnlyHotkeys(string which)
    {
        var d = After(s =>
        {
            switch (which)
            {
                case "dictate": s.DictateHotkey = "Ctrl+Alt+9"; break;
                case "dictate-off": s.DictateHotkeyEnabled = false; break;
                case "meeting": s.MeetingHotkey = "Ctrl+Alt+8"; break;
                case "template-picker": s.TemplateHotkey = "Ctrl+Alt+7"; break;
                case "right-ctrl": s.RightCtrlDictation = !s.RightCtrlDictation; break;
                case "template-key": s.Templates = new() { new DictationTemplate { Id = "a", Hotkey = "Ctrl+1" } }; break;
            }
        });
        Assert.Equal(new SettingsDelta(false, false, true), d);
    }

    [Fact]
    public void UnrelatedChange_IsEmpty() =>
        Assert.Equal(default, After(s => { s.Sounds = !s.Sounds; s.ApiKey = "k"; s.AutoPaste = !s.AutoPaste; }));

    [Fact]
    public void StoredKeyOfSwitchedOffShortcut_IsNotAHotkeyChange()
    {
        var s = Fresh();
        s.GlossaryHotkeyEnabled = false;
        var before = SettingsDelta.Snapshot.Of(s);
        s.GlossaryHotkey = "Ctrl+Alt+6";
        Assert.Equal(default, SettingsDelta.Between(before, s));
    }

    [Fact]
    public void Snapshot_IsACopy()
    {
        var s = Fresh();
        s.Glossary = new() { "a" };
        var before = SettingsDelta.Snapshot.Of(s);
        s.Glossary.Add("b");
        Assert.True(SettingsDelta.Between(before, s).GlossaryChanged);
    }
}
