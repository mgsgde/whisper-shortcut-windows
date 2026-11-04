namespace WhisperShortcut;

/// <summary>
/// A window that shows without the per-container layout cascade of WinForms, and that can be built in advance
/// (<see cref="Prewarm"/>) so its hotkey only has to make it visible. The settings window, the chat and the
/// welcome tour use it: their nested auto-size tables made every Show take seconds on a small PC.
/// </summary>
internal class FastShowForm : Form
{
    private bool _prewarming;

    /// <summary>
    /// Builds the window in advance, invisible and without the focus, so the hotkey shows it at once (the Mac's
    /// windows appear instantly; building and the first layout take seconds here). The first Show pays the
    /// handle creation and layout; the real Show later only makes it visible.
    /// </summary>
    internal void Prewarm()
    {
        _prewarming = true;
        Opacity = 0;
        try
        {
            Show();
            // The expensive part is the first layout and paint, which run from the message queue, not in Show.
            Update();
            Application.DoEvents();
            Hide();
        }
        finally { Opacity = 1; _prewarming = false; }
    }

    /// <summary>True while <see cref="Prewarm"/> shows the window: a window built in advance must not take the focus.</summary>
    protected bool Prewarming => _prewarming;

    protected override bool ShowWithoutActivation => _prewarming;

    /// <summary>
    /// WHY suspended while becoming visible: on Show every container gets OnVisibleChanged and lays itself out,
    /// which measures all labels of the nested auto-size tables again (dotnet-stack: Control.OnParentVisibleChanged
    /// → PerformLayout → TableLayout.GetPreferredSize → Label.GetPreferredSize). About sixty of those made every
    /// Show of the settings take 5 s on Magnus's PC; suspended down to the leaves and resumed once, it takes about 0.2 s.
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!value || Visible) { base.SetVisibleCore(value); return; }
        LayoutBatch.Run(this, () => base.SetVisibleCore(value));
    }
}

/// <summary>Many layout changes at once, laid out once at the end instead of once per change and container.</summary>
internal static class LayoutBatch
{
    /// <summary>Runs <paramref name="change"/> with every container under <paramref name="root"/> suspended, then lays out once, leaves first.</summary>
    public static void Run(Control root, Action change)
    {
        var containers = new List<Control>();
        void Collect(Control c)
        {
            // Not c.Visible: under a hidden parent every child reports false.
            if (c.Controls.Count == 0) return;
            containers.Add(c);
            foreach (Control child in c.Controls) Collect(child);
        }
        Collect(root);
        foreach (var c in containers) c.SuspendLayout();
        try { change(); }
        finally
        {
            for (int i = containers.Count - 1; i >= 0; i--) containers[i].ResumeLayout(true);
        }
    }
}
