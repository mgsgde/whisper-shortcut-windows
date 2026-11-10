namespace WhisperShortcut.Core;

/// <summary>One option of an enum-like setting: the value, its stored token (JSON, MCP) and the German label of the Settings window.</summary>
public sealed record Choice<T>(T Value, string Token, string Label, string? Explanation = null);

/// <summary>All options of one enum-like setting, in the order the Settings window lists them. The single place for tokens and labels.</summary>
public sealed class ChoiceSet<T>
{
    private readonly Choice<T> _fallback;

    public ChoiceSet(IReadOnlyList<Choice<T>> all, T fallback)
    {
        All = all;
        _fallback = all.First(c => EqualityComparer<T>.Default.Equals(c.Value, fallback));
    }

    public IReadOnlyList<Choice<T>> All { get; }

    /// <summary>The option for a stored token (trimmed, case-insensitive); null when missing or unknown.</summary>
    public Choice<T>? Find(string? token)
    {
        var t = token?.Trim();
        return string.IsNullOrEmpty(t) ? null : All.FirstOrDefault(c => string.Equals(c.Token, t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="token"/> names an option; otherwise <paramref name="value"/> is the fallback.</summary>
    public bool TryParse(string? token, out T value)
    {
        var c = Find(token);
        value = (c ?? _fallback).Value;
        return c != null;
    }

    /// <summary>The value of a stored token; the fallback for a missing or unknown one.</summary>
    public T Parse(string? token) => (Find(token) ?? _fallback).Value;

    private Choice<T> Of(T value) => All.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value)) ?? _fallback;

    public string Token(T value) => Of(value).Token;
    public string Label(T value) => Of(value).Label;
    public string? Explanation(T value) => Of(value).Explanation;
}

/// <summary>The choice tables of the enum-like settings. Settings, the MCP catalog and the Settings window all read these.</summary>
public static class Choices
{
    /// <summary>Provider. Unknown or missing = Gemini, the engine every old settings file used. The Local label is generic; each step words its own.</summary>
    public static readonly ChoiceSet<Engine> Engine = new(new[]
    {
        new Choice<Engine>(Core.Engine.Gemini, "gemini", "Gemini (Google)"),
        new Choice<Engine>(Core.Engine.OpenAI, "openai", "OpenAI"),
        new Choice<Engine>(Core.Engine.Local, "local", "Auf diesem PC"),
    }, Core.Engine.Gemini);

    /// <summary>Unknown or missing = Automatic.</summary>
    public static readonly ChoiceSet<PunctuationMode> Punctuation = new(new[]
    {
        new Choice<PunctuationMode>(PunctuationMode.Automatic, "automatic", "Automatisch"),
        new Choice<PunctuationMode>(PunctuationMode.SpokenOnly, "spokenOnly", "Nur gesprochen"),
    }, PunctuationMode.Automatic);

    /// <summary>Mac raw values "dictate" / "live"; anything else is the default.</summary>
    public static readonly ChoiceSet<DictateMode> DictateMode = new(new[]
    {
        new Choice<DictateMode>(Core.DictateMode.Dictate, "dictate", "Diktieren"),
        new Choice<DictateMode>(Core.DictateMode.Live, "live", "Live diktieren"),
    }, Core.DictateMode.Dictate);

    /// <summary>After auto-paste (Mac order, German labels from the Mac strings, explanations in du-form with Windows keys).</summary>
    public static readonly ChoiceSet<ClipboardAfterPaste> AfterPaste = new(new[]
    {
        new Choice<ClipboardAfterPaste>(ClipboardAfterPaste.KeepDictation, "keep", "Diktat behalten",
            "Der diktierte Text bleibt in der Zwischenablage, du kannst ihn mit Strg+V erneut einfügen."),
        new Choice<ClipboardAfterPaste>(ClipboardAfterPaste.RestorePrevious, "restore", "Vorherige wiederherstellen",
            "Legt 3 Sekunden nach dem Einfügen zurück, was du vorher kopiert hattest."),
        new Choice<ClipboardAfterPaste>(ClipboardAfterPaste.Clear, "clear", "Leeren",
            "Leert die Zwischenablage 3 Sekunden nach dem Einfügen, damit ein späteres Strg+V kein früheres Diktat einfügt. "
            + "„Letzte Transkription kopieren“ im Menü am Symbol funktioniert weiterhin."),
    }, ClipboardAfterPaste.KeepDictation);

    /// <summary>
    /// The after-paste mode of a settings file: its own field when it has one, otherwise the old switch "restoreClipboard"
    /// (true or missing = restore, which was the old default; false = keep the dictation).
    /// </summary>
    public static ClipboardAfterPaste AfterPasteFromFile(string? stored, bool? legacyRestoreClipboard) =>
        AfterPaste.TryParse(stored, out var mode) ? mode
        : legacyRestoreClipboard ?? true ? ClipboardAfterPaste.RestorePrevious : ClipboardAfterPaste.KeepDictation;
}
