using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class DictationTemplatesTests
{
    private static DictationTemplate T(string id, string instructions = "Mach etwas.", string hotkey = "", string name = "")
        => new() { Id = id, Name = name == "" ? "N" + id : name, Instructions = instructions, Hotkey = hotkey };

    private static string Canon(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();
    private static string? CanonOrNull(string s) { var c = Canon(s); return c.Length == 0 ? null : c; }

    private static readonly (string Name, string Hotkey)[] Apps =
    {
        ("Diktieren", "Ctrl+Shift+D"), ("Text bearbeiten", "Ctrl+Shift+P"), ("Glossar", "Ctrl+Shift+K"),
    };

    // ---- DictationTemplate ----

    [Fact]
    public void Template_Basics()
    {
        var t = new DictationTemplate { Name = "  Anamnese ", Instructions = " x ", Hotkey = "Ctrl+1" };
        Assert.True(Guid.TryParse(t.Id, out _));
        Assert.Equal("Anamnese", t.DisplayName);
        Assert.True(t.IsUsable);
        var c = t.Clone();
        Assert.NotSame(t, c);
        Assert.Equal((t.Id, t.Name, t.Instructions, t.Hotkey), (c.Id, c.Name, c.Instructions, c.Hotkey));
        Assert.Equal("Vorlage", new DictationTemplate { Name = "  " }.DisplayName);
        Assert.False(new DictationTemplate { Instructions = " \n " }.IsUsable);
    }

    // ---- TemplatePrompt ----

    [Fact]
    public void SystemPrompt_IsInstructionsPlusOutputRule_Only()
    {
        var p = TemplatePrompt.SystemPrompt("  Format A  \n");
        Assert.Equal("Format A" + Prompts.PromptModeOutputRule, p);
        Assert.DoesNotContain(Prompts.DefaultPromptModeSystemPrompt, p);
        Assert.DoesNotContain("Template for new texts", p);
    }

    [Fact]
    public void UserTurn_WithSelection_AndTranscript()
    {
        var u = TemplatePrompt.UserTurn("Der markierte Text", "  und das Diktat \n");
        Assert.StartsWith(TemplatePrompt.SelectionHeader, u);
        Assert.Contains("Der markierte Text", u);
        Assert.Contains(TemplatePrompt.DictationHeader + "\nund das Diktat", u);
        Assert.EndsWith("und das Diktat", u);
        Assert.DoesNotContain(Prompts.ClipboardSelectionHeader, u);
    }

    [Fact]
    public void UserTurn_WithSelection_BlankTranscript_HasNoDictationPart()
    {
        var u = TemplatePrompt.UserTurn("Der markierte Text", "  ");
        Assert.Equal(TemplatePrompt.SelectionHeader + "\n\nDer markierte Text", u);
        Assert.DoesNotContain(TemplatePrompt.DictationHeader, u);
        Assert.DoesNotContain("VOICE INSTRUCTION", u);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void UserTurn_WithoutSelection_IsComposeTurn(string? selection)
    {
        var u = TemplatePrompt.UserTurn(selection, " Das Diktat ");
        Assert.StartsWith(Prompts.DictatePromptComposeMarker, u);
        Assert.EndsWith("VOICE INSTRUCTION:\nDas Diktat", u);
        Assert.DoesNotContain(Prompts.ClipboardSelectionHeader, u);
        Assert.DoesNotContain(TemplatePrompt.SelectionHeader, u);
    }

    [Theory]
    [InlineData("text", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   \n", false)]
    public void AcceptsEmptyTranscript_OnlyWithSelection(string? selection, bool expected) =>
        Assert.Equal(expected, TemplatePrompt.AcceptsEmptyTranscript(selection));

    // ---- TemplatePicker ----

    [Fact]
    public void Picker_LastUsedWins()
    {
        var list = new[] { T("a"), T("b"), T("c") };
        Assert.Equal("b", TemplatePicker.Pick(list, "b")!.Id);
    }

    [Theory]
    [InlineData("zzz")]
    [InlineData("")]
    [InlineData(null)]
    public void Picker_UnknownOrBlankLastId_FirstUsable(string? last)
    {
        var list = new[] { T("a", instructions: " "), T("b"), T("c") };
        Assert.Equal("b", TemplatePicker.Pick(list, last)!.Id);
    }

    [Fact]
    public void Picker_LastIdOfUnusableTemplate_FirstUsable()
    {
        var list = new[] { T("a"), T("b", instructions: ""), T("c") };
        Assert.Equal("a", TemplatePicker.Pick(list, "b")!.Id);
    }

    [Fact]
    public void Picker_NoUsable_Null()
    {
        Assert.Null(TemplatePicker.Pick(new[] { T("a", instructions: "") }, "a"));
        Assert.Null(TemplatePicker.Pick(Array.Empty<DictationTemplate>(), null));
    }

    [Fact]
    public void Picker_OnlyTemplate()
    {
        Assert.Equal("a", TemplatePicker.Pick(new[] { T("a") }, null)!.Id);
    }

    // ---- TemplateHotkeys ----

    [Fact]
    public void AppHotkey_WinsOverTemplate_AndOverPicker()
    {
        var t = T("a", hotkey: "Ctrl+Shift+D");
        var list = new[] { t };
        var owner = TemplateHotkeys.Owner(t, list, Apps, "", CanonOrNull);
        Assert.Equal(new HotkeyOwner(HotkeyOwnerKind.App, "Diktieren"), owner);
        Assert.Empty(TemplateHotkeys.ActiveBindings(list, Apps, "", CanonOrNull));

        Assert.Equal(new HotkeyOwner(HotkeyOwnerKind.App, "Glossar"), TemplateHotkeys.PickerOwner("ctrl+shift+k", Apps, CanonOrNull));
        Assert.False(TemplateHotkeys.PickerIsActive("ctrl+shift+k", list, Apps, CanonOrNull));
    }

    [Fact]
    public void Picker_WinsOverTemplate()
    {
        var t = T("a", hotkey: "Ctrl+Shift+T");
        var list = new[] { t };
        Assert.True(TemplateHotkeys.PickerIsActive("Ctrl+Shift+T", list, Apps, CanonOrNull));
        Assert.Equal(new HotkeyOwner(HotkeyOwnerKind.Picker, TemplateHotkeys.PickerName),
            TemplateHotkeys.Owner(t, list, Apps, "Ctrl+Shift+T", CanonOrNull));
        Assert.Empty(TemplateHotkeys.ActiveBindings(list, Apps, "Ctrl+Shift+T", CanonOrNull));
    }

    [Fact]
    public void InactivePicker_DoesNotBlockTemplate()
    {
        var t = T("a", instructions: "x", hotkey: "Ctrl+Shift+T");
        var unusable = T("b", instructions: "");
        // Picker has the same key but no usable template exists besides... here one exists, so make the only-usable case:
        Assert.False(TemplateHotkeys.PickerIsActive("Ctrl+Shift+T", new[] { unusable }, Apps, CanonOrNull));
        Assert.Null(TemplateHotkeys.Owner(t, new[] { t, unusable }, Apps, "", CanonOrNull));
    }

    [Fact]
    public void Picker_Inactive_WithBlankKey_OrNoUsableTemplate()
    {
        Assert.False(TemplateHotkeys.PickerIsActive("", new[] { T("a") }, Apps, CanonOrNull));
        Assert.False(TemplateHotkeys.PickerIsActive(null, new[] { T("a") }, Apps, CanonOrNull));
        Assert.False(TemplateHotkeys.PickerIsActive("Ctrl+Shift+T", new[] { T("a", instructions: "") }, Apps, CanonOrNull));
        Assert.False(TemplateHotkeys.PickerIsActive("Ctrl+Shift+T", Array.Empty<DictationTemplate>(), Apps, CanonOrNull));
        Assert.True(TemplateHotkeys.PickerIsActive("Ctrl+Shift+T", new[] { T("a") }, Apps, CanonOrNull));
    }

    [Fact]
    public void EarlierUsableTemplate_WinsOverLater()
    {
        var first = T("a", hotkey: "Ctrl+Shift+1", name: "Erste");
        var second = T("b", hotkey: "ctrl+shift+1");
        var list = new[] { first, second };
        Assert.Null(TemplateHotkeys.Owner(first, list, Apps, "", CanonOrNull));
        Assert.Equal(new HotkeyOwner(HotkeyOwnerKind.Template, "Erste"), TemplateHotkeys.Owner(second, list, Apps, "", CanonOrNull));
        var active = TemplateHotkeys.ActiveBindings(list, Apps, "", CanonOrNull);
        Assert.Single(active);
        Assert.Equal("a", active[0].Template.Id);
    }

    [Fact]
    public void UnusableEarlierTemplate_DoesNotBlock()
    {
        var first = T("a", instructions: " ", hotkey: "Ctrl+Shift+1");
        var second = T("b", hotkey: "Ctrl+Shift+1");
        var list = new[] { first, second };
        Assert.Null(TemplateHotkeys.Owner(second, list, Apps, "", CanonOrNull));
        var active = TemplateHotkeys.ActiveBindings(list, Apps, "", CanonOrNull);
        Assert.Equal("b", Assert.Single(active).Template.Id);
    }

    [Fact]
    public void CanonicalComparison_DetectsDuplicate_RawStringsDoNot()
    {
        var a = T("a", hotkey: "ctrl+shift+1");
        var b = T("b", hotkey: " Ctrl+Shift+1 ");
        var list = new[] { a, b };
        Assert.NotEqual(a.Hotkey, b.Hotkey);
        Assert.NotNull(TemplateHotkeys.Owner(b, list, Apps, "", CanonOrNull));
    }

    [Fact]
    public void ActiveBindings_ReturnsExactlyTheWinners_WithCanonicalKey()
    {
        var list = new[]
        {
            T("a", hotkey: "Ctrl+Shift+1"),
            T("b", hotkey: "Ctrl+Shift+1"),            // loses to a
            T("c", hotkey: "Ctrl+Shift+P"),            // loses to the app
            T("d", hotkey: "Ctrl+Shift+T"),            // loses to the picker
            T("e", hotkey: ""),                        // no key
            T("f", instructions: "", hotkey: "Ctrl+Shift+2"), // unusable
            T("g", hotkey: " CTRL+Shift+3 "),
        };
        var active = TemplateHotkeys.ActiveBindings(list, Apps, "Ctrl+Shift+T", CanonOrNull);
        Assert.Equal(new[] { "a", "g" }, active.Select(x => x.Template.Id).ToArray());
        Assert.Equal(new[] { "ctrl+shift+1", "ctrl+shift+3" }, active.Select(x => x.Hotkey).ToArray());
    }

    [Fact]
    public void Owner_NullWhenTemplateHasNoKey()
    {
        var t = T("a", hotkey: "");
        Assert.Null(TemplateHotkeys.Owner(t, new[] { t }, Apps, "", CanonOrNull));
    }
}
