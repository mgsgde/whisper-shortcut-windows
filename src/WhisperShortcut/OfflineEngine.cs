using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// Owns everything the offline mode needs at runtime: the downloads (so they keep running when the
/// settings window closes), the loaded Parakeet model and the local llama-server process.
/// Lives as long as the tray app.
/// </summary>
internal sealed class OfflineEngine : IDisposable
{
    /// <summary>The local language model's server stops after this long without a request and frees its RAM.</summary>
    private static readonly TimeSpan LlmIdleTimeout = TimeSpan.FromMinutes(15);

    private readonly Core.Settings _settings;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Core.ModelDownloader _downloader;
    private readonly Core.LocalLlmClient _llm;
    private readonly KillOnCloseJob _job = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 60_000 };
    private readonly SemaphoreSlim _parakeetLock = new(1, 1);

    private Core.ParakeetTranscriber? _parakeet;
    private Core.LlamaServerHost? _server;
    private string? _serverModelId;
    private Download? _download;

    public OfflineEngine(Core.Settings settings)
    {
        _settings = settings;
        _downloader = new Core.ModelDownloader(_http);
        _llm = new Core.LocalLlmClient(_http);
        _idleTimer.Tick += (_, _) => _server?.StopIfIdle(LlmIdleTimeout);
        _idleTimer.Start();
    }

    public static Architecture Arch => RuntimeInformation.ProcessArchitecture;

    /// <summary>Physical RAM of this PC, for the model recommendation.</summary>
    public static long TotalRam => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public Core.LocalLlm SelectedLlm => Core.OfflineCatalog.LlmById(_settings.OfflineLlmId);

    public static bool SpeechInstalled => Core.ModelDownloader.IsInstalled(Core.OfflineCatalog.Parakeet);

    /// <summary>
    /// True once llama-server died with an illegal instruction on this PC (e.g. Apple M1 under Parallels).
    /// Remembered in a file, so the next attempt says so at once instead of loading for minutes and crashing again.
    /// </summary>
    public static bool LlmUnsupportedCpu => File.Exists(UnsupportedCpuMarker);

    private static string UnsupportedCpuMarker => Path.Combine(Core.OfflineCatalog.Root, "llm-unsupported-cpu.txt");

    public static bool LlmInstalled(Core.LocalLlm llm) =>
        Core.ModelDownloader.IsInstalled(llm.Component)
        && Core.ModelDownloader.IsInstalled(Core.OfflineCatalog.LlamaServer(Arch));

    // ---------------- downloads ----------------

    /// <summary>The running download, if any. Only one at a time: two parallel multi-GB downloads only slow each other down.</summary>
    public Download? CurrentDownload => _download;

    /// <summary>The last download that ended, kept so the settings can show why it failed.</summary>
    public Download? LastFinished { get; private set; }

    /// <summary>Raised on the UI thread whenever a download starts, progresses, finishes or fails.</summary>
    public event Action? DownloadChanged;

    /// <summary>Downloads the speech model, or the llama.cpp program plus the given language model.</summary>
    public void StartDownload(string id, IReadOnlyList<Core.OfflineComponent> components)
    {
        if (_download != null) return;
        var download = new Download(id, components);
        _download = download;
        LastFinished = null;
        var ui = SynchronizationContext.Current;
        void Changed() => ui?.Post(_ => DownloadChanged?.Invoke(), null);

        long total = Math.Max(1, components.Sum(c => c.Files.Sum(f => f.Size)));
        _ = Task.Run(async () =>
        {
            long done = 0;
            try
            {
                foreach (var c in components)
                {
                    if (Core.ModelDownloader.IsInstalled(c)) { done += c.Files.Sum(f => f.Size); continue; }
                    long size = c.Files.Sum(f => f.Size);
                    long before = done;
                    var progress = new Progress<double>(p =>
                    {
                        download.Fraction = (before + p * size) / total;
                        Changed();
                    });
                    await _downloader.DownloadAsync(c, progress, download.Cancel.Token).ConfigureAwait(false);
                    done += size;
                }
                download.Fraction = 1;
            }
            catch (OperationCanceledException)
            {
                Core.Log.Info($"Download {id} cancelled");
            }
            catch (Exception e)
            {
                Core.Log.Error($"Download {id} failed", e);
                download.Error = e is Core.OfflineDownloadException ? e.Message : "Download fehlgeschlagen. Details im Protokoll.";
            }
            finally
            {
                ui?.Post(_ =>
                {
                    _download = null;
                    download.Finished = true;
                    LastFinished = download;
                    DownloadChanged?.Invoke();
                    download.Cancel.Dispose();
                }, null);
            }
        });
        DownloadChanged?.Invoke();
    }

    /// <summary>Removes a downloaded model; unloads it first so Windows lets go of the files.</summary>
    public void Delete(Core.OfflineComponent component)
    {
        if (component == Core.OfflineCatalog.Parakeet)
        {
            _parakeet?.Dispose();
            _parakeet = null;
        }
        else
        {
            StopServer();
        }
        Core.ModelDownloader.Delete(component);
    }

    // ---------------- inference ----------------

    /// <summary>Loads Parakeet in the background (app start in offline mode), so the first dictation does not wait 3 s.</summary>
    public void Prewarm()
    {
        if (!_settings.OfflineMode || !SpeechInstalled) return;
        _ = Task.Run(async () =>
        {
            try { await LoadParakeetAsync().ConfigureAwait(false); }
            catch (Exception e) { Core.Log.Error("Parakeet prewarm failed", e); }
        });
    }

    /// <summary>Starts the language model while the user is still speaking the instruction.</summary>
    public void PrewarmLlm()
    {
        if (!LlmInstalled(SelectedLlm) || LlmUnsupportedCpu) return;
        _ = Task.Run(async () =>
        {
            try { await EnsureServerAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Core.Log.Error("LLM prewarm failed", e); }
        });
    }

    /// <summary>Speech to text with Parakeet. Throws <see cref="OfflineNotReadyException"/> if the model is missing.</summary>
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (!SpeechInstalled) throw new OfflineNotReadyException("Die Offline-Spracherkennung ist noch nicht geladen.");
        var parakeet = await LoadParakeetAsync().ConfigureAwait(false);
        // Decoding cannot be interrupted; a cancel just drops the result (a minute of audio takes ~1 s).
        var text = await Task.Run(() => parakeet.Transcribe(wav), CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return text;
    }

    /// <summary>Selection + spoken instruction (already transcribed) to the local language model.</summary>
    public async Task<string> PromptAsync(string? selection, string instruction, string? customSystemPrompt, CancellationToken ct)
    {
        if (!LlmInstalled(SelectedLlm)) throw new OfflineNotReadyException("Das Offline-Sprachmodell ist noch nicht geladen.");
        if (LlmUnsupportedCpu) throw new Core.LocalLlmException(Core.LlamaServerHost.UnsupportedCpuMessage);
        var server = await EnsureServerAsync(ct).ConfigureAwait(false);
        server.MarkUsed();
        try
        {
            var answer = await _llm.CompleteAsync(server.BaseUrl!,
                Core.DictatePromptText.SystemPrompt(customSystemPrompt),
                Core.DictatePromptText.LocalUserTurn(selection, instruction), ct).ConfigureAwait(false);
            server.MarkUsed();
            return answer;
        }
        catch (Core.LocalLlmException e)
        {
            // Give the process a moment to report its exit code; it dies while answering.
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            if (!server.CrashedOnUnsupportedCpu) throw;
            Core.Log.Error("llama-server crashed with an illegal instruction: this CPU cannot run the llama.cpp build");
            try { File.WriteAllText(UnsupportedCpuMarker, $"llama-server crashed with 0xC000001D on {DateTime.Now:yyyy-MM-dd HH:mm}. Delete this file to try again.\r\n"); }
            catch (Exception m) { Core.Log.Error("Could not write the unsupported-CPU marker", m); }
            throw new Core.LocalLlmException(Core.LlamaServerHost.UnsupportedCpuMessage, e);
        }
    }

    private async Task<Core.ParakeetTranscriber> LoadParakeetAsync()
    {
        if (_parakeet != null) return _parakeet;
        await _parakeetLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_parakeet != null) return _parakeet;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var dir = Path.Combine(Core.OfflineCatalog.Root, Core.OfflineCatalog.Parakeet.Directory);
            // Half the cores: decoding stays fast and Word keeps responding while it runs.
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            _parakeet = await Task.Run(() => Core.ParakeetTranscriber.Load(dir, threads)).ConfigureAwait(false);
            Core.Log.Info($"Parakeet loaded in {sw.ElapsedMilliseconds} ms ({threads} threads)");
            return _parakeet;
        }
        finally { _parakeetLock.Release(); }
    }

    private async Task<Core.LlamaServerHost> EnsureServerAsync(CancellationToken ct)
    {
        var llm = SelectedLlm;
        if (_server != null && _serverModelId != llm.Id) StopServer(); // model switched in the settings
        var exe = Core.OfflineCatalog.LlamaServerExe(Arch);
        if (_server == null) PlaceVcRuntime(Path.GetDirectoryName(exe)!);
        _server ??= new Core.LlamaServerHost(exe, llm.GgufPath, _job.Assign);
        _serverModelId = llm.Id;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool wasRunning = _server.IsRunning;
        await _server.EnsureStartedAsync(ct).ConfigureAwait(false);
        if (!wasRunning) Core.Log.Info($"llama-server ready with {llm.Id} after {sw.ElapsedMilliseconds} ms");
        return _server;
    }

    /// <summary>
    /// Copies the VC++ runtime DLLs embedded in this exe next to llama-server.exe (Windows looks there
    /// first). Without them llama-server dies at load time on PCs that never installed the VC++ redistributable.
    /// </summary>
    private static void PlaceVcRuntime(string dir)
    {
        const string prefix = "WhisperShortcut.vcruntime.";
        var asm = typeof(OfflineEngine).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var target = Path.Combine(dir, name[prefix.Length..]);
            if (File.Exists(target)) continue;
            try
            {
                using var src = asm.GetManifestResourceStream(name)!;
                using var dst = File.Create(target + ".part");
                src.CopyTo(dst);
                dst.Close();
                File.Move(target + ".part", target);
                Core.Log.Info($"Placed {Path.GetFileName(target)} beside llama-server");
            }
            catch (Exception e) { Core.Log.Error($"Could not place {Path.GetFileName(target)}", e); }
        }
    }

    /// <summary>True while the language model is loaded, so the tray can say "Sprachmodell wird geladen" only when it is not.</summary>
    public bool LlmReady => _server?.IsRunning == true && _serverModelId == SelectedLlm.Id;

    private void StopServer()
    {
        _server?.Stop();
        _server?.Dispose();
        _server = null;
        _serverModelId = null;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _download?.Cancel.Cancel();
        StopServer();
        _parakeet?.Dispose();
        _job.Dispose();
        _http.Dispose();
    }

    internal sealed class Download
    {
        public Download(string id, IReadOnlyList<Core.OfflineComponent> components) { Id = id; Components = components; }
        public string Id { get; }
        public IReadOnlyList<Core.OfflineComponent> Components { get; }
        public double Fraction { get; set; }
        public string? Error { get; set; }
        public bool Finished { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
    }
}

/// <summary>An offline model was needed but has not been downloaded yet.</summary>
internal sealed class OfflineNotReadyException(string message) : Exception(message);

/// <summary>
/// Windows job object with KILL_ON_JOB_CLOSE: llama-server is assigned to it, so it dies with the app
/// even when the app is killed in Task Manager or crashes. Otherwise a 6 GB process would stay behind.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private readonly IntPtr _handle;

    public KillOnCloseJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) { Core.Log.Error($"CreateJobObject failed ({Marshal.GetLastWin32Error()})"); return; }
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf(info);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!NativeMethods.SetInformationJobObject(_handle, NativeMethods.JobObjectExtendedLimitInformation, ptr, (uint)size))
                Core.Log.Error($"SetInformationJobObject failed ({Marshal.GetLastWin32Error()})");
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Assign(System.Diagnostics.Process process)
    {
        if (_handle == IntPtr.Zero) return;
        if (!NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
            Core.Log.Error($"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) NativeMethods.CloseHandle(_handle);
    }
}
