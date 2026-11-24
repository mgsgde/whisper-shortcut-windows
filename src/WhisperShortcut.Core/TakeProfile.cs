namespace WhisperShortcut.Core;

/// <summary>The hotkey family of a take: what the engines, the overlay and "same key stops" see. Live is Dictate.</summary>
public enum HotkeyFamily { Dictate, Prompt, Template }

/// <summary>
/// What one kind of recording is and does, in one row (replaces the old Mode enum, the Mode⇄kind switches and the
/// per-branch decisions in StartRecording). <see cref="Kind"/> stays the persisted name (<see cref="RecordingKind"/>:
/// crash checkpoints, history); <see cref="Name"/> is the label of the log lines ("Dictate", "Prompt", "Template").
/// </summary>
public sealed record TakeProfile(
    RecordingKindName Kind,
    HotkeyFamily Family,
    bool CopiesSelection,
    bool UsesTextEngine,
    TimeSpan Limit,
    bool TakesScreenshot,
    bool OffersQuickActions)
{
    /// <summary>Dictate, Live and Template are not bound by the inline-audio limit of Dictate Prompt.</summary>
    public static readonly TimeSpan DictateLimit = TimeSpan.FromMinutes(30);

    /// <summary>Dictate Prompt sends the audio inline to Gemini.</summary>
    public static readonly TimeSpan PromptLimit = TimeSpan.FromMinutes(5);

    public static readonly TakeProfile Dictate = new(RecordingKindName.Dictate, HotkeyFamily.Dictate,
        CopiesSelection: false, UsesTextEngine: false, DictateLimit, TakesScreenshot: false, OffersQuickActions: false);

    public static readonly TakeProfile Prompt = new(RecordingKindName.Prompt, HotkeyFamily.Prompt,
        CopiesSelection: true, UsesTextEngine: true, PromptLimit, TakesScreenshot: true, OffersQuickActions: true);

    // WHY the long limit: the template path transcribes in chunks and sends text.
    public static readonly TakeProfile Template = new(RecordingKindName.Template, HotkeyFamily.Template,
        CopiesSelection: true, UsesTextEngine: true, DictateLimit, TakesScreenshot: true, OffersQuickActions: false);

    public static readonly TakeProfile Live = Dictate with { Kind = RecordingKindName.Live };

    /// <summary>One row per recording kind.</summary>
    public static IReadOnlyList<TakeProfile> All { get; } = new[] { Dictate, Prompt, Template, Live };

    public static TakeProfile Of(RecordingKindName kind) => kind switch
    {
        RecordingKindName.Prompt => Prompt,
        RecordingKindName.Template => Template,
        RecordingKindName.Live => Live,
        _ => Dictate,
    };

    /// <summary>The plain row of a hotkey family (Live is only started by its own key or the Diktieren mode).</summary>
    public static TakeProfile ForFamily(HotkeyFamily family) => family switch
    {
        HotkeyFamily.Prompt => Prompt,
        HotkeyFamily.Template => Template,
        _ => Dictate,
    };

    /// <summary>The label in the log lines.</summary>
    public string Name => Family.ToString();
}
