using System.Text.Json.Nodes;
using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;
using WhisperShortcut.Tests.Mcp;

namespace WhisperShortcut.Tests;

/// <summary>
/// W12: the shortcut table (<see cref="HotkeySlots.All"/>) is the only place that lists the shortcuts; persistence, the change
/// snapshot, the settings window's fields and the AI-app list derive from it. A new row must be enough.
/// </summary>
public class HotkeyTableTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");

    private static IEnumerable<HotkeyAction> Actions =>
        Enum.GetValues<HotkeyAction>().Where(a => a != HotkeyAction.Template); // Template = a template's own key, not a slot

    [Fact]
    public void EveryAction_HasExactlyOneRow_AndOneBinding()
    {
        foreach (var a in Actions) Assert.Single(HotkeySlots.All, x => x.Action == a);
        Assert.Equal(Actions.Count(), HotkeySlots.All.Count);
        var s = Settings.Load(TempPath(), new PlainSecretProtector());
        Assert.Equal(HotkeySlots.All.Select(x => x.Action).OrderBy(x => x), s.Hotkeys.Keys.OrderBy(x => x));
    }

    [Fact]
    public void JsonFields_AreUniquePerRow()
    {
        var names = HotkeySlots.All.SelectMany(x => new[] { x.JsonKey, x.JsonOnKey }).Where(n => n != null).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(HotkeySlots.All.Count, HotkeySlots.All.Select(x => x.ConfigName).Distinct().Count());
    }

    [Fact]
    public void DerivedLists_CoverEveryRow()
    {
        var path = TempPath();
        var s = Settings.Load(path, new PlainSecretProtector());
        // Persistence: every row's key (and switch) is a field of the saved file.
        Assert.True(s.TrySave(out _));
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var x in HotkeySlots.All)
        {
            Assert.True(file.ContainsKey(x.JsonKey), x.JsonKey);
            if (x.JsonOnKey != null) Assert.True(file.ContainsKey(x.JsonOnKey), x.JsonOnKey);
        }
        // The change snapshot sees every row's key.
        foreach (var x in HotkeySlots.All)
        {
            var before = SettingsDelta.Snapshot.Of(s);
            var old = s.Key(x.Action);
            s.SetKey(x.Action, old + "9");
            s.SetOn(x.Action, true);
            Assert.True(SettingsDelta.Between(before, s).HotkeysChanged, x.ConfigName);
            s.SetKey(x.Action, old);
        }
        // The AI-app configuration lists every row.
        using var stack = new McpStack();
        var hot = stack.ToolJson("get_configuration", new JsonObject(), McpAccess.Read)["hotkeys"]!.AsObject();
        Assert.Equal(HotkeySlots.All.Select(x => x.ConfigName).OrderBy(x => x), hot.Select(p => p.Key).OrderBy(x => x));
    }

    [Fact]
    public void FileInTheCurrentShape_LoadsEveryShortcutUnchanged_AndSavesTheSameFields()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A settings.json as the released version writes it: flat key and switch fields, pin flag present via a stored model.
        var written = new Dictionary<string, object>
        {
            ["dictateHotkey"] = "Ctrl+Shift+F1", ["dictateHotkeyEnabled"] = false,
            ["promptHotkey"] = "Ctrl+Shift+F2", ["promptHotkeyEnabled"] = true,
            ["glossaryHotkey"] = "Ctrl+Shift+F3", ["glossaryHotkeyEnabled"] = true,
            ["screenshotHotkey"] = "Ctrl+Shift+F4", ["screenshotHotkeyEnabled"] = false,
            ["liveDictateHotkey"] = "Ctrl+Shift+F5", ["liveDictateHotkeyEnabled"] = true,
            ["readAloudHotkey"] = "Ctrl+Shift+F6", ["readAloudHotkeyEnabled"] = false,
            ["chatHotkey"] = "Ctrl+Shift+F7", ["chatHotkeyEnabled"] = true,
            ["openSettingsHotkey"] = "", ["openSettingsHotkeyEnabled"] = false,
            ["meetingHotkey"] = "Ctrl+Shift+F8",
            ["templateHotkey"] = "Ctrl+Shift+F9",
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(written));

        var s = Settings.Load(path, new PlainSecretProtector());
        foreach (var x in HotkeySlots.All)
        {
            Assert.Equal((string)written[x.JsonKey], x.Get(s));
            if (x.JsonOnKey != null) Assert.Equal((bool)written[x.JsonOnKey], x.IsOn(s));
        }
        Assert.Equal("", s.ActiveKey(HotkeyAction.Dictate));
        Assert.Equal("Ctrl+Shift+F2", s.ActiveKey(HotkeyAction.Prompt));

        Assert.True(s.TrySave(out _));
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var (name, value) in written)
        {
            Assert.True(file.ContainsKey(name), name);
            Assert.Equal(value is bool b ? (object)b : (string)value,
                value is bool ? file[name]!.GetValue<bool>() : file[name]!.GetValue<string>());
        }
        Assert.False(file.ContainsKey("hotkeys")); // no new field: the file stays readable by the released version
    }

    [Fact]
    public void FileWithoutTheShortcutFields_KeepsTheLegacyKeys()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"sounds\": true }");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+D", s.Key(HotkeyAction.Dictate));
        Assert.Equal("Ctrl+Shift+P", s.Key(HotkeyAction.Prompt));
        Assert.True(s.IsOn(HotkeyAction.Dictate));
        Assert.Equal("", s.Key(HotkeyAction.Meeting));
    }

    [Fact]
    public void EveryRow_SurvivesSaveLoad_AndCaptureRestore()
    {
        var path = TempPath();
        var s = Settings.Load(path, new PlainSecretProtector());
        var i = 0;
        foreach (var x in HotkeySlots.All)
        {
            s.SetKey(x.Action, $"Ctrl+Shift+F{++i}");
            if (x.HasSwitch) s.SetOn(x.Action, i % 2 == 0);
        }
        var captured = s.Capture();
        Assert.True(s.TrySave(out _));
        var t = Settings.Load(path, new PlainSecretProtector());
        foreach (var x in HotkeySlots.All)
        {
            Assert.Equal(s.Key(x.Action), t.Key(x.Action));
            Assert.Equal(s.IsOn(x.Action), t.IsOn(x.Action));
        }
        foreach (var x in HotkeySlots.All) { t.SetKey(x.Action, "Alt+F12"); if (x.HasSwitch) t.SetOn(x.Action, !x.IsOn(t)); }
        t.Restore(captured);
        foreach (var x in HotkeySlots.All)
        {
            Assert.Equal(s.Key(x.Action), t.Key(x.Action));
            Assert.Equal(s.IsOn(x.Action), t.IsOn(x.Action));
        }
    }
}
