namespace WhisperShortcut;

/// <summary>
/// The tray submenu „Feedback senden“ (Mac MenuBarController: „Problem mit letztem Diktat melden…“, then WhatsApp, Email,
/// GitHub Issue via <c>FeedbackLinks</c>). Windows keeps its problem report (version, models, log, never the dictation)
/// and adds „Protokoll öffnen“, which the report points to.
/// </summary>
internal sealed partial class TrayApp
{
    private void AddFeedbackItems(ToolStripMenuItem feedback)
    {
        feedback.DropDownItems.Add(new ToolStripMenuItem("Problem melden …", null, (_, _) => ReportProblemFromTray()));
        feedback.DropDownItems.Add(new ToolStripSeparator());
        feedback.DropDownItems.Add(new ToolStripMenuItem("WhatsApp", null, (_, _) => OpenFeedback(Core.FeedbackChannel.WhatsApp))
            { ToolTipText = "Dem Entwickler auf WhatsApp schreiben" });
        feedback.DropDownItems.Add(new ToolStripMenuItem("E-Mail", null, (_, _) => OpenFeedback(Core.FeedbackChannel.Email))
            { ToolTipText = "E-Mail an den Entwickler" });
        feedback.DropDownItems.Add(new ToolStripMenuItem("GitHub-Issue", null, (_, _) => OpenFeedback(Core.FeedbackChannel.GitHub))
            { ToolTipText = "GitHub-Issue anlegen" });
        feedback.DropDownItems.Add(new ToolStripSeparator());
        feedback.DropDownItems.Add(new ToolStripMenuItem("Protokoll öffnen", null, (_, _) => OpenLog()));
    }

    /// <summary>Mac: the row opens the report directly, not just the settings window.</summary>
    private void ReportProblemFromTray()
    {
        ShowSettingsPage(SettingsForm.InfoPage);
        _settingsForm?.ReportProblem();
    }

    /// <summary>Opens the channel with the Mac's prefilled message; nothing is sent until the user presses send there.</summary>
    internal static bool OpenFeedback(Core.FeedbackChannel channel, string? context = null)
    {
        var url = Core.FeedbackLinks.Url(channel, context, Application.ProductVersion.Split('+')[0], Environment.OSVersion.Version.ToString());
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url.AbsoluteUri, UseShellExecute = true });
            Core.Log.Info($"Feedback opened: {channel}");
            return true;
        }
        catch (Exception e)
        {
            Core.Log.Error($"Feedback {channel} could not be opened", e);
            return false;
        }
    }
}
