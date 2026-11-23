namespace WhisperShortcut;

/// <summary>
/// „Problem melden …“ (Mac #549: one feedback path). The tray row, Einstellungen → Über, the error popups, the chat's
/// /feedback and MCP's report_problem all open <see cref="ProblemReportWindow"/>. This part keeps the results the window
/// offers (in memory only, like the recent texts: a dictation must not linger in a file) and builds the configuration
/// section of the report file.
/// </summary>
internal sealed partial class TrayApp
{
    private readonly Core.ResultHistory _results = new();

    /// <summary>Wires the window's two providers to this app (Mac: ProblemReport.candidatesProvider in MenuBarController).</summary>
    private void SetUpProblemReport()
    {
        ProblemReportWindow.CandidatesProvider = () => Core.ResultReport.Candidates(_results.Dictations, _settings.Glossary,
            _settings.OfflineMode, _results.LastPrompt, _results.LastReadAloud, Core.ReportConfigFingerprint.Current(_settings), DateTime.Now);
        ProblemReportWindow.ConfigurationProvider = BuildReportConfiguration;
        // Mac prunes once at launch too; off the UI thread, a slow disk must not delay the tray.
        _ = Task.Run(() => Core.ReportAttachment.PruneOld(DateTime.Now));
    }

    /// <summary>
    /// Settings, prompts, glossary and templates (Mac ConfigurationReport with every option on), then the end of the log:
    /// Windows has no hang reports, and the log is what explains a failure. No keys, no dictated text in either.
    /// </summary>
    private string BuildReportConfiguration()
    {
        var catalog = _mcp != null ? Core.Mcp.McpSettingsCatalog.Build(_settings, _mcp) : Array.Empty<Core.Mcp.McpSetting>();
        var env = new Core.ConfigurationReportEnvironment(UpdateService.CurrentVersion, Environment.OSVersion.Version.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        var configuration = Core.ConfigurationReport.Build(new Core.ConfigurationReportOptions(IncludeGlossary: true, IncludePrompts: true,
            IncludeTemplates: true), _settings, catalog, env);
        var log = Core.ProblemReport.LogSection(Core.ProblemReport.TailLog(Core.Log.DefaultPath, 80));
        return log.Length == 0 ? configuration : configuration + "\n\n" + log;
    }

    /// <summary>Mac reportLastResult: opens after the tray menu has closed, or the window lands behind the last app.</summary>
    private void OpenProblemReportFromTray() => _ui.Post(_ => ProblemReportWindow.Open(), null);

    /// <summary>Mac PopupNotificationWindow.reportProblemClicked: the error text prefilled, on general feedback.</summary>
    private static void ReportError(string title, string text)
    {
        var error = Core.FeedbackLinks.Truncated($"{title}: {text}", 500);
        ProblemReportWindow.Open(message: "Dieser Fehler ist aufgetreten:\n\n" + error, startOnGeneral: true);
    }

    /// <summary>The model a step ran with, as the report shows it ("Gemini · gemini-3.5-flash", "Parakeet (lokal)").</summary>
    private string ReportModel(Core.ModelSelection selection, string localName)
    {
        var (engine, model) = selection.Effective(_settings.OfflineMode);
        return engine == Core.Engine.Local ? $"{localName} (lokal)" : $"{engine} · {model}";
    }

    /// <summary>A Dictate Prompt answer for the report window (Mac LastResultStore.lastPrompt); only the latest is kept.</summary>
    private void RememberPromptResult(string? selection, string instruction, string response)
    {
        try
        {
            var (engine, _) = _settings.Prompt.Effective(_settings.OfflineMode);
            _results.SetPrompt(new Core.PromptResult(DateTime.Now, instruction,
                selection == null ? null : Core.FeedbackLinks.Truncated(selection, 500), response,
                ReportModel(_settings.Prompt, _settings.OfflineLlmId), engine.ToString(), _take?.Shot != null,
                HistoryFor(selection)?.Count ?? 0, _take?.QuickAction));
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: prompt result not remembered ({e.GetType().Name})");
        }
    }

    /// <summary>A finished dictation, live dictation or template run for the report window.</summary>
    private void RememberDictationResult(string text, string mode, double? seconds, string? templateName = null, string? raw = null,
        IReadOnlyList<string>? echoRemoved = null)
    {
        try
        {
            _results.AddDictation(new Core.DictationResult(DateTime.Now, text, mode,
                mode == "Dictate Live" ? "Parakeet (lokal)" : ReportModel(_settings.Dictate, "Parakeet"), templateName, seconds,
                raw, UpdateService.CurrentVersion, Core.ReportConfigFingerprint.Current(_settings), echoRemoved));
        }
        catch (Exception e)
        {
            Core.Log.Warn($"REPORT: result not remembered ({e.GetType().Name})"); // never fail a dictation over its report
        }
    }
}
