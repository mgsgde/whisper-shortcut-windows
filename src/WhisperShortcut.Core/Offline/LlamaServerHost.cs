using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WhisperShortcut.Core;

/// <summary>
/// Runs llama.cpp's <c>llama-server</c> on 127.0.0.1 with a free port, started on demand and
/// stopped when idle. Loading a model takes seconds, so callers keep the host alive between requests.
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(180);
    private const int RingSize = 40;

    private readonly string _serverExe;
    private readonly string _modelPath;
    private readonly Action<Process>? _onStarted;
    private readonly object _gate = new();
    private readonly Queue<string> _ring = new();
    private readonly HttpClient _health = new() { Timeout = TimeSpan.FromSeconds(2) };

    private Process? _process;
    private Uri? _baseUrl;
    private Task? _startTask;
    private bool _ready;
    private long _lastUseTicks = Environment.TickCount64;

    /// <summary>
    /// Creates the host. <paramref name="onStarted"/> runs right after the process starts (the app
    /// attaches a Windows job object there so the server dies with the app).
    /// </summary>
    public LlamaServerHost(string serverExe, string modelPath, Action<Process>? onStarted = null)
    {
        _serverExe = serverExe;
        _modelPath = modelPath;
        _onStarted = onStarted;
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

    /// <summary>Starts the server if needed and waits until it is healthy. Concurrent callers share one start.</summary>
    public Task EnsureStartedAsync(CancellationToken ct)
    {
        Task start;
        lock (_gate)
        {
            if (IsRunningLocked) return Task.CompletedTask;
            if (_startTask == null || _startTask.IsCompleted || (_process?.HasExited ?? false))
            {
                CleanupLocked();
                _startTask = StartAsync();
            }
            start = _startTask;
        }
        // The start itself is not tied to one caller's token: others may still wait for it.
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

    /// <summary>Kills the server and its child processes.</summary>
    public void Stop()
    {
        lock (_gate) CleanupLocked();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _health.Dispose();
    }

    private void CleanupLocked()
    {
        var p = _process;
        _process = null;
        _baseUrl = null;
        _ready = false;
        _startTask = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) { Log.Error("llama-server kill failed", ex); }
        p.Dispose();
    }

    private async Task StartAsync()
    {
        int port;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var psi = new ProcessStartInfo(_serverExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", _modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
                     "-c", "8192", "--jinja", "--no-webui", "-ngl", "99" })
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
            throw new LocalLlmException("Das lokale Sprachmodell ließ sich nicht starten. Details im Protokoll.", ex);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (_gate)
        {
            _process = p;
            _baseUrl = new Uri($"http://127.0.0.1:{port}/");
        }
        try { _onStarted?.Invoke(p); }
        catch (Exception ex) { Log.Error("llama-server onStarted failed", ex); }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StartTimeout)
        {
            if (Exited(p)) Fail("llama-server exited");
            try
            {
                using var resp = await _health.GetAsync(new Uri(_baseUrl!, "health")).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    lock (_gate) _ready = ReferenceEquals(_process, p);
                    Log.Info($"llama-server ready on port {port} after {sw.Elapsed.TotalSeconds:F1} s");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        Fail("llama-server did not become healthy in time");
    }

    // Stop() from another thread disposes the process; that counts as exited.
    private static bool Exited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private void Fail(string why)
    {
        string[] lines;
        lock (_gate) lines = _ring.ToArray();
        Log.Error($"{why}; last output: {string.Join(" | ", lines)}");
        Stop();
        throw new LocalLlmException("Das lokale Sprachmodell ließ sich nicht starten. Details im Protokoll.");
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
