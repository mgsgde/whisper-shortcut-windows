namespace WhisperShortcut.Core;

/// <summary>What a global shortcut does. <see cref="Template"/> is a template's own key (not a slot of <see cref="HotkeySlots.All"/>).</summary>
public enum HotkeyAction
{
    Dictate, Prompt, Glossary, Meeting, LiveDictate, Screenshot, ReadAloud, Chat, OpenSettings, TemplatePicker, Template,
}

/// <summary>
/// One fixed global shortcut: the one place that says what it is called, which keys it has on a new install and on an install
/// from before the Mac digits, whether it ships on, and how it is read from and written to <see cref="Settings"/>.
/// <paramref name="Owner"/> is the German name as the UI says it (the Mac's wording). <paramref name="McpId"/> is the setting id
/// AI apps may change; null = fixed for AI apps (a deliberate choice per row). <paramref name="ConfigName"/> names it in the
/// read-only get_configuration list, which shows every row. <paramref name="JsonKey"/> / <paramref name="JsonOnKey"/> are the
/// settings.json fields (the Dto of <see cref="Settings"/> carries them; JsonOnKey is null for a key without an on switch: Live
/// Meeting, Vorlagen). <paramref name="BlankKeyIsMissing"/>: an empty stored key counts as a missing field on load.
/// </summary>
public sealed record HotkeySlot(HotkeyAction Action, string Owner, string NewDefault, string Legacy, bool NewEnabled, string? McpId,
    string ConfigName, string JsonKey, string? JsonOnKey, bool BlankKeyIsMissing = false)
{
    /// <summary>False for a key without an on switch (Live Meeting, Vorlagen): empty = unassigned.</summary>
    public bool HasSwitch => JsonOnKey != null;

    public string Get(Settings s) => s.Key(Action);

    public void Set(Settings s, string key) => s.SetKey(Action, key);

    public bool IsOn(Settings s) => s.IsOn(Action);

    public void SetOn(Settings s, bool on) => s.SetOn(Action, on);

    /// <summary>The stored key when the shortcut is on, otherwise "" (nothing to register, nothing taken).</summary>
    public string Active(Settings s) => s.ActiveKey(Action);
}

/// <summary>A key somebody holds right now. <paramref name="Key"/> is normalized when it can be, else as stored.</summary>
public sealed record HotkeyAssignment(string Owner, string Key, HotkeyAction Action, string? TemplateId, bool IsRightCtrlSwitch = false);

public enum HotkeyConflictKind
{
    /// <summary>Not a key combination at all.</summary>
    Invalid,
    /// <summary>A combination that would take a key away from every program (see <see cref="HotkeyText.IsSafe"/>).</summary>
    Unsafe,
    /// <summary>Another shortcut already has the key.</summary>
    Taken,
}

/// <summary><paramref name="Offender"/> is the later of the two shortcuts; <paramref name="Other"/> the earlier one (Taken only).</summary>
public sealed record HotkeyConflict(HotkeyConflictKind Kind, HotkeyAssignment Offender, HotkeyAssignment? Other)
{
    /// <summary>German display of the key ("Strg+Umschalt+D").</summary>
    public string Display => Mcp.HotkeyRules.Display(Offender.Key);
}

/// <summary>
/// Values to use instead of the stored ones, e.g. what the settings window's fields show now. <paramref name="Key"/> returns the
/// field value of a slot ("" = off), null = use the stored one.
/// </summary>
public sealed record HotkeyOverrides(Func<HotkeySlot, string?>? Key = null, IReadOnlyList<DictationTemplate>? Templates = null, bool? RightCtrl = null);

/// <summary>
/// Every fixed global shortcut in one table, and the one clash check. The settings file, the settings window, the tray
/// registration and the AI-app catalog all derive from it; a new shortcut is one row (plus its two Dto fields in Settings and its field in the settings window).
/// </summary>
public static class HotkeySlots
{
    /// <summary>Owner name of the right Ctrl key (the Mac's Fn key); it is not a slot, it follows <see cref="Settings.RightCtrlDictation"/>.</summary>
    public const string RightCtrlOwner = "Rechte Strg-Taste (Diktieren)";

    /// <summary>
    /// Display order of the settings window. New-install keys follow the Mac (ShortcutConfig.default): the same digit for the same
    /// function, with Alt as the Windows modifier (Ctrl+Alt is AltGr on German keyboards). Legacy = what installs before that ran
    /// on (Mac: legacyCommandDigit), pinned while the file lacks the pin flag. The chat did not exist then: an update adds it
    /// with its new key (switched off when the user already uses that key, see <see cref="Settings"/>).
    /// </summary>
    public static readonly IReadOnlyList<HotkeySlot> All = new[]
    {
        new HotkeySlot(HotkeyAction.Dictate, "Diktieren", "Alt+1", "Ctrl+Shift+D", true, "shortcuts.dictate",
            "dictate", "dictateHotkey", "dictateHotkeyEnabled", BlankKeyIsMissing: true),
        new HotkeySlot(HotkeyAction.Prompt, "Dictate Prompt", "Alt+2", "Ctrl+Shift+P", true, "shortcuts.dictate_prompt",
            "dictate_prompt", "promptHotkey", "promptHotkeyEnabled", BlankKeyIsMissing: true),
        new HotkeySlot(HotkeyAction.Screenshot, "Bildschirmfoto", "Alt+3", "", true, null,
            "screenshot", "screenshotHotkey", "screenshotHotkeyEnabled"),
        new HotkeySlot(HotkeyAction.ReadAloud, "Vorlesen", "Alt+4", "", true, "shortcuts.read_aloud",
            "read_aloud", "readAloudHotkey", "readAloudHotkeyEnabled"),
        // Ships off, like the Mac: the key is only the suggestion.
        new HotkeySlot(HotkeyAction.Glossary, "Ins Glossar", "Alt+7", "Ctrl+Shift+K", false, "shortcuts.add_to_glossary",
            "add_to_glossary", "glossaryHotkey", "glossaryHotkeyEnabled", BlankKeyIsMissing: true),
        new HotkeySlot(HotkeyAction.LiveDictate, "Live diktieren", "Alt+5", "", false, null,
            "live_dictate", "liveDictateHotkey", "liveDictateHotkeyEnabled"),
        // The Mac uses ⌥Space; on Windows Alt+Space opens every window's system menu, so the chat takes the free digit 6.
        // The chat did not exist in older files: a missing key stays the new one (Settings switches it off when an older file already uses it).
        new HotkeySlot(HotkeyAction.Chat, "Chat", "Alt+6", "Alt+6", true, null,
            "chat", "chatHotkey", "chatHotkeyEnabled"),
        new HotkeySlot(HotkeyAction.OpenSettings, "Einstellungen öffnen", "Alt+0", "", true, null,
            "open_settings", "openSettingsHotkey", "openSettingsHotkeyEnabled"),
        // Set from the tray menu; unassigned by default.
        new HotkeySlot(HotkeyAction.Meeting, "Live Meeting", "", "", false, null,
            "meeting", "meetingHotkey", null),
        // The generic key that applies the last-used template.
        new HotkeySlot(HotkeyAction.TemplatePicker, "Vorlagen", "", "", false, null,
            "template_picker", "templateHotkey", null),
    };

    /// <summary>The slot of <paramref name="action"/> (not <see cref="HotkeyAction.Template"/>).</summary>
    public static HotkeySlot Of(HotkeyAction action) => All.First(x => x.Action == action);

    /// <summary>Slots with an on switch: the ones the defaults, the legacy pin and the reset apply to.</summary>
    private static IEnumerable<HotkeySlot> Switchable => All.Where(x => x.HasSwitch);

    /// <summary>New-install keys and switches.</summary>
    public static void ApplyDefaults(Settings s)
    {
        foreach (var slot in Switchable)
        {
            slot.Set(s, slot.NewDefault);
            slot.SetOn(s, slot.NewEnabled);
        }
    }

    /// <summary>The keys an install had before the Mac-digit defaults; every switch on, as it behaved then.</summary>
    public static void ApplyLegacy(Settings s)
    {
        foreach (var slot in Switchable)
        {
            slot.Set(s, slot.Legacy);
            slot.SetOn(s, true);
        }
    }

    /// <summary>
    /// The one normalisation: "alt + 1" and "Alt+1" are the same key. The strict form (<see cref="Mcp.HotkeyRules.TryNormalize"/>)
    /// when the text has it, else modifiers in order plus the key as written (the settings window records any key; keys are
    /// compared ignoring case). Null when blank or not a combination.
    /// </summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (Mcp.HotkeyRules.TryNormalize(key, out var n)) return n;
        return HotkeyText.TryParse(key, out var mods, out var token) ? HotkeyText.Format(mods, token, german: false) : null;
    }

    /// <summary>True when both are keys and the same key in any spelling.</summary>
    public static bool SameKey(string? a, string? b) =>
        Normalize(a) is { } x && Normalize(b) is { } y && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    private static bool Blank(string? key) => string.IsNullOrWhiteSpace(key);

    private static HotkeyAssignment Make(string owner, string key, HotkeyAction action, string? templateId) =>
        new(owner, Normalize(key) ?? key.Trim(), action, templateId);

    /// <summary>
    /// Every key that is held right now, in the order of precedence: the slots in <see cref="All"/> order (switched-off ones
    /// hold nothing), then the usable templates' own keys (a template without instructions owns none, like the tray), then the
    /// right Ctrl key while it dictates. This is what the tray registers (a template key an earlier one holds is skipped there,
    /// see <see cref="TemplateHotkeys"/>).
    /// </summary>
    public static IEnumerable<HotkeyAssignment> Assignments(Settings s, HotkeyOverrides? o = null)
    {
        foreach (var slot in All)
        {
            var key = o?.Key?.Invoke(slot) ?? slot.Active(s);
            if (!Blank(key)) yield return Make(slot.Owner, key, slot.Action, null);
        }
        foreach (var t in o?.Templates ?? s.Templates)
            if (t.IsUsable && !Blank(t.Hotkey)) yield return Make($"Vorlage „{t.DisplayName}“", t.Hotkey, HotkeyAction.Template, t.Id);
        if (o?.RightCtrl ?? s.RightCtrlDictation)
            yield return new HotkeyAssignment(RightCtrlOwner, HotkeyText.RightCtrl, HotkeyAction.Dictate, null, IsRightCtrlSwitch: true);
    }

    /// <summary>
    /// The first problem in <see cref="Assignments"/> order, or null: a key that is not a combination (Invalid), not safe (Unsafe),
    /// or already held by an earlier one (Taken; Offender = the later, Other = the earlier). A pair clashes whichever way round.
    /// <paramref name="focus"/> limits the report to problems that involve a matching assignment (AI apps only answer for the keys they
    /// change); <paramref name="takenOnly"/> skips Invalid and Unsafe.
    /// </summary>
    public static HotkeyConflict? Check(Settings s, HotkeyOverrides? o = null, Func<HotkeyAssignment, bool>? focus = null, bool takenOnly = false)
    {
        bool Relevant(HotkeyAssignment? a) => focus == null || (a != null && focus(a));
        var seen = new Dictionary<string, HotkeyAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Assignments(s, o))
        {
            if (Normalize(a.Key) is not { } key
                || !HotkeyText.TryParse(key, out var mods, out var token) || !HotkeyText.IsSafe(mods, token))
            {
                if (!takenOnly && Relevant(a))
                    return new HotkeyConflict(Normalize(a.Key) == null ? HotkeyConflictKind.Invalid : HotkeyConflictKind.Unsafe, a, null);
                continue;
            }
            if (seen.TryGetValue(key, out var other))
            {
                if (Relevant(a) || Relevant(other)) return new HotkeyConflict(HotkeyConflictKind.Taken, a, other);
                continue;
            }
            seen[key] = a;
        }
        return null;
    }

    /// <summary>True when the key of <paramref name="action"/> is also held by another shortcut.</summary>
    public static bool Clashes(Settings s, HotkeyAction action)
    {
        var key = Of(action).Get(s);
        return !Blank(key) && Assignments(s).Any(a => a.Action != action && SameKey(a.Key, key));
    }
}
