using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZSlayerCommandCenter.Launcher;

public class ServerProcessManager
{
    private readonly WatchdogSection _config;
    private readonly string _sptRoot;
    private readonly Action<string> _log;
    private Process? _process;
    private DateTime? _startedAt;
    private int _restartCount;
    private string? _lastCrashReason;
    private string _exePath = "";
    private string _workingDir = "";
    private bool _available;
    private bool _stopping;
    private CancellationTokenSource? _readinessCts;
    private readonly List<string> _consoleBuffer = new();
    private const int MaxConsoleLines = 50;

    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    { Timeout = TimeSpan.FromSeconds(3) };

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>True once the SPT webserver is actually accepting HTTP connections.</summary>
    public bool ServerReady { get; private set; }

    /// <summary>The server's backend URL read from http.json (e.g. "https://127.0.0.1:6969").</summary>
    public string ServerUrl { get; private set; } = "https://127.0.0.1:6969";

    public ServerProcessManager(WatchdogSection config, string sptRoot, Action<string> log)
    {
        _config = config;
        _sptRoot = sptRoot;
        _log = log;
    }

    public void Configure()
    {
        if (_config.SptServerExe != "auto" && File.Exists(_config.SptServerExe))
        {
            _exePath = Path.GetFullPath(_config.SptServerExe);
        }
        else
        {
            // Search relative to SPT root
            var candidates = new[]
            {
                Path.Combine(_sptRoot, "SPT.Server.exe"),
                Path.Combine(_sptRoot, "..", "SPT.Server.exe"),
                Path.Combine(_sptRoot, "SPT", "SPT.Server.exe"),
            };

            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full))
                {
                    _exePath = full;
                    break;
                }
            }
        }

        _available = !string.IsNullOrEmpty(_exePath) && File.Exists(_exePath);
        _workingDir = _available ? Path.GetDirectoryName(_exePath)! : "";

        if (_available)
        {
            _log($"SPT.Server.exe found: {_exePath}");
            ReadHttpConfig();
            TryAttachExisting();
        }
        else
        {
            _log("SPT.Server.exe not found — place launcher next to SPT.Server.exe");
        }
    }

    /// <summary>
    /// Reads SPT_Data/configs/http.json to determine the server's bound IP and port.
    /// Falls back to https://127.0.0.1:6969 if the file is missing or unreadable.
    /// </summary>
    private void ReadHttpConfig()
    {
        try
        {
            // http.json lives relative to the server exe's working directory
            var httpJsonPath = Path.Combine(_workingDir, "SPT_Data", "configs", "http.json");
            if (!File.Exists(httpJsonPath))
            {
                _log($"http.json not found at {httpJsonPath} — using default {ServerUrl}");
                return;
            }

            var json = File.ReadAllText(httpJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() : "127.0.0.1";
            var port = root.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 6969;

            // For health checks, always use 127.0.0.1 (0.0.0.0 isn't connectable)
            var connectIp = ip == "0.0.0.0" ? "127.0.0.1" : ip;
            ServerUrl = $"https://{connectIp}:{port}";
            _log($"Server URL from http.json: {ServerUrl}");
        }
        catch (Exception ex)
        {
            _log($"Failed to read http.json: {ex.Message} — using default {ServerUrl}");
        }
    }

    /// <summary>
    /// Scan running processes to detect an externally-launched instance.
    /// Only attaches if we are not already tracking a process.
    /// </summary>
    public void TryAttachExisting()
    {
        if (_stopping) return;
        if (_process != null && !_process.HasExited) return;
        if (!_available || string.IsNullOrEmpty(_exePath)) return;

        var procName = Path.GetFileNameWithoutExtension(_exePath);
        try
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    var modulePath = p.MainModule?.FileName;
                    if (modulePath != null &&
                        string.Equals(Path.GetFullPath(modulePath), Path.GetFullPath(_exePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _process = p;
                        _process.EnableRaisingEvents = true;
                        _process.Exited += OnProcessExited;
                        _stopping = false;

                        // Use the process start time for accurate uptime
                        try { _startedAt = p.StartTime.ToUniversalTime(); }
                        catch { _startedAt = DateTime.UtcNow; }

                        _log($"Attached to existing server process (PID {p.Id})");
                        StartReadinessProbe();
                        return;
                    }
                }
                catch { /* access denied on MainModule for some processes — skip */ }
            }
        }
        catch { /* GetProcessesByName can throw on restricted environments */ }
    }

    public string SptRoot => _sptRoot;

    public void Start()
    {
        if (!_available)
        {
            _log("Cannot start — SPT.Server.exe not found");
            return;
        }

        if (IsRunning) return;

        _stopping = false;

        try
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                WorkingDirectory = _workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi);
            if (_process != null)
            {
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
                _process.OutputDataReceived += OnOutputData;
                _process.ErrorDataReceived += OnOutputData;
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _startedAt = DateTime.UtcNow;
                lock (_consoleBuffer) _consoleBuffer.Clear();
                _log($"Server started (PID {_process.Id})");
                StartReadinessProbe();
            }
        }
        catch (Exception ex)
        {
            _log($"Failed to start server: {ex.Message}");
        }
    }

    public void Stop()
    {
        _stopping = true;
        CancelReadinessProbe();

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _log($"Stopping server (PID {_process.Id})...");
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                _log($"Error stopping server: {ex.Message}");
            }
        }

        _process = null;
        _startedAt = null;
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Starts a background task that polls the server's HTTP endpoint until it responds,
    /// then sets ServerReady = true.
    /// </summary>
    private void StartReadinessProbe()
    {
        CancelReadinessProbe();
        _readinessCts = new CancellationTokenSource();
        var token = _readinessCts.Token;

        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var resp = await _httpClient.GetAsync(ServerUrl, token);
                        // Any response means the webserver is up
                        ServerReady = true;
                        _log("Server webserver is ready (HTTP health check passed)");
                        return;
                    }
                    catch (TaskCanceledException) { throw; }
                    catch
                    {
                        // Connection refused / timeout — server not ready yet
                        await Task.Delay(2000, token);
                    }
                }
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    private void CancelReadinessProbe()
    {
        ServerReady = false;
        _readinessCts?.Cancel();
        _readinessCts = null;
    }

    public WatchdogStatus GetStatus()
    {
        var running = IsRunning;
        var uptimeSeconds = running && _startedAt.HasValue
            ? (long)(DateTime.UtcNow - _startedAt.Value).TotalSeconds
            : 0;

        return new WatchdogStatus
        {
            Available = _available,
            Running = running,
            Pid = running ? _process?.Id : null,
            Uptime = running ? FormatUptime(uptimeSeconds) : "",
            UptimeSeconds = uptimeSeconds,
            RestartCount = _restartCount,
            LastCrashReason = _lastCrashReason,
            AutoRestartOnCrash = _config.AutoRestartOnCrash,
            RestartDelaySec = _config.RestartDelaySec
        };
    }

    private static string FormatUptime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        lock (_consoleBuffer)
        {
            _consoleBuffer.Add(e.Data);
            while (_consoleBuffer.Count > MaxConsoleLines)
                _consoleBuffer.RemoveAt(0);
        }
    }

    public List<string> GetRecentConsoleLines(int count = 8)
    {
        lock (_consoleBuffer)
        {
            var start = Math.Max(0, _consoleBuffer.Count - count);
            return _consoleBuffer.GetRange(start, _consoleBuffer.Count - start);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        CancelReadinessProbe();
        if (_stopping) return;

        var exitCode = _process?.ExitCode;
        _lastCrashReason = $"Process exited with code {exitCode}";
        _restartCount++;
        _process = null;
        _startedAt = null;

        _log($"Server exited (code {exitCode}), crash #{_restartCount}");

        if (_config.AutoRestartOnCrash && _available)
        {
            var delay = Math.Clamp(_config.RestartDelaySec, 1, 300);
            _log($"Auto-restarting in {delay}s...");
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (!IsRunning && !_stopping)
                    Start();
            });
        }
    }
}

public class WatchdogStatus
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("uptime")]
    public string Uptime { get; set; } = "";

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    [JsonPropertyName("restartCount")]
    public int RestartCount { get; set; }

    [JsonPropertyName("lastCrashReason")]
    public string? LastCrashReason { get; set; }

    [JsonPropertyName("autoRestartOnCrash")]
    public bool AutoRestartOnCrash { get; set; }

    [JsonPropertyName("restartDelaySec")]
    public int RestartDelaySec { get; set; }
}
