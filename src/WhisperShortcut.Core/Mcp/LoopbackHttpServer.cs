using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WhisperShortcut.Core.Mcp;

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 (port of LoopbackHTTPServer.swift): one request per connection, no TLS.
/// Binds IPAddress.Loopback only, never Any, so nothing but this PC can reach it.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    /// <summary>Largest accepted request body.</summary>
    public const int MaxBodyBytes = 1_048_576;

    private const int MaxHeaderBytes = 32 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener;
    private readonly Func<McpHttpRequest, McpHttpResponse> _handler;
    private readonly Action<string>? _onFailure;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    /// <summary>Binds 127.0.0.1:port (0 = any free port) and starts accepting at once. Throws SocketException (e.g. AddressAlreadyInUse).</summary>
    public LoopbackHttpServer(int port, Func<McpHttpRequest, McpHttpResponse> handler, Action<string>? onFailure = null)
    {
        _handler = handler;
        _onFailure = onFailure;
        _listener = new TcpListener(IPAddress.Loopback, port);
        // Windows lets a second listener share a port unless asked otherwise.
        if (OperatingSystem.IsWindows()) _listener.ExclusiveAddressUse = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>The bound port.</summary>
    public int Port { get; }

    /// <summary>Stops accepting and cancels open connections. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleConnection(client));
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) == 1 || ex is OperationCanceledException) return;
            Log.Error("MCP: accept loop stopped", ex);
            try { _onFailure?.Invoke(ex.Message); } catch { }
        }
    }

    private sealed class HttpError(int status) : Exception { public int Status { get; } = status; }

    private async Task HandleConnection(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                McpHttpResponse response;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(ReadTimeout);
                    var request = await ReadRequest(stream, timeout.Token).ConfigureAwait(false);
                    try { response = _handler(request); }
                    catch (Exception ex)
                    {
                        Log.Error("MCP: handler failed", ex);
                        response = McpHttpResponse.Json(new System.Text.Json.Nodes.JsonObject { ["error"] = "internal error" }, 500);
                    }
                }
                catch (HttpError e) { response = McpHttpResponse.Empty(e.Status); }
                catch (OperationCanceledException)
                {
                    if (_cts.IsCancellationRequested) return;
                    response = McpHttpResponse.Empty(408);
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { return; }

                await WriteResponse(stream, response).ConfigureAwait(false);
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            catch (Exception ex)
            {
                Log.Warn("MCP: connection failed (" + ex.GetType().Name + ")");
            }
        }
    }

    private sealed class Reader(Stream s)
    {
        private readonly byte[] _buf = new byte[8192];
        private int _pos, _len;

        public async ValueTask<int> ReadByte(CancellationToken ct)
        {
            if (_pos == _len)
            {
                _len = await s.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0) return -1;
            }
            return _buf[_pos++];
        }

        /// <summary>One line without its terminator; the budget is shared by the whole header block.</summary>
        public async ValueTask<string?> ReadLine(int[] budget, CancellationToken ct)
        {
            var sb = new List<byte>(128);
            while (true)
            {
                var b = await ReadByte(ct).ConfigureAwait(false);
                if (b < 0) return sb.Count == 0 ? null : throw new HttpError(400);
                if (--budget[0] < 0) throw new HttpError(413);
                if (b == '\n') break;
                sb.Add((byte)b);
            }
            if (sb.Count > 0 && sb[^1] == '\r') sb.RemoveAt(sb.Count - 1);
            return Encoding.Latin1.GetString(sb.ToArray());
        }

        public async ValueTask ReadExact(byte[] dest, int offset, int count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (_pos == _len)
                {
                    _len = await s.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
                    _pos = 0;
                    if (_len == 0) throw new HttpError(400);
                }
                var n = Math.Min(count, _len - _pos);
                Buffer.BlockCopy(_buf, _pos, dest, offset, n);
                _pos += n; offset += n; count -= n;
            }
        }
    }

    private static async Task<McpHttpRequest> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var reader = new Reader(stream);
        var budget = new[] { MaxHeaderBytes };
        var requestLine = await reader.ReadLine(budget, ct).ConfigureAwait(false) ?? throw new HttpError(400);
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new HttpError(400);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLine(budget, ct).ConfigureAwait(false) ?? throw new HttpError(400);
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpError(400);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var chunked = headers.TryGetValue("Transfer-Encoding", out var te);
        if (chunked && !te!.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)) throw new HttpError(400);
        var hasLength = headers.TryGetValue("Content-Length", out var cl);
        if (chunked && hasLength) throw new HttpError(400); // request smuggling shape

        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);

        byte[] body = Array.Empty<byte>();
        if (chunked)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await reader.ReadLine(new[] { 1024 }, ct).ConfigureAwait(false) ?? throw new HttpError(400);
                var semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine[..semi];
                if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var size) || size < 0)
                    throw new HttpError(400);
                if (size == 0)
                {
                    // Trailers, ended by an empty line.
                    var tb = new[] { MaxHeaderBytes };
                    while ((await reader.ReadLine(tb, ct).ConfigureAwait(false) ?? throw new HttpError(400)).Length > 0) { }
                    break;
                }
                if (ms.Length + size > MaxBodyBytes) throw new HttpError(413);
                var chunk = new byte[size];
                await reader.ReadExact(chunk, 0, size, ct).ConfigureAwait(false);
                ms.Write(chunk);
                var end = await reader.ReadLine(new[] { 8 }, ct).ConfigureAwait(false);
                if (end == null || end.Length != 0) throw new HttpError(400);
            }
            body = ms.ToArray();
        }
        else if (hasLength)
        {
            if (!long.TryParse(cl, out var n) || n < 0) throw new HttpError(400);
            if (n > MaxBodyBytes) throw new HttpError(413);
            body = new byte[n];
            await reader.ReadExact(body, 0, (int)n, ct).ConfigureAwait(false);
        }
        return new McpHttpRequest(parts[0], parts[1], headers, body);
    }

    private static string Reason(int status) => status switch
    {
        100 => "Continue",
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    private static async Task WriteResponse(NetworkStream stream, McpHttpResponse r)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
        foreach (var (k, v) in r.Headers)
        {
            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || k.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            // A header value with a line break would split the response.
            sb.Append(k.Replace('\r', ' ').Replace('\n', ' ')).Append(": ").Append(v.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
        }
        sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        using var cts = new CancellationTokenSource(ReadTimeout);
        try
        {
            await stream.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), cts.Token).ConfigureAwait(false);
            if (r.Body.Length > 0) await stream.WriteAsync(r.Body, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
}
