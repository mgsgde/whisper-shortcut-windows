// Test-only static file server for the Windows VM e2e loop. Not part of the solution or the app.
// Why: the Mac firewall blocks the VM from reaching a Mac-hosted server, and PowerShell over
// `prlctl exec` hangs in Windows Terminal. So this runs inside the VM (WinExe: no console window).
// Usage: feedserver.exe <root folder> [port=8765]
//   then start the app with: --update-feed http://localhost:8765/latest.json
// GET /<name> serves <root>/<name> (200) or 404; GET /quit stops the server.
// Every request is appended to <root>/feedserver.log (time, path, status).
using System.Net;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var port = args.Length > 1 ? int.Parse(args[1]) : 8765;
var logPath = Path.Combine(root, "feedserver.log");

using var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Start();

var running = true;
while (running)
{
    var ctx = await listener.GetContextAsync();
    var name = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
    int status;
    try
    {
        if (name == "quit") { status = 200; running = false; }
        else if (name.Length == 0 || name.Contains("..") || name.Contains('/') || name.Contains('\\')
                 || !File.Exists(Path.Combine(root, name))) status = 404;
        else
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, name));
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            status = 200;
        }
    }
    catch { status = 500; }
    ctx.Response.StatusCode = status;
    ctx.Response.Close();
    File.AppendAllText(logPath, $"{DateTime.Now:O}\t/{name}\t{status}\n");
}
listener.Stop();
