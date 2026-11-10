namespace WhisperShortcut;

/// <summary>What the settings window needs from one setting (or group of controls that is saved together).</summary>
internal interface IBinding
{
    /// <summary>The control shows something other than what it was loaded or last synced with.</summary>
    bool Edited { get; }

    /// <summary>
    /// Reads the control once: true when it is <see cref="Edited"/>; <paramref name="moved"/> says whether it changed since the
    /// previous call (the window saves once nothing moved for a while).
    /// </summary>
    bool Poll(out bool moved);

    /// <summary>The window loaded: what the control shows now is the baseline, what the settings hold now is what it knows of them.</summary>
    void Rebase(Core.Settings s);

    /// <summary>The settings may have been changed from outside: an untouched control shows the new value; an edited one keeps the edit.</summary>
    void Pull(Core.Settings s);

    /// <summary>Writes the control into <paramref name="s"/> when it was edited (and its gate lets it). True when it wrote.</summary>
    bool Apply(Core.Settings s);

    /// <summary>The settings were saved with what <see cref="Apply"/> wrote: the control is the baseline now.</summary>
    void Commit(Core.Settings s);
}

/// <summary>
/// One setting of the settings window: how to read it from the settings, write it back, read and show it in the control.
/// The window writes only the bindings the user edited, so a value that changed from outside meanwhile (tray, playback bar,
/// AI app, glossary key) is never overwritten by a stale control; <see cref="Pull"/> brings such a value into the control.
/// </summary>
/// <param name="get">The value in the settings, as a snapshot (a list is copied: the settings change it in place).</param>
/// <param name="set">Writes the control's value into the settings.</param>
/// <param name="read">The control's value (a fresh snapshot, never the live list).</param>
/// <param name="show">Puts a value into the control.</param>
/// <param name="eq">How two values compare (default: <see cref="EqualityComparer{T}.Default"/>; lists need their own).</param>
/// <param name="merge">
/// For a value that grows from outside (the glossary): called with (value, what we knew, what the settings hold now) when the
/// control is edited, so a term added meanwhile is not lost. Null = the edit simply wins.
/// </param>
/// <param name="gate">A condition for the whole write (all shortcuts valid). While false the binding stays edited.</param>
/// <param name="gateApply">False = write anyway, but stay edited while the gate is false (templates: names yes, keys wait).</param>
internal sealed class Binding<T>(Func<Core.Settings, T> get, Action<Core.Settings, T> set, Func<T> read, Action<T> show,
    IEqualityComparer<T>? eq = null, Func<T, T, T, T>? merge = null, Func<bool>? gate = null, bool gateApply = true) : IBinding
{
    private readonly IEqualityComparer<T> _eq = eq ?? EqualityComparer<T>.Default;
    private T _baseline = default!; // what the control showed when loaded / last synced
    private T _known = default!;    // what the settings held when we last looked
    private T _seen = default!;     // what the control showed at the last Poll

    /// <summary>What the control showed when it was loaded, synced or last saved.</summary>
    public T Baseline => _baseline;

    public bool Edited => !_eq.Equals(read(), _baseline);

    public bool Poll(out bool moved)
    {
        var value = read();
        moved = !_eq.Equals(value, _seen);
        _seen = value;
        return !_eq.Equals(value, _baseline);
    }

    public void Rebase(Core.Settings s)
    {
        _baseline = _seen = read();
        _known = get(s);
    }

    public void Pull(Core.Settings s)
    {
        var current = get(s);
        if (_eq.Equals(current, _known)) return;
        var previous = _known;
        _known = current;
        if (!Edited)
        {
            show(current);
            _baseline = _seen = read(); // what the control makes of it (a combo without the entry keeps its own)
        }
        else if (merge != null)
        {
            var edited = read();
            _baseline = merge(_baseline, previous, current);
            show(merge(edited, previous, current));
            _seen = read();
        }
    }

    public bool Apply(Core.Settings s)
    {
        if (!Edited || (gateApply && gate != null && !gate())) return false;
        set(s, read());
        return true;
    }

    public void Commit(Core.Settings s)
    {
        if (gate != null && !gate()) return;
        _baseline = _seen = read();
        _known = get(s);
    }
}
