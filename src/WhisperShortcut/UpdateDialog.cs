using System.Runtime.InteropServices;
using System.Text;

namespace WhisperShortcut;

/// <summary>
/// "Version X ist da": the release notes, and the one button that downloads, checks the signature and
/// swaps the exe. Opened from the tray notice, the tray menu or Einstellungen → Info.
/// </summary>
internal sealed class UpdateDialog : Form
{
    private readonly UpdateService _service;
    private readonly Core.UpdateInfo _update;
    private readonly ThemedLabel _status;
    private readonly Bar _progress;
    private readonly FluentButton _install;
    private readonly FluentButton _later;
    private CancellationTokenSource? _cts;

    public UpdateDialog(UpdateService service, Core.UpdateInfo update)
    {
        _service = service;
        _update = update;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut – Update";
        Font = Theme.Body;
        Icon = TrayIcons.App;
        ClientSize = new Size(560, 500);
        MinimumSize = new Size(460, 400);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 20, 24, 18) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- head: icon, "Version X ist da", what is installed now ---
        var head = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 14) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.Controls.Add(new PictureBox
        {
            Image = new Icon(TrayIcons.App, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(44, 44), Margin = new Padding(0, 0, 14, 0),
        }, 0, 0);
        var names = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        names.Controls.Add(new ThemedLabel { Text = $"Version {update.Version} ist da", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Margin = new Padding(0) });
        names.Controls.Add(new ThemedLabel { Text = $"Installiert ist Version {UpdateService.CurrentVersion}.", Tone = Tone.Secondary, AutoSize = true, Margin = new Padding(0, 2, 0, 0) });
        head.Controls.Add(names, 1, 0);
        root.Controls.Add(head);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- release notes ---
        root.Controls.Add(new ThemedLabel { Text = "Was ist neu", Font = Theme.Section, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var notes = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, TabStop = false,
            Text = PlainNotes(update.Notes), Font = Theme.Body, AccessibleName = "Änderungen in dieser Version",
        };
        var notesFrame = new FieldFrame(notes, multiline: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        root.Controls.Add(notesFrame);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // --- what happens on install, then progress and errors in the same line ---
        _status = new ThemedLabel { Text = InstallHint(), Tone = Tone.Secondary, Font = Theme.Small, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        _status.MaximumSize = new Size(ClientSize.Width - 48, 0);
        root.Controls.Add(_status);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _progress = new Bar(level: false) { Visible = false, Margin = new Padding(0, 0, 0, 12) };
        root.Controls.Add(_progress);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- buttons: page link on the left, Später / Jetzt installieren on the right ---
        var buttons = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var page = new FluentButton("Download-Seite") { Margin = new Padding(0), AccessibleName = "Download-Seite im Browser öffnen" };
        page.Click += (_, _) => OpenUrl(_update.Url);
        buttons.Controls.Add(page, 0, 0);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _later = new FluentButton("Später") { MinimumSize = new Size(104, 32) };
        _later.Click += (_, _) => Close();
        _install = new FluentButton("Jetzt installieren", accent: true) { MinimumSize = new Size(148, 32) };
        _install.Click += async (_, _) => await InstallAsync();
        right.Controls.Add(_later);
        right.Controls.Add(_install);
        buttons.Controls.Add(right, 1, 0);
        root.Controls.Add(buttons);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(root);
        AcceptButton = _install;
        CancelButton = _later;
        ResumeLayout(true);

        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) => _install.Focus();
        Resize += (_, _) => _status.MaximumSize = new Size(Math.Max(100, ClientSize.Width - LogicalToDeviceUnits(48)), 0);
        // Closing during the download cancels it; nothing is half-installed because the swap only starts afterwards.
        FormClosing += (_, _) => _cts?.Cancel();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    /// <summary>The VM test's stand-in for a click on "Jetzt installieren".</summary>
    public void InstallNow()
    {
        if (!IsDisposed && _install.Enabled) _install.PerformClick();
    }

    private static string InstallHint()
    {
        var hint = "Lädt etwa 60 MB, prüft die Signatur und startet WhisperShortcut neu. Einstellungen, Schlüssel und Glossar bleiben.";
        if (!UpdateInstaller.CanWriteNextToExe())
            hint += " Weil die App unter „Programme“ liegt, fragt Windows gleich nach Administratorrechten.";
        return hint;
    }

    private async Task InstallAsync()
    {
        if (!_service.IsIdle)
        {
            SetStatus("Erst die laufende Aufnahme beenden und warten, bis der Text da ist. Dann noch einmal klicken.", Tone.Caution);
            return;
        }
        _install.Enabled = false;
        _install.Text = "Wird geladen …";
        _progress.Value = 0;
        _progress.Visible = true;
        SetStatus("Wird heruntergeladen …", Tone.Secondary);
        _cts = new CancellationTokenSource();
        string exe;
        try
        {
            // No overall timeout: 60 MB on a slow practice line can take minutes. A stalled connection
            // ends through HttpClient's per-request timeout; closing the window cancels.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var progress = new Progress<double>(p =>
            {
                if (IsDisposed) return;
                _progress.Value = (float)p;
                SetStatus($"Wird heruntergeladen … {p * 100:0} %", Tone.Secondary);
            });
            exe = await Core.UpdateDownloader.DownloadAsync(http, _update, RuntimeInformation.ProcessArchitecture,
                UpdateInstaller.StagingDir, progress, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Core.Log.Info("Update download cancelled");
            return;
        }
        catch (OperationCanceledException)
        {
            Core.Log.Error("Update download timed out");
            Failed("Der Download hat zu lange gedauert. Bitte später noch einmal versuchen.");
            return;
        }
        catch (Core.UpdateException e)
        {
            Core.Log.Error($"Update download failed: {e.Message}");
            Failed(e.Message);
            return;
        }
        catch (Exception e)
        {
            // Disk full, staging folder locked by a virus scanner …: say so instead of crashing the tray app.
            Core.Log.Error("Update download failed", e);
            Failed("Die neue Version ließ sich nicht speichern. Details stehen im Protokoll.");
            return;
        }
        if (IsDisposed) return;

        // The download took a while: a recording may have started meanwhile.
        if (!_service.IsIdle)
        {
            Failed("Während einer Aufnahme wird nicht neu gestartet. Nach der Aufnahme noch einmal klicken, der Download geht dann schnell.");
            return;
        }
        _progress.Value = 1;
        _install.Text = "Wird installiert …";
        SetStatus("Wird installiert, WhisperShortcut startet gleich neu …", Tone.Secondary);
        if (await UpdateInstaller.ApplyAsync(exe))
        {
            _service.QuitForRestart();
            return;
        }
        Failed(UpdateInstaller.CanWriteNextToExe()
            ? "Die neue Version ließ sich nicht einsetzen. Details stehen im Protokoll. Alternativ über „Download-Seite“ von Hand ersetzen."
            : "Ohne Administratorrechte lässt sich die App unter „Programme“ nicht ersetzen. Bitte jemanden mit Admin-Konto "
              + "„Jetzt installieren“ bestätigen lassen, oder die exe über „Download-Seite“ von Hand ersetzen.");
    }

    private void Failed(string message)
    {
        if (IsDisposed) return;
        _progress.Visible = false;
        _install.Enabled = true;
        _install.Text = "Erneut versuchen";
        SetStatus(message, Tone.Caution);
    }

    private void SetStatus(string text, Tone tone)
    {
        _status.Text = text;
        _status.Tone = tone;
    }

    /// <summary>
    /// The GitHub release text is Markdown written for the download page: bold headings and "- " lists
    /// become plain lines with bullets; the lines about which ZIP to pick and the build hash are dropped.
    /// </summary>
    internal static string PlainNotes(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "Keine Beschreibung zu dieser Version.";
        var sb = new StringBuilder();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Build:", StringComparison.Ordinal) || line.Contains("ANLEITUNG.txt", StringComparison.Ordinal)) continue;
            line = line.Replace("**", "");
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "•  " + line[2..];
            else if (line.StartsWith('#')) line = line.TrimStart('#', ' ');
            sb.Append(line).Append("\r\n");
        }
        return sb.ToString().Trim();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error($"Opening the release page failed: {e.GetType().Name}"); }
    }
}
