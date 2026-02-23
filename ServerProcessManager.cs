using System.Diagnostics;
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

    public bool IsRunning => _process != null && !_process.HasExited;

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
            _log($"SPT.Server.exe found: {_exePath}");
        else
            _log("SPT.Server.exe not found — place launcher next to SPT.Server.exe");
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
                UseShellExecute = true
            };

            _process = Process.Start(psi);
            if (_process != null)
            {
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
                _startedAt = DateTime.UtcNow;
                _log($"Server started (PID {_process.Id})");
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

    private void OnProcessExited(object? sender, EventArgs e)
    {
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
