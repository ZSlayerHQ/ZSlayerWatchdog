using System.Net;
using System.Text;
using System.Text.Json;

namespace ZSlayerCommandCenter.Launcher;

public class WatchdogApi
{
    private readonly ServerProcessManager _server;
    private readonly HeadlessProcessManager _headless;
    private readonly WatchdogAppConfig _config;
    private readonly Action<string> _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public WatchdogApi(ServerProcessManager server, HeadlessProcessManager headless,
                       WatchdogAppConfig config, Action<string> log)
    {
        _server = server;
        _headless = headless;
        _config = config;
        _log = log;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_config.Watchdog.Port}/");

        try
        {
            _listener.Start();
            _log($"Watchdog API listening on http://127.0.0.1:{_config.Watchdog.Port}/");
        }
        catch (Exception ex)
        {
            _log($"WARNING: Could not start watchdog API: {ex.Message}");
            return;
        }

        Task.Run(async () =>
        {
            while (_listener.IsListening && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(ctx));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimStart('/') ?? "";
        var method = ctx.Request.HttpMethod;

        // CORS headers
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";

        if (method == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        object? result = null;
        switch (path)
        {
            // Server endpoints
            case "status":
                result = _server.GetStatus();
                break;
            case "start" when method == "POST":
                _log("Server start requested via API");
                _server.Start();
                result = _server.GetStatus();
                break;
            case "stop" when method == "POST":
                _log("Server stop requested via API");
                _server.Stop();
                result = _server.GetStatus();
                break;
            case "restart" when method == "POST":
                _log("Server restart requested via API");
                _server.Stop();
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    _server.Start();
                });
                result = new { restarting = true };
                break;

            // Headless endpoints
            case "headless/status":
                result = _headless.GetStatus();
                break;
            case "headless/start" when method == "POST":
                _log("Headless start requested via API");
                result = _headless.Start();
                break;
            case "headless/stop" when method == "POST":
                _log("Headless stop requested via API");
                result = _headless.Stop();
                break;
            case "headless/restart" when method == "POST":
                _log("Headless restart requested via API");
                result = _headless.Restart();
                break;

            default:
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
        }

        var json = JsonSerializer.Serialize(result);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }
}
