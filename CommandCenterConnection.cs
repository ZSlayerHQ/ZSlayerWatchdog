using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ZSlayerCommandCenter.Launcher;

/// <summary>
/// WebSocket client that connects to the CC server's /ws/watchdog endpoint.
/// Handles auto-reconnect with exponential backoff, registration, periodic status,
/// and inbound command execution.
/// </summary>
public class CommandCenterConnection : IDisposable
{
    public enum State { Disconnected, Connecting, Connected }

    private readonly string _wsUrl;
    private readonly string _watchdogId;
    private readonly string _watchdogName;
    private readonly WatchdogAppConfig _config;
    private readonly ServerProcessManager _server;
    private readonly HeadlessProcessManager _headless;
    private readonly Action<string> _log;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _backoffMs = 5000;
    private const int MaxBackoffMs = 30_000;
    private bool _authRejected;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ValidTargets = ["sptServer", "headlessClient"];
    private static readonly HashSet<string> ValidActions = ["start", "stop", "restart"];

    public State ConnectionState { get; private set; } = State.Disconnected;
    public event Action<State>? StateChanged;

    public CommandCenterConnection(
        string serverUrl,
        string watchdogId,
        string watchdogName,
        string token,
        WatchdogAppConfig config,
        ServerProcessManager server,
        HeadlessProcessManager headless,
        Action<string> log)
    {
        // Convert https:// → wss://, http:// → ws://
        var baseUri = serverUrl.TrimEnd('/');
        string wsBase;
        if (baseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            wsBase = "wss://" + baseUri[8..] + "/ws/watchdog";
        else if (baseUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            wsBase = "ws://" + baseUri[7..] + "/ws/watchdog";
        else
            wsBase = "wss://" + baseUri + "/ws/watchdog";

        // Append auth token as query parameter
        if (!string.IsNullOrEmpty(token))
            _wsUrl = wsBase + "?token=" + Uri.EscapeDataString(token);
        else
            _wsUrl = wsBase;

        _watchdogId = watchdogId;
        _watchdogName = watchdogName;
        _config = config;
        _server = server;
        _headless = headless;
        _log = log;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = ConnectLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        CloseSocket();
        SetState(State.Disconnected);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    /// <summary>
    /// Send a status update to the CC server. Called periodically from the UI poll timer.
    /// </summary>
    public async Task SendStatusAsync()
    {
        if (ConnectionState != State.Connected || _ws?.State != WebSocketState.Open)
            return;

        try
        {
            var svr = _server.GetStatus();
            var hdl = _headless.GetStatus();
            var (cpu, ramUsed, ramTotal) = SystemStats.Get();

            var msg = new
            {
                type = "status",
                watchdogId = _watchdogId,
                sptServer = new
                {
                    running = svr.Running,
                    pid = svr.Pid,
                    uptime = svr.Uptime,
                    crashes = svr.RestartCount,
                    autoRestart = svr.AutoRestartOnCrash,
                    autoStart = _config.Watchdog.AutoStartServer
                },
                headlessClient = new
                {
                    running = hdl.Running,
                    pid = hdl.Pid,
                    uptime = hdl.Uptime,
                    crashes = hdl.RestartCount,
                    autoRestart = hdl.AutoRestart,
                    autoStart = hdl.AutoStart,
                    profile = hdl.ProfileName,
                    restartAfterRaids = _config.Headless.RestartAfterRaids,
                    startDelay = $"{_config.Headless.AutoStartDelaySec}s"
                },
                system = new
                {
                    cpuPercent = cpu,
                    ramUsedGB = ramUsed,
                    ramTotalGB = ramTotal
                }
            };

            await SendJsonAsync(msg);
        }
        catch (Exception ex)
        {
            _log($"Failed to send status: {ex.Message}");
        }
    }

    // ── Connection Loop ─────────────────────────────────────────

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SetState(State.Connecting);

            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                // Set session cookie so SPT maps this connection correctly
                var scheme = _wsUrl.StartsWith("wss://") ? "https" : "http";
                var authority = new Uri(_wsUrl).Authority;
                _ws.Options.Cookies = new CookieContainer();
                _ws.Options.Cookies.Add(
                    new Uri($"{scheme}://{authority}/"),
                    new Cookie("PHPSESSID", _watchdogId));

                _log($"Connecting to {_wsUrl}...");
                await _ws.ConnectAsync(new Uri(_wsUrl), ct);

                SetState(State.Connected);
                _backoffMs = 5000; // Reset backoff on success
                _log("Connected to Command Center");

                await SendRegisterAsync(ct);
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log($"Connection failed: {ex.Message}");
            }

            CloseSocket();
            SetState(State.Disconnected);

            if (_authRejected)
            {
                _log("Stopped reconnecting — auth token was rejected. Fix token and restart Watchdog.");
                break;
            }

            if (ct.IsCancellationRequested) break;

            _log($"Reconnecting in {_backoffMs / 1000}s...");
            try { await Task.Delay(_backoffMs, ct); }
            catch (OperationCanceledException) { break; }

            // Exponential backoff: 5s → 10s → 20s → 30s cap
            _backoffMs = Math.Min(_backoffMs * 2, MaxBackoffMs);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var msgBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_ws?.CloseStatus != null && (int)_ws.CloseStatus.Value == 4001)
                    {
                        _authRejected = true;
                        _log("AUTH REJECTED — invalid or missing token. Update token in watchdog-config.json and restart.");
                    }
                    else
                    {
                        _log("Server closed connection");
                    }
                    break;
                }

                msgBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(msgBuffer.ToArray());
                    msgBuffer.SetLength(0);
                    HandleMessage(json);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                _log($"WebSocket error: {ex.Message}");
                break;
            }
        }
    }

    // ── Message Handling ────────────────────────────────────────

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "command") return;

            var target = root.TryGetProperty("target", out var tgt) ? tgt.GetString() ?? "" : "";
            var action = root.TryGetProperty("action", out var act) ? act.GetString() ?? "" : "";

            // Validate command against whitelist
            if (!ValidTargets.Contains(target) || !ValidActions.Contains(action))
            {
                _log($"Rejected invalid command: {target}.{action}");
                _ = SendCommandResultAsync(target, action, false, $"Rejected — invalid command: {target}.{action}");
                return;
            }

            _log($"Command received: {target}.{action}");
            ExecuteCommand(target, action);
        }
        catch (Exception ex)
        {
            _log($"Failed to parse message: {ex.Message}");
        }
    }

    private void ExecuteCommand(string target, string action)
    {
        bool success;
        string message;

        try
        {
            (success, message) = (target, action) switch
            {
                ("sptServer", "start") => (true, DoServerStart()),
                ("sptServer", "stop") => (true, DoServerStop()),
                ("sptServer", "restart") => (true, DoServerRestart()),
                ("headlessClient", "start") => (true, DoHeadlessStart()),
                ("headlessClient", "stop") => (true, DoHeadlessStop()),
                ("headlessClient", "restart") => (true, DoHeadlessRestart()),
                _ => (false, $"Unknown: {target}.{action}")
            };
        }
        catch (Exception ex)
        {
            success = false;
            message = $"Error: {ex.Message}";
        }

        _ = SendCommandResultAsync(target, action, success, message);
    }

    private string DoServerStart() { _server.Start(); return "Server start initiated"; }
    private string DoServerStop()
    {
        if (_headless.IsRunning) _headless.Stop();
        _server.Stop();
        return "Server stopped";
    }
    private string DoServerRestart()
    {
        if (_headless.IsRunning) _headless.Stop();
        _server.Restart();
        return "Server restart initiated";
    }
    private string DoHeadlessStart() { _headless.Start(); return "Headless start initiated"; }
    private string DoHeadlessStop() { _headless.Stop(); return "Headless stopped"; }
    private string DoHeadlessRestart() { _headless.Restart(); return "Headless restart initiated"; }

    // ── Outbound Messages ───────────────────────────────────────

    private async Task SendRegisterAsync(CancellationToken ct)
    {
        var hostname = Environment.MachineName;
        var ip = ResolveLocalIp(hostname);

        var msg = new
        {
            type = "register",
            watchdogId = _watchdogId,
            name = _watchdogName,
            hostname,
            ip,
            manages = new
            {
                sptServer = true,
                headlessClient = true
            }
        };

        await SendJsonAsync(msg, ct);
        _log($"Registered as '{_watchdogName}' ({hostname}, {ip})");
    }

    private async Task SendCommandResultAsync(string target, string action, bool success, string message)
    {
        try
        {
            var msg = new
            {
                type = "commandResult",
                watchdogId = _watchdogId,
                target,
                action,
                success,
                message
            };
            await SendJsonAsync(msg);
        }
        catch { /* best effort */ }
    }

    private async Task SendJsonAsync(object message, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private void CloseSocket()
    {
        if (_ws == null) return;
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        _ws.Dispose();
        _ws = null;
    }

    private void SetState(State state)
    {
        if (ConnectionState == state) return;
        ConnectionState = state;
        StateChanged?.Invoke(state);
    }

    private static string ResolveLocalIp(string hostname)
    {
        try
        {
            var host = Dns.GetHostEntry(hostname);
            return host.AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?
                .ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }
}
