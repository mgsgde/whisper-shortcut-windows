using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WhisperShortcut.Core;

/// <summary>
/// How llama-server places the model. <see cref="Auto"/> lets llama.cpp offload whatever fits;
/// <see cref="Cpu"/> forces the CPU backends.
/// </summary>
public enum LlamaStartMode
{
    /// <summary>No <c>-ngl</c>: llama.cpp's own fitter offloads the layers that fit in GPU memory.</summary>
    Auto,

    /// <summary><c>-ngl 0 --device none</c>: do not use the GPU.</summary>
    Cpu,
}

/// <summary>Why the server is starting in <see cref="LlamaStartMode"/>; written to the log.</summary>
public enum LlamaStartReason
{
    /// <summary>No remembered GPU failure for this version and model.</summary>
    Default,

    /// <summary>Settings already record that this version and model did not fit on the GPU.</summary>
    Persisted,

    /// <summary>The GPU attempt just failed; this start is the one CPU retry.</summary>
    Retry,
}

/// <summary>
/// Runs llama.cpp's <c>llama-server</c> on 127.0.0.1 with a free port, started on demand and
/// stopped when idle. Loading a model takes seconds, so callers keep the host alive between requests.
/// A GPU start that exits before it is healthy is tried once more on CPU.
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(180);
    private const int RingSize = 40;
    private const string CouldNotStart = "Das lokale Sprachmodell ließ sich nicht starten. Details im Protokoll.";

    /// <summary>STATUS_ILLEGAL_INSTRUCTION: the CPU lacks an instruction the llama.cpp build uses.</summary>
    private const int IllegalInstruction = unchecked((int)0xC000001D);

    /// <summary>STATUS_DLL_NOT_FOUND: the loader failed (e.g. the VC++ runtime) before any model load.</summary>
    private const int DllNotFound = unchecked((int)0xC0000135);

    private readonly string _serverExe;
    private readonly string _modelPath;
    private readonly Action<Process>? _onStarted;
    private readonly Action? _onFellBackToCpu;
    private readonly object _gate = new();
    private readonly Queue<string> _ring = new();
    private readonly HttpClient _health = NetworkPolicy.CreateApiClient(TimeSpan.FromSeconds(2));

    private Process? _process;
    private Uri? _baseUrl;
    private Task? _startTask;
    private bool _ready;
    private int _epoch;
    private LlamaStartMode _mode;
    private LlamaStartReason _reason;
    private bool _rememberFallback;
    private long _lastUseTicks = Environment.TickCount64;

    /// <summary>
    /// Creates the host. <paramref name="onStarted"/> runs right after the process starts (the app
    /// attaches a Windows job object there so the server dies with the app).
    /// <paramref name="onFellBackToCpu"/> runs once the CPU retry is healthy, so the caller can remember it.
    /// </summary>
    public LlamaServerHost(string serverExe, string modelPath, Action<Process>? onStarted = null,
        LlamaStartMode mode = LlamaStartMode.Auto, LlamaStartReason reason = LlamaStartReason.Default,
        Action? onFellBackToCpu = null)
    {
        _serverExe = serverExe;
        _modelPath = modelPath;
        _onStarted = onStarted;
        _mode = mode;
        _reason = reason;
        _onFellBackToCpu = onFellBackToCpu;
    }

    /// <summary>http://127.0.0.1:port/ while the server runs, otherwise null.</summary>
    public Uri? BaseUrl
    {
        get { lock (_gate) return IsRunningLocked ? _baseUrl : null; }
    }

    /// <summary>True while the server process is alive and answered /health.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return IsRunningLocked; }
    }

    private bool IsRunningLocked => _ready && _process is { HasExited: false };

    /// <summary>User text for <see cref="CrashedOnUnsupportedCpu"/>.</summary>
    public const string UnsupportedCpuMessage = UserCopy.UnsupportedCpu;

    /// <summary>
    /// True when the server died with an illegal instruction, typically on the first request. The ARM64
    /// llama.cpp build needs ARMv8.6 (I8MM): Snapdragon X and Apple M2+ have it, Apple M1 and Snapdragon 8cx do not.
    /// </summary>
    public bool CrashedOnUnsupportedCpu
    {
        get { lock (_gate) return _process is { } p && Exited(p) && ExitCode(p) == IllegalInstruction; }
    }

    /// <summary>
    /// Arguments of one llama-server start. <see cref="LlamaStartMode.Auto"/> leaves layer offload to llama.cpp
    /// (passing <c>-ngl 99</c> makes it abort instead of fitting). <see cref="LlamaStartMode.Cpu"/> allows no GPU.
    /// </summary>
    internal static IReadOnlyList<string> BuildArgs(string modelPath, int port, LlamaStartMode mode)
    {
        var args = new List<string>
        {
            "-m", modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
            "-c", "16384", "--jinja", "--no-webui",
        };
        if (mode == LlamaStartMode.Cpu)
        {
            args.Add("-ngl");
            args.Add("0");
            args.Add("--device");
            args.Add("none");
        }
        return args;
    }

    /// <summary>
    /// True when a completion failed because the GPU server went away, so one CPU restart should serve that request.
    /// An unsupported CPU, a missing DLL, a still-running server and a plain timeout or HTTP error do not qualify.
    /// </summary>
    internal static bool ShouldRetryOnCpu(LlamaStartMode mode, int? exitCode, bool connectionError)
    {
        if (mode != LlamaStartMode.Auto) return false;
        if (exitCode == IllegalInstruction || exitCode == DllNotFound) return false;
        return exitCode != null || connectionError;
    }

    /// <summary>See <see cref="ShouldRetryOnCpu(LlamaStartMode, int?, bool)"/> for the request that just failed.</summary>
    public bool ShouldRetryOnCpu(Exception error)
    {
        int? exit = null;
        LlamaStartMode mode;
        lock (_gate)
        {
            mode = _mode;
            if (_process is { } p && Exited(p)) exit = ExitCode(p);
        }
        return ShouldRetryOnCpu(mode, exit, error is LocalLlmException { InnerException: HttpRequestException });
    }

    /// <summary>Starts the server if needed and waits until it is healthy. Concurrent callers share one start.</summary>
    public Task EnsureStartedAsync(CancellationToken ct)
    {
        // A caller that wants the server counts as use: the idle timer must not kill it before the request arrives.
        MarkUsed();
        Task start;
        Process? stale = null;
        lock (_gate)
        {
            if (IsRunningLocked) return Task.CompletedTask;
            // HasExited alone must not start a second process: the running start owns the retry onto CPU.
            if (_startTask == null || _startTask.IsCompleted)
            {
                stale = DetachLocked();
                _startTask = StartAsync();
            }
            start = _startTask;
        }
        Kill(stale, wait: false);
        // The start itself is not tied to one caller's token: others may still wait for it.
        return start.WaitAsync(ct);
    }

    /// <summary>
    /// Drops a GPU server that died during a request and starts once on CPU. The following completion
    /// repeats the request; a healthy CPU start remembers the fallback.
    /// </summary>
    public Task RestartOnCpuAsync(CancellationToken ct)
    {
        MarkUsed();
        Task start;
        Process? stale;
        lock (_gate)
        {
            _mode = LlamaStartMode.Cpu;
            _reason = LlamaStartReason.Retry;
            _rememberFallback = true;
            stale = DetachLocked();
            _startTask = StartAsync();
            start = _startTask;
        }
        Kill(stale, wait: false);
        return start.WaitAsync(ct);
    }

    /// <summary>Records a use, which postpones <see cref="StopIfIdle"/>.</summary>
    public void MarkUsed() => Interlocked.Exchange(ref _lastUseTicks, Environment.TickCount64);

    /// <summary>Stops the server when it has not been used for <paramref name="idle"/>.</summary>
    public void StopIfIdle(TimeSpan idle)
    {
        var since = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastUseTicks));
        if (IsRunning && since >= idle)
        {
            Log.Info("llama-server idle, stopping");
            Stop();
        }
    }

    /// <summary>Kills the server and its child processes and waits (up to 5 s) until it is gone.</summary>
    public void Stop()
    {
        Process? p;
        lock (_gate) p = DetachLocked();
        // Outside the lock: the output handlers take it. The model file stays mapped until the process is gone.
        Kill(p, wait: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _health.Dispose();
    }

    // Forgets the process and returns it, so the caller kills it outside the lock.
    // Bumps the epoch so a start that is still in its health loop does not retry or kill the next process.
    private Process? DetachLocked()
    {
        _epoch++;
        var p = _process;
        _process = null;
        _baseUrl = null;
        _ready = false;
        _startTask = null;
        return p;
    }

    private static void Kill(Process? p, bool wait)
    {
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            if (wait && !p.WaitForExit(5000)) Log.Error("llama-server did not exit within 5 s of the kill");
        }
        catch (Exception ex) { Log.Error("llama-server kill failed", ex); }
        p.Dispose();
    }

    private async Task StartAsync()
    {
        int epoch;
        LlamaStartMode mode;
        LlamaStartReason reason;
        lock (_gate)
        {
            epoch = _epoch;
            mode = _mode;
            reason = _reason;
        }
        try
        {
            await StartOnceAsync(mode, reason, epoch).ConfigureAwait(false);
        }
        catch (LlamaStartupExitException) when (mode == LlamaStartMode.Auto)
        {
            bool retry;
            lock (_gate)
            {
                retry = _epoch == epoch;
                if (retry)
                {
                    _mode = LlamaStartMode.Cpu;
                    _reason = LlamaStartReason.Retry;
                    _rememberFallback = true;
                }
            }
            if (!retry) throw;
            Log.Info("GPU start failed, retrying on CPU");
            await StartOnceAsync(LlamaStartMode.Cpu, LlamaStartReason.Retry, epoch).ConfigureAwait(false);
        }
    }

    private async Task StartOnceAsync(LlamaStartMode mode, LlamaStartReason reason, int epoch)
    {
        Log.Info($"llama-server start mode={mode} (reason={reason.ToString().ToLowerInvariant()})");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var baseUrl = new Uri($"http://127.0.0.1:{port}/");

        var psi = new ProcessStartInfo(_serverExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in BuildArgs(_modelPath, port, mode))
            psi.ArgumentList.Add(a);

        Process p;
        lock (_gate) _ring.Clear();
        try
        {
            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => Ring(e.Data);
            p.ErrorDataReceived += (_, e) => Ring(e.Data);
            p.Start();
        }
        catch (Exception ex)
        {
            Log.Error("llama-server could not be started", ex);
            throw new LocalLlmException(CouldNotStart, ex);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Process? orphan = null;
        lock (_gate)
        {
            // Stop() won while we were creating the process: do not adopt it, and do not treat it as a GPU failure.
            // Kill outside the lock; the output handlers take it.
            if (_epoch != epoch) orphan = p;
            else
            {
                _process = p;
                _baseUrl = baseUrl;
            }
        }
        if (orphan != null)
        {
            Kill(orphan, wait: false);
            throw new LocalLlmException(CouldNotStart);
        }
        try { _onStarted?.Invoke(p); }
        catch (Exception ex) { Log.Error("llama-server onStarted failed", ex); }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StartTimeout)
        {
            if (Exited(p))
            {
                var code = ExitCode(p);
                Fail(p, epoch, $"llama-server exited (code 0x{code:X8})", Classify(code));
            }
            try
            {
                using var resp = await _health.GetAsync(new Uri(baseUrl, "health")).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    AcceptReady(p, epoch, port, sw.Elapsed);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }
            // 50 ms while the process is coming up (saves up to 200 ms vs a 250 ms poll);
            // back off after 10 s so a stuck start does not hammer /health.
            var delay = sw.Elapsed < TimeSpan.FromSeconds(10) ? 50 : 250;
            await Task.Delay(delay).ConfigureAwait(false);
        }
        Fail(p, epoch, "llama-server did not become healthy in time", StartupFailure.Timeout);
    }

    // The health check succeeded. Only the start that still owns the process may publish it.
    private void AcceptReady(Process p, int epoch, int port, TimeSpan elapsed)
    {
        Action? remember = null;
        string? offloaded = null;
        lock (_gate)
        {
            if (_epoch != epoch || !ReferenceEquals(_process, p))
                throw new LocalLlmException(CouldNotStart);
            _ready = true;
            foreach (var line in _ring)
                if (line.Contains("offloaded", StringComparison.Ordinal)) offloaded = line;
            if (_rememberFallback)
            {
                _rememberFallback = false;
                remember = _onFellBackToCpu;
            }
        }
        MarkUsed();
        Log.Info($"llama-server ready on port {port} after {elapsed.TotalSeconds:F1} s");
        if (offloaded != null) Log.Info(offloaded);
        if (remember == null) return;
        try { remember(); }
        catch (Exception ex) { Log.Error("Could not store the CPU fallback", ex); }
    }

    private enum StartupFailure { Exited, UnsupportedCpu, MissingDll, Timeout }

    private static StartupFailure Classify(int code) => code switch
    {
        IllegalInstruction => StartupFailure.UnsupportedCpu,
        DllNotFound => StartupFailure.MissingDll,
        _ => StartupFailure.Exited,
    };

    // Stop() from another thread disposes the process; that counts as exited.
    private static bool Exited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static int ExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return 0; }
    }

    // Kills only the process this start still owns. A Stop() that already detached it must not become a CPU retry,
    // and must not kill whatever process a newer start has published.
    private void Fail(Process mine, int epoch, string why, StartupFailure kind)
    {
        string[] lines;
        Process? toKill;
        lock (_gate)
        {
            lines = _ring.ToArray();
            if (_epoch != epoch || !ReferenceEquals(_process, mine))
                toKill = null;
            else
            {
                toKill = _process;
                _process = null;
                _baseUrl = null;
                _ready = false;
            }
        }
        if (toKill == null) throw new LocalLlmException(CouldNotStart);
        Log.Error($"{why}; last output: {string.Join(" | ", lines)}");
        Kill(toKill, wait: true);
        throw kind switch
        {
            StartupFailure.UnsupportedCpu => new UnsupportedCpuException(UnsupportedCpuMessage),
            StartupFailure.Exited => new LlamaStartupExitException(CouldNotStart),
            _ => new LocalLlmException(CouldNotStart),
        };
    }

    private void Ring(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_gate)
        {
            _ring.Enqueue(line);
            while (_ring.Count > RingSize) _ring.Dequeue();
        }
    }
}
