namespace WhisperShortcut.Core;

/// <summary>
/// A dictation template ("Vorlage"): a name, the instructions that form the whole system prompt of its run, and
/// optionally an own global hotkey.
/// </summary>
public sealed class DictationTemplate
{
    /// <summary>Stable id (Guid "D" format); the hotkey registration and the last-used memory point at it.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>Name shown in the settings, the overlay and the tray.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the model is told to do with the dictation; the whole system prompt of a template run.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Stored hotkey like "Ctrl+Shift+1"; "" = no own hotkey.</summary>
    public string Hotkey { get; set; } = "";

    /// <summary>True when the instructions are not blank. A template without them cannot run and owns no hotkey.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Instructions);

    /// <summary>The trimmed <see cref="Name"/>, or "Vorlage" when blank.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Vorlage" : Name.Trim();

    /// <summary>A fresh id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>A copy with the same <see cref="Id"/>.</summary>
    public DictationTemplate Clone() => new()
    {
        Id = Id,
        Name = Name,
        Instructions = Instructions,
        Hotkey = Hotkey,
    };
}

/// <summary>Chooses the template the generic "Vorlage" hotkey applies.</summary>
public static class TemplatePicker
{
    /// <summary>What the generic hotkey applies: the usable template with <paramref name="lastUsedId"/>, else the first usable one, else null.</summary>
    public static DictationTemplate? Pick(IReadOnlyList<DictationTemplate> templates, string? lastUsedId)
    {
        if (!string.IsNullOrEmpty(lastUsedId))
        {
            var last = templates.FirstOrDefault(t => t.IsUsable && t.Id == lastUsedId);
            if (last != null) return last;
        }
        return templates.FirstOrDefault(t => t.IsUsable);
    }
}

/// <summary>The texts of a template run (Mac: templateSelectionHeader / decidePromptTurnKind).</summary>
public static class TemplatePrompt
{
    /// <summary>Header over the selected text that is put into the template's format.</summary>
    public const string SelectionHeader = "Content to put into the format (the user selected it):";

    /// <summary>Header over what was dictated in addition to a selection.</summary>
    public const string DictationHeader = "Additional content the user dictated:";

    /// <summary>
    /// The template's instructions (trimmed) plus <see cref="Prompts.PromptModeOutputRule"/>. WHY nothing else: the
    /// default editing prompt, the stored Dictate Prompt prompt and the template for new texts would pull the
    /// model away from the format the user defined.
    /// </summary>
    public static string SystemPrompt(string instructions) => instructions.Trim() + Prompts.PromptModeOutputRule;

    /// <summary>
    /// The text-only user turn of a template run. With a selection: the selection under <see cref="SelectionHeader"/>,
    /// plus the dictation under <see cref="DictationHeader"/> when there is one. Without: the compose marker and the
    /// dictation as voice instruction.
    /// </summary>
    public static string UserTurn(string? selection, string transcript)
    {
        var spoken = transcript.Trim();
        if (string.IsNullOrWhiteSpace(selection))
            return Prompts.DictatePromptComposeMarker + "\n\nVOICE INSTRUCTION:\n" + spoken;
        var turn = SelectionHeader + "\n\n" + selection;
        if (spoken.Length > 0) turn += "\n\n" + DictationHeader + "\n" + spoken;
        return turn;
    }

    /// <summary>A template may format a selection with no speech: true when <paramref name="selection"/> is not blank.</summary>
    public static bool AcceptsEmptyTranscript(string? selection) => !string.IsNullOrWhiteSpace(selection);
}

/// <summary>What owns a hotkey.</summary>
public enum HotkeyOwnerKind
{
    /// <summary>One of the app's own hotkeys (Diktieren, Text bearbeiten, Glossar).</summary>
    App,
    /// <summary>The generic "Vorlage" hotkey.</summary>
    Picker,
    /// <summary>Another template's own hotkey.</summary>
    Template,
}

/// <summary>Who owns a hotkey. <paramref name="Name"/>: the app slot name, "Vorlage" for the picker, the template's display name.</summary>
public sealed record HotkeyOwner(HotkeyOwnerKind Kind, string Name);

/// <summary>
/// Which template hotkeys are really registered. Precedence, as on the Mac: an app hotkey wins over the picker,
/// the picker over any template, an earlier usable template over a later one. Keys are compared in the
/// canonical form the caller supplies, never as raw strings.
/// </summary>
public static class TemplateHotkeys
{
    /// <summary>Name of the generic hotkey in <see cref="HotkeyOwner"/>.</summary>
    public const string PickerName = "Vorlage";

    /// <summary>Picker registered only when its key is set (canonical != null), at least one template is usable, and no app hotkey has that key.</summary>
    public static bool PickerIsActive(string? pickerHotkey, IReadOnlyList<DictationTemplate> templates,
        IReadOnlyList<(string Name, string Hotkey)> appHotkeys, Func<string, string?> canonical) =>
        Canon(pickerHotkey, canonical) != null
        && templates.Any(t => t.IsUsable)
        && PickerOwner(pickerHotkey, appHotkeys, canonical) == null;

    /// <summary>The app hotkey that owns the picker's key, or null.</summary>
    public static HotkeyOwner? PickerOwner(string? pickerHotkey, IReadOnlyList<(string Name, string Hotkey)> appHotkeys,
        Func<string, string?> canonical)
    {
        var key = Canon(pickerHotkey, canonical);
        return key == null ? null : AppOwner(key, appHotkeys, canonical);
    }

    /// <summary>
    /// Who else owns <paramref name="template"/>'s key: an app slot, then the active picker, then an earlier
    /// usable template in the list (by Id position). Null when the key is free or the template has none.
    /// </summary>
    public static HotkeyOwner? Owner(DictationTemplate template, IReadOnlyList<DictationTemplate> templates,
        IReadOnlyList<(string Name, string Hotkey)> appHotkeys, string? pickerHotkey, Func<string, string?> canonical)
    {
        var key = Canon(template.Hotkey, canonical);
        if (key == null) return null;
        if (AppOwner(key, appHotkeys, canonical) is { } app) return app;
        if (PickerIsActive(pickerHotkey, templates, appHotkeys, canonical) && Canon(pickerHotkey, canonical) == key)
            return new HotkeyOwner(HotkeyOwnerKind.Picker, PickerName);

        var own = IndexOf(template, templates);
        for (var i = 0; i < templates.Count && (own < 0 || i < own); i++)
        {
            var other = templates[i];
            if (other.IsUsable && Canon(other.Hotkey, canonical) == key)
                return new HotkeyOwner(HotkeyOwnerKind.Template, other.DisplayName);
        }
        return null;
    }

    /// <summary>Usable templates with a key and no <see cref="Owner"/>; Hotkey in the result is the canonical form.</summary>
    public static IReadOnlyList<(DictationTemplate Template, string Hotkey)> ActiveBindings(IReadOnlyList<DictationTemplate> templates,
        IReadOnlyList<(string Name, string Hotkey)> appHotkeys, string? pickerHotkey, Func<string, string?> canonical)
    {
        var result = new List<(DictationTemplate, string)>();
        foreach (var t in templates)
        {
            if (!t.IsUsable) continue;
            var key = Canon(t.Hotkey, canonical);
            if (key == null || Owner(t, templates, appHotkeys, pickerHotkey, canonical) != null) continue;
            result.Add((t, key));
        }
        return result;
    }

    private static string? Canon(string? stored, Func<string, string?> canonical) =>
        string.IsNullOrWhiteSpace(stored) ? null : canonical(stored);

    private static HotkeyOwner? AppOwner(string key, IReadOnlyList<(string Name, string Hotkey)> appHotkeys, Func<string, string?> canonical)
    {
        foreach (var (name, hotkey) in appHotkeys)
            if (Canon(hotkey, canonical) == key) return new HotkeyOwner(HotkeyOwnerKind.App, name);
        return null;
    }

    // -1 when the template is not in the list: then every listed template counts as earlier.
    private static int IndexOf(DictationTemplate template, IReadOnlyList<DictationTemplate> templates)
    {
        for (var i = 0; i < templates.Count; i++)
            if (templates[i].Id == template.Id) return i;
        return -1;
    }
}
