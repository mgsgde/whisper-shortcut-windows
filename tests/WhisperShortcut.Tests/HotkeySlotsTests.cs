using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>The shortcut table and its one clash check.</summary>
public class HotkeySlotsTests
{
    private static Settings Fresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        return Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector());
    }

    // Every slot on with its own key: no clash anywhere.
    private static Settings AllDistinct()
    {
        var s = Fresh();
        s.RightCtrlDictation = false;
        for (int i = 0; i < HotkeySlots.All.Count; i++)
        {
            var slot = HotkeySlots.All[i];
            slot.Set(s, $"Ctrl+Shift+F{i + 1}");
            slot.SetOn?.Invoke(s, true);
        }
        return s;
    }

    private static DictationTemplate Template(string name, string hotkey, string instructions = "x") =>
        new() { Name = name, Hotkey = hotkey, Instructions = instructions };

    [Fact]
    public void Table_OwnersAndDefaultsAreUnique_AndSwitchableSlotsHaveBothAccessors()
    {
        Assert.Equal(HotkeySlots.All.Count, HotkeySlots.All.Select(x => x.Owner).Distinct().Count());
        Assert.Equal(HotkeySlots.All.Count, HotkeySlots.All.Select(x => x.Action).Distinct().Count());
        Assert.All(HotkeySlots.All, x => Assert.Equal(x.On == null, x.SetOn == null));
        var keys = HotkeySlots.All.Select(x => x.NewDefault).Where(k => k.Length > 0).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(HotkeySlots.All.Where(x => x.NewDefault.Length > 0), x => Assert.True(WhisperShortcut.Core.Mcp.HotkeyRules.IsSafe(x.NewDefault)));
    }

    [Fact]
    public void FreshInstall_FollowsTheTable_AndHasNoConflict()
    {
        var s = Fresh();
        foreach (var slot in HotkeySlots.All.Where(x => x.On != null))
        {
            Assert.Equal(slot.NewDefault, slot.Get(s));
            Assert.Equal(slot.NewEnabled, slot.On!(s));
        }
        Assert.Null(HotkeySlots.Check(s));
    }

    [Fact]
    public void EveryPair_Clashes_InBothDirections_InAnySpelling()
    {
        var slots = HotkeySlots.All;
        for (int a = 0; a < slots.Count; a++)
            for (int b = 0; b < slots.Count; b++)
            {
                if (a == b) continue;
                var s = AllDistinct();
                slots[a].Set(s, "Alt+9");
                slots[b].Set(s, "alt + 9");
                var c = HotkeySlots.Check(s);
                Assert.NotNull(c);
                Assert.Equal(HotkeyConflictKind.Taken, c!.Kind);
                Assert.Equal(new[] { slots[a].Owner, slots[b].Owner }.Order(), new[] { c.Offender.Owner, c.Other!.Owner }.Order());
                Assert.Equal("Alt+9", c.Display);
            }
    }

    [Fact]
    public void SwitchedOffKey_IsNotTaken_UntilItIsSwitchedOn()
    {
        var s = AllDistinct();
        s.GlossaryHotkey = s.DictateHotkey;
        Assert.NotNull(HotkeySlots.Check(s));
        s.GlossaryHotkeyEnabled = false;
        Assert.Null(HotkeySlots.Check(s));
        Assert.DoesNotContain(HotkeySlots.Assignments(s), a => a.Action == HotkeyAction.Glossary);
        s.GlossaryHotkeyEnabled = true;
        Assert.Equal(HotkeyConflictKind.Taken, HotkeySlots.Check(s)!.Kind);
    }

    [Fact]
    public void OverrideKey_ReplacesTheStoredKey_EmptyMeansOff()
    {
        var s = AllDistinct();
        var dictate = HotkeySlots.Of(HotkeyAction.Dictate);
        var glossary = HotkeySlots.Of(HotkeyAction.Glossary);
        var same = s.DictateHotkey;
        Assert.NotNull(HotkeySlots.Check(s, new HotkeyOverrides(x => x == glossary ? same : null)));
        Assert.Null(HotkeySlots.Check(s, new HotkeyOverrides(x => x == glossary ? "" : null)));
        Assert.Null(HotkeySlots.Check(s, new HotkeyOverrides(x => x == dictate ? "Alt+8" : null)));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Q", HotkeyConflictKind.Unsafe)]
    [InlineData("Ctrl+D", HotkeyConflictKind.Unsafe)]
    [InlineData("Shift+F5", HotkeyConflictKind.Unsafe)]
    [InlineData("gibberish", HotkeyConflictKind.Invalid)]
    [InlineData("Ctrl+Shift", HotkeyConflictKind.Invalid)]
    public void InvalidOrUnsafeKey_IsReportedOnItsOwner(string key, HotkeyConflictKind kind)
    {
        var s = AllDistinct();
        s.OpenSettingsHotkey = key;
        var c = HotkeySlots.Check(s);
        Assert.Equal(kind, c!.Kind);
        Assert.Equal(HotkeyAction.OpenSettings, c.Offender.Action);
        Assert.Null(c.Other);
        Assert.Null(HotkeySlots.Check(s, takenOnly: true));
    }

    [Theory]
    [InlineData("Alt+1")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("strg+umschalt+d")]
    public void SafeKeys_Pass(string key)
    {
        var s = AllDistinct();
        s.MeetingHotkey = key;
        Assert.Null(HotkeySlots.Check(s));
    }

    [Fact]
    public void TemplateKeys_CountWhenUsable_AndClashWithSlotsPickerAndEachOther()
    {
        var s = AllDistinct();
        s.Templates = new() { Template("Mail", s.DictateHotkey), Template("Brief", "Ctrl+Shift+F20"), Template("Brief2", "ctrl+shift+f20") };
        var c = HotkeySlots.Check(s)!;
        Assert.Equal(HotkeyAction.Template, c.Offender.Action);
        Assert.Equal("Vorlage „Mail“", c.Offender.Owner);
        Assert.Equal("Diktieren", c.Other!.Owner);

        s.Templates[0].Hotkey = "";
        c = HotkeySlots.Check(s)!;
        Assert.Equal("Vorlage „Brief2“", c.Offender.Owner);
        Assert.Equal("Vorlage „Brief“", c.Other!.Owner);

        s.Templates[2].Hotkey = s.TemplateHotkey;
        c = HotkeySlots.Check(s)!;
        Assert.Equal("Vorlagen", c.Other!.Owner);
    }

    [Fact]
    public void TemplateWithoutInstructions_OwnsNoKey()
    {
        var s = AllDistinct();
        s.Templates = new() { Template("Leer", s.DictateHotkey, instructions: " ") };
        Assert.Null(HotkeySlots.Check(s));
        Assert.DoesNotContain(HotkeySlots.Assignments(s), a => a.Action == HotkeyAction.Template);
        Assert.Contains(HotkeySlots.Assignments(s, new HotkeyOverrides(Templates: new[] { Template("Voll", "Ctrl+Shift+F30") })),
            a => a.Action == HotkeyAction.Template && a.Owner == "Vorlage „Voll“");
    }

    [Fact]
    public void RightCtrl_IsHeldWhileItDictates()
    {
        var s = AllDistinct();
        s.MeetingHotkey = "RightCtrl";
        Assert.Null(HotkeySlots.Check(s));
        s.RightCtrlDictation = true;
        var c = HotkeySlots.Check(s)!;
        Assert.Equal(HotkeyConflictKind.Taken, c.Kind);
        Assert.Contains(HotkeySlots.RightCtrlOwner, new[] { c.Offender.Owner, c.Other!.Owner });
        Assert.Null(HotkeySlots.Check(s, new HotkeyOverrides(RightCtrl: false)));
    }

    [Fact]
    public void Focus_LimitsTheReportToTheKeysInvolved()
    {
        var s = AllDistinct();
        s.ScreenshotHotkey = s.ChatHotkey; // an old clash between two keys AI apps cannot change
        Assert.NotNull(HotkeySlots.Check(s));
        Assert.Null(HotkeySlots.Check(s, focus: a => a.Action == HotkeyAction.Dictate));
        s.DictateHotkey = s.ChatHotkey;
        Assert.NotNull(HotkeySlots.Check(s, focus: a => a.Action == HotkeyAction.Dictate));
    }

    [Theory]
    [InlineData("alt + 1", "Alt+1")]
    [InlineData("STRG+umschalt+d", "Ctrl+Shift+D")]
    [InlineData("rechte Strg", "RightCtrl")]
    [InlineData("ctrl+shift+space", "Ctrl+Shift+space")]
    public void Normalize_IsOneForm(string input, string expected) => Assert.Equal(expected, HotkeySlots.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]
    [InlineData("Ctrl+")]
    public void Normalize_BlankOrNoCombination_IsNull(string input) => Assert.Null(HotkeySlots.Normalize(input));

    [Fact]
    public void SameKey_IgnoresSpellingAndCase_NeverForBlank()
    {
        Assert.True(HotkeySlots.SameKey("alt + 6", "ALT+6"));
        Assert.True(HotkeySlots.SameKey("Ctrl+Shift+Space", "ctrl+shift+SPACE"));
        Assert.False(HotkeySlots.SameKey("", ""));
        Assert.False(HotkeySlots.SameKey("Alt+6", "Alt+7"));
    }

    [Fact]
    public void Assignments_AreWhatTheTrayRegisters()
    {
        var s = AllDistinct();
        s.GlossaryHotkeyEnabled = false;
        s.MeetingHotkey = "";
        s.TemplateHotkey = "Ctrl+Shift+F40";
        s.Templates = new() { Template("Mail", "Ctrl+Shift+F41"), Template("Leer", "Ctrl+Shift+F42", instructions: "") };
        s.RightCtrlDictation = true;
        var all = HotkeySlots.Assignments(s).ToList();

        // The tray registers the active keys of the app functions, in this order ...
        var expectedApp = new[]
        {
            s.ActiveDictateHotkey, s.ActivePromptHotkey, s.ActiveScreenshotHotkey, s.ActiveReadAloudHotkey, s.ActiveGlossaryHotkey,
            s.ActiveLiveDictateHotkey, s.ActiveChatHotkey, s.ActiveOpenSettingsHotkey, s.MeetingHotkey,
        }.Where(k => k.Length > 0).ToList();
        Assert.Equal(expectedApp, all.Where(a => a.Action is not (HotkeyAction.TemplatePicker or HotkeyAction.Template) && !a.IsRightCtrlSwitch)
            .Select(a => a.Key));
        // ... then the picker, the usable templates, and right Ctrl while it dictates.
        Assert.Equal(new[] { HotkeyAction.TemplatePicker, HotkeyAction.Template, HotkeyAction.Dictate },
            all.Skip(expectedApp.Count).Select(a => a.Action));
        Assert.True(all[^1].IsRightCtrlSwitch);

        // Template registration (TemplateHotkeys) is fed from the same list and agrees.
        var apps = all.Where(a => a.Action is not (HotkeyAction.TemplatePicker or HotkeyAction.Template) && !a.IsRightCtrlSwitch)
            .Select(a => (a.Owner, a.Key)).ToList();
        var bindings = TemplateHotkeys.ActiveBindings(s.Templates, apps, s.TemplateHotkey, HotkeySlots.Normalize);
        Assert.Equal(new[] { "Ctrl+Shift+F41" }, bindings.Select(b => b.Hotkey));
    }

    [Fact]
    public void Assignments_ShadowedTemplateKey_IsListedButTheTrayDoesNotBindIt()
    {
        var s = AllDistinct();
        s.Templates = new() { Template("Mail", s.PromptHotkey.ToLowerInvariant()) };
        var apps = HotkeySlots.Assignments(s).Where(a => a.Action != HotkeyAction.Template && !a.IsRightCtrlSwitch)
            .Select(a => (a.Owner, a.Key)).ToList();
        Assert.Contains(HotkeySlots.Assignments(s), a => a.Action == HotkeyAction.Template);
        Assert.Empty(TemplateHotkeys.ActiveBindings(s.Templates, apps, "", HotkeySlots.Normalize));
        Assert.Equal(HotkeyConflictKind.Taken, HotkeySlots.Check(s)!.Kind);
    }

    [Fact]
    public void Clashes_IgnoresItselfAndSwitchedOffKeys()
    {
        var s = AllDistinct();
        Assert.False(HotkeySlots.Clashes(s, HotkeyAction.Chat));
        s.ChatHotkey = s.ReadAloudHotkey;
        Assert.True(HotkeySlots.Clashes(s, HotkeyAction.Chat));
        s.ReadAloudHotkeyEnabled = false;
        Assert.False(HotkeySlots.Clashes(s, HotkeyAction.Chat));
    }
}
