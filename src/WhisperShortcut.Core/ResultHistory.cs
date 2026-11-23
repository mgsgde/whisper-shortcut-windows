namespace WhisperShortcut.Core;

/// <summary>One finished dictation, kept in memory for "Problem melden".</summary>
public sealed record DictationResult(DateTime Ts, string Text, string Mode, string? Model, string? TemplateName,
    double? AudioSeconds, string? RawOutput, string AppVersion, string? ConfigHash, IReadOnlyList<string>? GlossaryEchoRemoved);

/// <summary>The last Dictate Prompt answer.</summary>
public sealed record PromptResult(DateTime Ts, string Instruction, string? SelectedText, string Response, string? Model,
    string? Provider, bool HadScreenshot, int HistoryTurns, string? QuickInstruction);

/// <summary>The last Read Aloud.</summary>
public sealed record ReadAloudResult(DateTime Ts, string Text, string? Model, string? Voice);

/// <summary>
/// The last results the user can report (Mac LastResultStore + TranscriptionHistoryStore). In memory only:
/// nothing is ever written to disk, so no dictation lingers in a file.
/// </summary>
public sealed class ResultHistory
{
    /// <summary>Most dictations kept.</summary>
    public const int Capacity = 10;

    private const int SelectedTextLimit = 500;

    private readonly object _gate = new();
    private readonly List<DictationResult> _dictations = new();
    private PromptResult? _prompt;
    private ReadAloudResult? _readAloud;

    /// <summary>Adds a dictation as the newest; the oldest beyond <see cref="Capacity"/> is dropped. Blank text is ignored.</summary>
    public void AddDictation(DictationResult r)
    {
        if (string.IsNullOrWhiteSpace(r.Text)) return;
        lock (_gate)
        {
            _dictations.Insert(0, r);
            if (_dictations.Count > Capacity) _dictations.RemoveRange(Capacity, _dictations.Count - Capacity);
        }
    }

    /// <summary>Keeps only this prompt result; the selected text is cut to 500 characters.</summary>
    public void SetPrompt(PromptResult r)
    {
        var cut = r.SelectedText is null ? null : FeedbackLinks.Truncated(r.SelectedText, SelectedTextLimit);
        lock (_gate) _prompt = r with { SelectedText = cut };
    }

    /// <summary>Keeps only this read-aloud result.</summary>
    public void SetReadAloud(ReadAloudResult r)
    {
        lock (_gate) _readAloud = r;
    }

    /// <summary>Snapshot, newest first.</summary>
    public IReadOnlyList<DictationResult> Dictations
    {
        get { lock (_gate) return _dictations.ToList(); }
    }

    /// <summary>The latest Dictate Prompt result, if any.</summary>
    public PromptResult? LastPrompt
    {
        get { lock (_gate) return _prompt; }
    }

    /// <summary>The latest Read Aloud result, if any.</summary>
    public ReadAloudResult? LastReadAloud
    {
        get { lock (_gate) return _readAloud; }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _dictations.Clear();
            _prompt = null;
            _readAloud = null;
        }
    }
}
