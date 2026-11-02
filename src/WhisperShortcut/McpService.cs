using System.Diagnostics;
using System.Net.Sockets;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut;

/// <summary>
/// The local MCP server inside the app (Settings → „Claude &amp; KI-Apps“): starts and stops the loopback
/// listener, runs every tool call on the UI thread (the one thread that touches <see cref="Core.Settings"/>),
/// shows the approval windows and writes the client configs behind the „Verbinden“ buttons.
/// Off by default, and off while offline mode is on: an AI app reading the glossary would send it to its provider.
/// </summary>
internal sealed class McpService : IMcpHost, IDisposable
{
    // How long a request waits for the UI thread. A modal dialog still pumps messages, so this only
    // runs out when the UI thread is really stuck.
    private static readonly TimeSpan MainWait = TimeSpan.FromSeconds(10);

    private readonly Core.Settings _settings;
    private readonly SynchronizationContext _ui;
    private readonly Func<bool> _processing;
    private readonly Func<bool> _settingsDirty;
    private readonly Action _afterApply;
    private readonly McpToolContext _context;
    private readonly McpDispatcher _dispatcher;
    private readonly Dictionary<string, McpApprovalDialog> _dialogs = new();
    private LoopbackHttpServer? _server;
    private string? _failure;

    /// <summary>Server, keys, access level and the clients that connected.</summary>
    public McpConfig Config { get; }

    /// <summary>What AI apps changed directly or through an accepted proposal, with Undo.</summary>
    public McpChangeLog ChangeLog { get; }

    /// <summary>Changes waiting for the user's „Übernehmen“.</summary>
    public McpProposalStore Proposals { get; }

    /// <summary>Anything the settings page shows changed (status, clients, changes, proposals). Raised on the UI thread.</summary>
    public event Action? Changed;

    /// <param name="processing">True while a recording or its processing runs: writes wait until it is done.</param>
    /// <param name="settingsDirty">True while the settings window has unsaved edits: a write would be overwritten by its Save.</param>
    /// <param name="afterApply">Settings were changed and saved: re-register hotkeys, refresh the open window.</param>
    public McpService(Core.Settings settings, SynchronizationContext ui, Func<bool> processing, Func<bool> settingsDirty, Action afterApply)
    {
        _settings = settings;
        _ui = ui;
        _processing = processing;
        _settingsDirty = settingsDirty;
        _afterApply = afterApply;
        Config = McpConfig.Load(McpConfig.DefaultPath, new DpapiProtector());
        ChangeLog = new McpChangeLog(McpChangeLog.DefaultPath);
        Proposals = new McpProposalStore(McpProposalStore.DefaultPath);
        var catalog = McpSettingsCatalog.Build(settings, this);
        var applier = new McpConfigApplier(settings, ChangeLog, catalog, this, () => Core.PromptHistory.Load(Core.PromptHistory.DefaultPath));
        Proposals.Applier = applier;
        _context = new McpToolContext(settings, this, Config, Proposals, ChangeLog, applier);
        var configTools = new McpConfigTools(_context);
        // Meeting tools come as one more provider here; the existing ones stay as they are.
        _dispatcher = new McpDispatcher(new IMcpToolProvider[] { configTools, new McpSettingsTools(_context) }, AppVersion, configTools)
        {
            OnInitialize = (name, version, access) => Config.RecordClient(name, version, access, DateTime.UtcNow),
            RunOnMain = RunOnMain,
        };
        Config.Changed += RaiseChanged;
        ChangeLog.Changed += RaiseChanged;
        Proposals.Changed += RaiseChanged;
    }

    // ---------------- state ----------------

    /// <summary>The address the AI apps call.</summary>
    public string EndpointUrl => $"http://127.0.0.1:{_server?.Port ?? Config.Port}/mcp";

    /// <summary>True while the listener is up.</summary>
    public bool Running => _server != null;

    /// <summary>One German sentence for the settings page, and how to colour it.</summary>
    public (string Text, Tone Tone) Status
    {
        get
        {
            if (!Config.Enabled)
                return ("Aus. Eingeschaltet wartet WhisperShortcut auf diesem PC auf Claude; aus dem Netzwerk ist es nicht erreichbar.", Tone.Secondary);
            if (_settings.OfflineMode)
                return ("Pausiert, solange der Offline-Modus an ist: Was Claude liest, ginge an Anthropic.", Tone.Caution);
            if (_failure != null) return (_failure, Tone.Bad);
            if (_server != null)
                return ($"✓ Bereit auf diesem PC (Port {_server.Port}). Nur Apps mit dem Zugangsschlüssel kommen herein.", Tone.Good);
            return ("Startet …", Tone.Secondary);
        }
    }

    /// <summary>The key handed to apps by „Verbinden“: the read-only one while the user allows only reading.</summary>
    public string KeyForClients => Config.AccessLevel == McpAccessLevel.Read ? Config.ReadToken : Config.WriteToken;

    // ---------------- lifecycle ----------------

    /// <summary>At app start.</summary>
    public void StartIfEnabled() => Sync();

    /// <summary>The switch on the settings page.</summary>
    public void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save();
        Core.Log.Info($"MCP: server {(enabled ? "on" : "off")}");
        Sync();
    }

    /// <summary>Read / suggest / apply, chosen on the settings page.</summary>
    public void SetAccessLevel(McpAccessLevel level)
    {
        if (Config.AccessLevel == level) return;
        Config.AccessLevel = level;
        Config.Save();
        Core.Log.Info($"MCP: access level {level}");
        RaiseChanged();
    }

    /// <summary>Offline mode was switched (tray or settings window): stop or restart the listener.</summary>
    public void SyncOfflineMode() => Sync();

    private void Sync()
    {
        bool wanted = Config.Enabled && !_settings.OfflineMode;
        if (!wanted)
        {
            Stop();
            _failure = null;
            RaiseChanged();
            return;
        }
        if (_server != null) return;
        try
        {
            _server = new LoopbackHttpServer(Config.Port, Handle, reason => _ui.Post(_ => OnServerFailed(reason), null));
            _failure = null;
            Core.Log.Info($"MCP: listening on 127.0.0.1:{_server.Port}");
        }
        catch (SocketException e)
        {
            _failure = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {Config.Port} ist schon von einem anderen Programm belegt. Das Programm beenden oder WhisperShortcut neu starten."
                : "Der Dienst konnte nicht starten. Details stehen im Protokoll.";
            Core.Log.Error($"MCP: start failed ({e.SocketErrorCode})");
        }
        RaiseChanged();
    }

    private void OnServerFailed(string reason)
    {
        Core.Log.Error($"MCP: listener stopped: {reason}");
        Stop();
        _failure = "Der Dienst ist stehen geblieben. Bitte aus- und wieder einschalten.";
        RaiseChanged();
    }

    private void Stop()
    {
        if (_server == null) return;
        _server.Dispose();
        _server = null;
        Core.Log.Info("MCP: stopped");
    }

    // Runs on a thread-pool thread for every request.
    private McpHttpResponse Handle(McpHttpRequest request)
    {
        // Checked per request: offline mode switched on in the tray must take effect before the listener is down.
        if (_settings.OfflineMode) return McpHttpResponse.Empty(503);
        return McpHttpHandler.Handle(request, Config.ReadToken, Config.WriteToken, _dispatcher);
    }

    /// <summary>Runs a tool call on the UI thread; null = it did not get there in time (the dispatcher answers "busy").</summary>
    private McpToolResult? RunOnMain(Func<McpToolResult> call)
    {
        // 0 = queued, 1 = running, 2 = given up. A call given up before it started must not run later:
        // the AI app was already told that nothing changed.
        int state = 0;
        McpToolResult? result = null;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _ui.Post(_ =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { result = call(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        }, null);
        if (!done.Wait(MainWait) && Interlocked.CompareExchange(ref state, 2, 0) == 0) return null;
        done.Wait(); // it started: let it finish rather than report a change as not made
        if (error != null) throw error;
        return result;
    }

    private void RaiseChanged() => _ui.Post(_ => Changed?.Invoke(), null);

    // ---------------- IMcpHost ----------------

    /// <inheritdoc />
    public string AppVersion => Application.ProductVersion.Split('+')[0];

    /// <inheritdoc />
    public bool SpeechModelInstalled => OfflineEngine.SpeechInstalled;

    /// <inheritdoc />
    public bool LocalLlmInstalled(string llmId) => OfflineEngine.LlmInstalled(Core.OfflineCatalog.LlmById(llmId));

    /// <inheritdoc />
    public bool? AutoStartEnabled => AutoStart.IsEnabled;

    /// <inheritdoc />
    public bool SetAutoStart(bool enabled) => enabled ? AutoStart.Enable() : AutoStart.Disable();

    /// <inheritdoc />
    public string? WriteBlockedReason()
    {
        if (_processing()) return "WhisperShortcut is recording or processing a dictation right now. Nothing was changed. Try again in a moment.";
        if (_settingsDirty())
            return "The WhisperShortcut Settings window has unsaved changes. Nothing was changed. Ask the user to save or close it, then try again.";
        return null;
    }

    /// <summary>The same check in German, for „Übernehmen“ in the approval window.</summary>
    public string? AcceptBlockedReason()
    {
        if (_processing()) return "Gerade läuft eine Aufnahme. Bitte warten, bis der Text da ist, dann noch einmal „Übernehmen“.";
        if (_settingsDirty()) return "Im Einstellungsfenster gibt es ungespeicherte Änderungen. Bitte erst speichern oder schließen, dann „Übernehmen“.";
        return null;
    }

    /// <inheritdoc />
    public void AfterApply()
    {
        _afterApply();
        RaiseChanged();
    }

    /// <inheritdoc />
    public void ProposalCreated(McpProposal proposal) =>
        // Posted: the tool call answers first, the window opens right after.
        _ui.Post(_ => ShowProposal(proposal), null);

    /// <inheritdoc />
    public void OpenProblemReport(string path)
    {
        Core.Log.Info("MCP: problem report saved");
        _ui.Post(_ =>
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false }); }
            catch (Exception e)
            {
                Core.Log.Error($"Opening the problem report failed: {e.GetType().Name}");
                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true }); } catch { }
            }
        }, null);
    }

    // ---------------- proposals ----------------

    /// <summary>Opens the approval window for every pending proposal (the „Ansehen“ button).</summary>
    public void ShowPendingProposals()
    {
        foreach (var p in Proposals.Pending) ShowProposal(p);
    }

    private void ShowProposal(McpProposal proposal)
    {
        if (Proposals.Find(proposal.Id)?.Status != McpProposalStatus.Pending) return;
        if (_dialogs.TryGetValue(proposal.Id, out var open) && !open.IsDisposed)
        {
            open.Activate();
            return;
        }
        var dialog = new McpApprovalDialog(this, proposal, ClientName());
        _dialogs[proposal.Id] = dialog;
        dialog.FormClosed += (_, _) => _dialogs.Remove(proposal.Id);
        dialog.Show();
        dialog.Activate();
    }

    /// <summary>„Claude“ when the app that last connected is a Claude client, else neutral.</summary>
    public string ClientName()
    {
        var latest = Config.Clients.FirstOrDefault();
        return latest != null && latest.Name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ? "Claude" : "Die KI-App";
    }

    /// <summary>Undo of one change in the list; the German message says what happened.</summary>
    public McpUndoResult Undo(string changeId)
    {
        if (AcceptBlockedReason() is { } blocked) return new McpUndoResult(McpUndoOutcome.Failed, blocked);
        return Proposals.Applier!.Undo(changeId);
    }

    // ---------------- connecting apps ----------------

    /// <summary>Writes our entry into Claude Desktop's config (backup first). Returns the German result for the user.</summary>
    public string ConnectClaudeDesktop()
    {
        var path = McpClientSetup.ClaudeDesktopConfigPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var (url, key) = (EndpointUrl, KeyForClients);
        McpClientSetup.WriteWithBackup(path, existing => McpClientSetup.MergeClaudeDesktop(existing, url, key));
        Core.Log.Info("MCP: Claude Desktop config written");
        var text = "Eingetragen. Jetzt Claude Desktop ganz beenden (Rechtsklick auf das Claude-Symbol unten rechts → Beenden) und neu öffnen.";
        if (FindOnPath("npx.cmd", "npx.exe") == null)
            text += "\n\nClaude Desktop braucht dafür Node.js. Es ist auf diesem PC nicht zu finden: von nodejs.org die LTS-Version installieren, dann Claude neu öffnen.";
        return text;
    }

    /// <summary>
    /// Adds the server to Claude Code with <c>claude mcp add</c> (user scope); without the command line tool it
    /// writes %USERPROFILE%\.claude.json instead. Runs off the UI thread. Returns the German result for the user.
    /// </summary>
    public async Task<string> ConnectClaudeCodeAsync()
    {
        var (url, key) = (EndpointUrl, KeyForClients);
        var claude = FindClaude();
        if (claude != null)
        {
            // An older entry (other port or key) makes "add" fail: remove it first, its failure does not matter.
            await RunAsync(claude, McpClientSetup.ClaudeCodeRemoveArguments());
            var (exit, output) = await RunAsync(claude, McpClientSetup.ClaudeCodeAddArguments(url, key));
            Core.Log.Info($"MCP: claude mcp add exit {exit}");
            if (exit == 0)
                return "Eingetragen. In Claude Code eine neue Sitzung starten; „/mcp“ zeigt dann „whisper-shortcut“.";
            Core.Log.Error($"MCP: claude mcp add failed: {Core.ProblemReport.Redact(output.Replace(key, "…"))}");
        }
        var file = McpClientSetup.ClaudeCodeUserConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        await Task.Run(() => McpClientSetup.WriteWithBackup(file, existing => McpClientSetup.MergeClaudeCode(existing, url, key)));
        Core.Log.Info("MCP: Claude Code user config written");
        return "Eingetragen (in .claude.json im Benutzerordner). Claude Code ganz beenden und eine neue Sitzung starten; "
            + "„/mcp“ zeigt dann „whisper-shortcut“.";
    }

    /// <summary>Address, key and both setups as text, for an app without a „Verbinden“ button.</summary>
    public string ManualSetup() => McpClientSetup.ManualSetupText(EndpointUrl, KeyForClients);

    /// <summary>New keys for both access levels; every connected app has to be connected again.</summary>
    public void RegenerateKeys()
    {
        Config.RegenerateTokens();
        Core.Log.Info("MCP: keys regenerated");
        RaiseChanged();
    }

    private static string? FindClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // Native installer (~/.local/bin) and npm (%APPDATA%\npm) put it outside PATH when the shell was never restarted.
        foreach (var candidate in new[] { Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(roaming, "npm", "claude.cmd") })
            if (File.Exists(candidate)) return candidate;
        return FindOnPath("claude.exe", "claude.cmd");
    }

    private static string? FindOnPath(params string[] names)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { } // a broken PATH entry
            }
        }
        return null;
    }

    private static async Task<(int Exit, string Output)> RunAsync(string exe, IReadOnlyList<string> args)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "timeout");
            }
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch (Exception e)
        {
            return (-1, e.GetType().Name);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        foreach (var d in _dialogs.Values.ToList()) d.Close();
    }
}
