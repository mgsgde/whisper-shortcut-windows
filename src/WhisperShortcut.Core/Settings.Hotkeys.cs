namespace WhisperShortcut.Core;

/// <summary>One shortcut as stored: its key ("" = unassigned) and its on switch. A shortcut without a switch is always "on".</summary>
public sealed record HotkeyBinding(string Key, bool On)
{
    /// <summary>The key when the shortcut is on, otherwise "" (nothing to register, nothing taken).</summary>
    public string Active => On ? Key : "";
}

public sealed partial class Settings
{
    // Every fixed shortcut of HotkeySlots.All, keyed by action. Which actions exist, their JSON fields, defaults and labels:
    // the HotkeySlots table. Nothing here names a single shortcut.
    private readonly Dictionary<HotkeyAction, HotkeyBinding> _hotkeys =
        HotkeySlots.All.ToDictionary(x => x.Action, _ => new HotkeyBinding("", true));

    /// <summary>The stored binding of every fixed shortcut (one entry per row of <see cref="HotkeySlots.All"/>).</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> Hotkeys => _hotkeys;

    /// <summary>The stored key of <paramref name="action"/>, switched on or not ("" = unassigned).</summary>
    public string Key(HotkeyAction action) => _hotkeys[action].Key;

    /// <summary>Whether the shortcut is switched on (always true for one without a switch).</summary>
    public bool IsOn(HotkeyAction action) => _hotkeys[action].On;

    /// <summary>The key to register: the stored key when the shortcut is on, otherwise "".</summary>
    public string ActiveKey(HotkeyAction action) => _hotkeys[action].Active;

    public void SetKey(HotkeyAction action, string key) => _hotkeys[action] = _hotkeys[action] with { Key = key };

    public void SetOn(HotkeyAction action, bool on) => _hotkeys[action] = _hotkeys[action] with { On = on };
}
