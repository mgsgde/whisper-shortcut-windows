using System.Globalization;

namespace WhisperShortcut.Core;

/// <summary>How a setting is stored and what kind of value it is.</summary>
public enum SettingType { Flag, Text, Number, Choice, ModelStep, List, Hotkey, Secret, Internal }

/// <summary>What the settings window uses to edit a setting. None = no control of its own (set elsewhere, or not user-facing).</summary>
public enum ControlKind { None, Toggle, Choice, Text, Hotkey, ModelStep, List }

/// <summary>The settings page that holds the control (Internal = not on a page).</summary>
public enum SettingsPage { General, Dictate, DictatePrompt, Templates, ReadAloud, Chat, Meetings, Privacy, Internal }

/// <summary>
/// One setting, described once (W11). The table behind <see cref="Settings.Descriptors"/> is where a setting is declared:
/// persistence (JSON keys, missing-field rule), its new-install value (<see cref="Default"/>, which also seeds a fresh
/// <see cref="Settings"/>), the control kind and page of the settings window, the AI-app ids that may write it
/// (<see cref="McpIds"/>, empty = not writable by AI apps) and its line in the tester report (<see cref="ReportTitle"/>, null =
/// not in the report). A secret (API key) can carry neither an MCP id nor a report title: the constructor refuses it.
/// </summary>
public sealed class SettingDescriptor
{
    private readonly Func<Settings, object?>? _get;
    private readonly Action<Settings, object?>? _set;
    private readonly Func<Settings, string>? _show;

    internal SettingDescriptor(string jsonKey, IReadOnlyList<string> jsonKeys, SettingType type, object? @default,
        IReadOnlyList<string> mcpIds, string? reportTitle, ControlKind control, SettingsPage page,
        Func<Settings, object?>? get, Action<Settings, object?>? set, Func<Settings, string>? show)
    {
        if (type == SettingType.Secret && (mcpIds.Count > 0 || reportTitle != null))
            throw new ArgumentException($"{jsonKey}: a secret never goes to AI apps or into a report");
        JsonKey = jsonKey; JsonKeys = jsonKeys; Type = type; Default = @default; McpIds = mcpIds; ReportTitle = reportTitle;
        Control = control; Page = page; _get = get; _set = set; _show = type == SettingType.Secret ? null : show; // a secret has no way to be shown
    }

    /// <summary>The setting's own JSON field (the first of <see cref="JsonKeys"/>); unique over the table.</summary>
    public string JsonKey { get; }

    /// <summary>Every settings.json field this setting owns (a model step has three, a shortcut its key and its on switch).</summary>
    public IReadOnlyList<string> JsonKeys { get; }

    /// <summary>Kind of value.</summary>
    public SettingType Type { get; }

    /// <summary>The value of a fresh install; null for bookkeeping that is no Settings property.</summary>
    public object? Default { get; }

    /// <summary>Ids of the AI-app settings this one backs (<see cref="Mcp.McpSettingsCatalog"/>); empty = AI apps cannot change it.</summary>
    public IReadOnlyList<string> McpIds { get; }

    /// <summary>The first <see cref="McpIds"/> entry, or null.</summary>
    public string? McpId => McpIds.Count > 0 ? McpIds[0] : null;

    /// <summary>German title in the tester report; null = not listed (secrets, free text, bookkeeping).</summary>
    public string? ReportTitle { get; }

    /// <summary>The kind of control the settings window edits it with.</summary>
    public ControlKind Control { get; }

    /// <summary>The page of that control.</summary>
    public SettingsPage Page { get; }

    /// <summary>True for an API key.</summary>
    public bool Secret => Type == SettingType.Secret;

    /// <summary>The value on <paramref name="s"/>, boxed (null for bookkeeping that is no Settings property).</summary>
    public object? Read(Settings s) => _get?.Invoke(s);

    /// <summary>The value on <paramref name="s"/> as <typeparamref name="T"/>.</summary>
    public T Get<T>(Settings s) => (T)_get!(s)!;

    /// <summary>Writes <paramref name="value"/> onto <paramref name="s"/>.</summary>
    public void Set<T>(Settings s, T value) => _set!(s, value);

    /// <summary>The value as the report shows it ("An"/"Aus", a token, a number); null when this setting has no report title.</summary>
    public string? Show(Settings s) => ReportTitle == null || _show == null ? null : _show(s);

    /// <summary>Formats a plain value the way the report does (bool An/Aus, empty text, invariant numbers, list sizes).</summary>
    internal static string Format(object? v) => v switch
    {
        null => "(leer)",
        bool b => b ? "An" : "Aus",
        string t => t.Length == 0 ? "(leer)" : t,
        System.Collections.ICollection c => c.Count.ToString(CultureInfo.InvariantCulture) + " Einträge",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

public sealed partial class Settings
{
    private static IReadOnlyList<SettingDescriptor>? _descriptors;

    /// <summary>Every persisted setting, once: the table that persistence, the settings window, the AI-app catalog and the report derive from.</summary>
    public static IReadOnlyList<SettingDescriptor> Descriptors => _descriptors ??=
        Fields.Concat(ExplicitFields).Select(f => f.Descriptor).OfType<SettingDescriptor>().ToList();

    /// <summary>The descriptor whose <see cref="SettingDescriptor.JsonKey"/> is <paramref name="jsonKey"/>.</summary>
    public static SettingDescriptor Descriptor(string jsonKey) =>
        Descriptors.FirstOrDefault(d => d.JsonKey == jsonKey) ?? throw new KeyNotFoundException($"No setting descriptor \"{jsonKey}\"");
}
