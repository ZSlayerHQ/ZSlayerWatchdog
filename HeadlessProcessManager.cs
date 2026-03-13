using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZSlayerCommandCenter.Launcher;

public class HeadlessProcessManager
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_APPWINDOW = 0x40000;

    private readonly HeadlessSection _config;
    private readonly string? _sptRoot;
    private readonly Action<string> _log;
    private readonly string? _explicitExePath;
    private readonly string? _explicitProfileId;
    private readonly string? _explicitBackendUrl;
    private string? _serverUrl;
    private string? _cachedProfileName;
    private ServerProcessManager? _serverManager;
    private Process? _process;
    private DateTime? _startedAt;
    private int _restartCount;
    private string? _lastCrashReason;
    private string _exePath = "";
    private string _workingDir = "";
    private bool _available;
    private bool _stopping;
    private CancellationTokenSource? _autoStartCts;
    private CancellationTokenSource? _rarCts;
    private CancellationTokenSource? _readinessCts;
    private bool _startupFailed;
    // Only the Fika websocket marker means the headless is genuinely connected and ready.
    // The telemetry plugin loads much earlier during BepInEx chainloader — NOT a readiness signal.
    private const string FikaHeadlessMarker = "Connected to HeadlessWebSocket";
    private const int ReadinessTimeoutSeconds = 180;
    private static readonly string[] StartupErrorMarkers =
    [
        "A PATCH IN SPTCustomPlugin FAILED",
        "SUBSEQUENT PATCHES HAVE NOT LOADED"
    ];
    private readonly List<string> _consoleBuffer = new();
    private const int MaxConsoleLines = 50;
    private bool _consoleVisible = true;
    private readonly List<IntPtr> _windowHandles = new();
    private bool _waitingForServer;

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>True when auto-start is pending and waiting for server readiness.</summary>
    public bool WaitingForServer => _waitingForServer;

    /// <summary>True once the headless telemetry plugin has reported active (fully loaded).</summary>
    public bool HeadlessReady { get; private set; }

    /// <summary>True if the headless process exited before becoming ready (startup failure).</summary>
    public bool StartupFailed => _startupFailed;

    public HeadlessProcessManager(HeadlessSection config, string? sptRoot, Action<string> log,
        string? explicitExePath = null, string? explicitProfileId = null,
        string? explicitBackendUrl = null, string? serverUrl = null)
    {
        _config = config;
        _sptRoot = sptRoot;
        _log = log;
        _explicitExePath = explicitExePath;
        _explicitProfileId = explicitProfileId;
        _explicitBackendUrl = explicitBackendUrl;
        _serverUrl = serverUrl;
    }

    /// <summary>Wire up the server manager reference so we can read ServerUrl at launch time.</summary>
    public void SetServerManager(ServerProcessManager server) => _serverManager = server;

    public bool IsAvailable => _available;

    public void SetServerUrl(string url) => _serverUrl = url;

    public void NotifyRemoteReady(string sourceId)
    {
        if (!string.IsNullOrEmpty(_config.ProfileId) &&
            sourceId == _config.ProfileId &&
            IsRunning && !HeadlessReady)
        {
            HeadlessReady = true;
            _startupFailed = false;
            _log("Headless fully loaded (detected via remote telemetry hello)");
        }
    }

    public void Configure()
    {
        // 3-tier EXE resolution: explicit > config.ExePath > SPT root discovery
        if (!string.IsNullOrEmpty(_explicitExePath) && File.Exists(_explicitExePath))
        {
            _exePath = _explicitExePath;
        }
        else if (!string.IsNullOrEmpty(_config.ExePath) && File.Exists(_config.ExePath))
        {
            _exePath = _config.ExePath;
        }
        else if (!string.IsNullOrEmpty(_sptRoot))
        {
            var gameRoot = Path.GetFullPath(Path.Combine(_sptRoot, ".."));
            var candidate = Path.Combine(gameRoot, "EscapeFromTarkov.exe");
            if (File.Exists(candidate))
                _exePath = candidate;
        }

        _available = !string.IsNullOrEmpty(_exePath) && File.Exists(_exePath);
        _workingDir = _available ? Path.GetDirectoryName(_exePath)! : "";

        if (_available)
            _log($"EscapeFromTarkov.exe found: {_exePath}");
        else
            _log("EscapeFromTarkov.exe not found — headless management unavailable");

        // ProfileId resolution: explicit > HeadlessConfig.json > config.ProfileId
        if (!string.IsNullOrEmpty(_explicitProfileId))
        {
            _config.ProfileId = _explicitProfileId;
            _log("Using explicit headless profileId from watchdog-config.json");
        }
        else if (_available && string.IsNullOrEmpty(_config.ProfileId))
        {
            var headlessConfigPath = Path.Combine(_workingDir, "HeadlessConfig.json");
            if (File.Exists(headlessConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(headlessConfigPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ProfileId", out var pid))
                    {
                        _config.ProfileId = pid.GetString() ?? "";
                        if (!string.IsNullOrEmpty(_config.ProfileId))
                            _log("Auto-read headless profileId from HeadlessConfig.json");
                    }
                }
                catch { /* ignore parse errors */ }
            }
        }

        if (_available)
            TryAttachExisting();
    }

    /// <summary>
    /// Scan running processes to detect an externally-launched headless instance.
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

                        _log($"Attached to existing headless process (PID {p.Id})");
                        _startupFailed = false;
                        StartReadinessProbe();
                        return;
                    }
                }
                catch { /* access denied on MainModule for some processes — skip */ }
            }
        }
        catch { /* GetProcessesByName can throw on restricted environments */ }
    }

    public void CancelAutoStart()
    {
        _autoStartCts?.Cancel();
        _autoStartCts = null;
        _waitingForServer = false;
    }

    public void StartAutoStartTimer(Func<bool> isServerReady)
    {
        if (!_config.AutoStart || !_available || string.IsNullOrEmpty(_config.ProfileId))
            return;

        _autoStartCts = new CancellationTokenSource();
        var token = _autoStartCts.Token;
        _waitingForServer = true;

        _log("Headless auto-start scheduled (waiting for server readiness)...");

        Task.Run(async () =>
        {
            try
            {
                // Wait for server to be ready (caller checks uptime threshold)
                while (!isServerReady() && !token.IsCancellationRequested)
                    await Task.Delay(1000, token);

                _waitingForServer = false;
                if (!token.IsCancellationRequested && !IsRunning)
                    Start();
            }
            catch (TaskCanceledException) { _waitingForServer = false; }
        }, token);
    }

    public HeadlessStatusDto Start()
    {
        if (!_available)
            return GetStatus("EscapeFromTarkov.exe not found");

        if (string.IsNullOrEmpty(_config.ProfileId))
            return GetStatus("Profile ID not configured");

        if (IsRunning)
            return GetStatus();

        _stopping = false;
        HeadlessReady = false;
        _startupFailed = false;

        var backendUrl = _explicitBackendUrl
            ?? (_serverManager != null && !string.IsNullOrEmpty(_serverManager.ServerUrl) ? _serverManager.ServerUrl : null)
            ?? _serverUrl
            ?? "https://127.0.0.1:6969";
        var args = $"-token={_config.ProfileId} " +
                   $"-config={{'BackendUrl':'{backendUrl}','Version':'live'}} " +
                   "-nographics -batchmode --enable-console true";

        try
        {
            var psi = new ProcessStartInfo(_exePath, args)
            {
                WorkingDirectory = _workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = !_consoleVisible
            };

            _process = Process.Start(psi);
            if (_process != null)
            {
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
                _startedAt = DateTime.UtcNow;

                _process.OutputDataReceived += OnOutputData;
                _process.ErrorDataReceived += OnOutputData;
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                lock (_consoleBuffer) _consoleBuffer.Clear();
                StartReadinessProbe();

                _log($"Headless started (PID {_process.Id})");

                // Apply console visibility — retry until BepInEx console appears
                if (!_consoleVisible)
                {
                    Task.Run(async () =>
                    {
                        // BepInEx allocates its console after process start; may take several seconds
                        for (int attempt = 0; attempt < 10; attempt++)
                        {
                            await Task.Delay(attempt < 3 ? 500 : 1000);
                            if (_process == null || _process.HasExited || _consoleVisible) return;
                            _windowHandles.Clear();
                            DiscoverWindows();
                            if (_windowHandles.Count > 0)
                            {
                                ApplyConsoleVisibility();
                                return;
                            }
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Failed to start headless: {ex.Message}");
            return GetStatus($"Failed to start: {ex.Message}");
        }

        return GetStatus();
    }

    public HeadlessStatusDto Stop()
    {
        _stopping = true;
        _autoStartCts?.Cancel();
        _rarCts?.Cancel();
        CancelReadinessProbe();

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _log($"Stopping headless (PID {_process.Id})...");
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                _log($"Error stopping headless: {ex.Message}");
            }
        }

        _process = null;
        _startedAt = null;
        _windowHandles.Clear();
        HeadlessReady = false;
        _startupFailed = false;
        return GetStatus();
    }

    public void ScheduleRaidRestart()
    {
        if (_config.RestartAfterRaids <= 0 || !IsRunning || _stopping)
        {
            _log("RAR: Skipped — disabled, not running, or already stopping");
            return;
        }

        // Cancel any previous pending RAR
        _rarCts?.Cancel();
        _rarCts = new CancellationTokenSource();
        var token = _rarCts.Token;

        _log("RAR: Raid ended, restarting headless in 30s...");

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(30_000, token);
                if (IsRunning && !_stopping)
                {
                    _log("RAR: Restarting headless now");
                    Restart();
                }
            }
            catch (TaskCanceledException)
            {
                _log("RAR: Restart cancelled");
            }
        }, token);
    }

    public HeadlessStatusDto Restart()
    {
        Stop();
        return Start();
    }

    public void SetConsoleVisible(bool visible)
    {
        _consoleVisible = visible;
        ApplyConsoleVisibility();
    }

    private void DiscoverWindows()
    {
        if (_process == null || _process.HasExited) return;
        _windowHandles.Clear();
        var pid = (uint)_process.Id;

        // Only get the console window (BepInEx) — do NOT enumerate all process windows
        // (EFT creates Default IME, MFC, and game windows that should never be touched)
        FreeConsole();
        if (AttachConsole(pid))
        {
            var hConsole = GetConsoleWindow();
            if (hConsole != IntPtr.Zero)
                _windowHandles.Add(hConsole);
            FreeConsole();
        }
    }

    private void ApplyConsoleVisibility()
    {
        if (_process == null || _process.HasExited) return;
        try
        {
            if (_windowHandles.Count == 0)
                DiscoverWindows();

            foreach (var hWnd in _windowHandles)
            {
                if (_consoleVisible)
                {
                    var style = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                    style &= ~WS_EX_TOOLWINDOW;
                    style |= WS_EX_APPWINDOW;
                    SetWindowLongPtr(hWnd, GWL_EXSTYLE, (IntPtr)style);
                    ShowWindow(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                }
                else
                {
                    ShowWindow(hWnd, SW_HIDE);
                    var style = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                    style |= WS_EX_TOOLWINDOW;
                    style &= ~WS_EX_APPWINDOW;
                    SetWindowLongPtr(hWnd, GWL_EXSTYLE, (IntPtr)style);
                }
            }
        }
        catch { /* process may have exited */ }
    }

    public HeadlessStatusDto GetStatus(string? error = null)
    {
        var running = IsRunning;
        var uptimeSeconds = running && _startedAt.HasValue
            ? (long)(DateTime.UtcNow - _startedAt.Value).TotalSeconds
            : 0;

        return new HeadlessStatusDto
        {
            Available = _available,
            Running = running,
            Pid = running ? _process?.Id : null,
            Uptime = running ? FormatUptime(uptimeSeconds) : "",
            UptimeSeconds = uptimeSeconds,
            RestartCount = _restartCount,
            LastCrashReason = error ?? _lastCrashReason,
            AutoStart = _config.AutoStart,
            AutoStartDelaySec = _config.AutoStartDelaySec,
            AutoRestart = _config.AutoRestart,
            ProfileId = _config.ProfileId,
            ProfileName = _cachedProfileName ?? ResolveProfileName(_config.ProfileId),
            ExePath = _exePath
        };
    }

    private string ResolveProfileName(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return "";
        if (string.IsNullOrEmpty(_sptRoot)) return "";
        try
        {
            var profilePath = Path.Combine(_sptRoot, "user", "profiles", $"{profileId}.json");
            if (!File.Exists(profilePath)) return "";

            var json = File.ReadAllText(profilePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("characters", out var chars) &&
                chars.TryGetProperty("pmc", out var pmc) &&
                pmc.TryGetProperty("Info", out var info) &&
                info.TryGetProperty("Nickname", out var nick))
            {
                return nick.GetString() ?? "";
            }
        }
        catch { /* ignore */ }
        return "";
    }

    public async Task ResolveProfileNameAsync(string profileId, string serverUrl)
    {
        if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(serverUrl)) return;

        // Try local file first
        var localName = ResolveProfileName(profileId);
        if (!string.IsNullOrEmpty(localName))
        {
            _cachedProfileName = localName;
            return;
        }

        // HTTP fallback to remote server
        try
        {
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            })
            { Timeout = TimeSpan.FromSeconds(5) };

            var resp = await http.GetStringAsync($"{serverUrl.TrimEnd('/')}/zslayer/cc/profiles");
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("profiles", out var profiles) &&
                profiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in profiles.EnumerateArray())
                {
                    var sid = p.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                    if (sid == profileId)
                    {
                        var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (!string.IsNullOrEmpty(name))
                        {
                            _cachedProfileName = name;
                            _log($"Resolved headless profile name via HTTP: {name}");
                        }
                        return;
                    }
                }
            }
        }
        catch { /* remote server not reachable yet — will retry later */ }
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

    /// <summary>
    /// Polls the BepInEx log file for readiness/error markers.
    /// Skips stale content from previous sessions by tracking the file offset at probe start.
    /// </summary>
    private static readonly System.Net.Http.HttpClient _telemetryHttpClient = new(new System.Net.Http.HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    private void StartReadinessProbe()
    {
        CancelReadinessProbe();
        _readinessCts = new CancellationTokenSource();
        var token = _readinessCts.Token;
        var logPath = Path.Combine(_workingDir, "BepInEx", "LogOutput.log");

        Task.Run(async () =>
        {
            var elapsed = 0;
            var logFileFound = false;

            // Snapshot the current log file size so we only scan NEW content.
            long startOffset = 0;
            try
            {
                if (File.Exists(logPath))
                {
                    startOffset = new FileInfo(logPath).Length;
                    logFileFound = true;
                }
            }
            catch { /* file may not exist yet */ }

            try
            {
                while (!token.IsCancellationRequested && elapsed < ReadinessTimeoutSeconds)
                {
                    if (HeadlessReady) return;

                    // After 10s with no log file, switch to telemetry polling
                    if (!logFileFound && elapsed >= 10 && !string.IsNullOrEmpty(_serverUrl))
                    {
                        if (await PollTelemetryForReadiness(token))
                            return;
                        await Task.Delay(2000, token);
                        elapsed += 2;
                        continue;
                    }

                    if (File.Exists(logPath))
                    {
                        logFileFound = true;
                        try
                        {
                            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                            if (fs.Length < startOffset)
                                startOffset = 0;

                            fs.Seek(startOffset, SeekOrigin.Begin);
                            using var reader = new StreamReader(fs);
                            var content = await reader.ReadToEndAsync(token);

                            if (content.Length > 0)
                            {
                                if (content.Contains(FikaHeadlessMarker))
                                {
                                    if (!HeadlessReady)
                                    {
                                        HeadlessReady = true;
                                        _startupFailed = false;
                                        _log("Headless fully loaded (detected via log file)");
                                    }
                                    return;
                                }

                                foreach (var marker in StartupErrorMarkers)
                                {
                                    if (content.Contains(marker))
                                    {
                                        _startupFailed = true;
                                        _lastCrashReason = $"Startup error: {marker}";
                                        _log($"Headless startup failure detected: {marker}");
                                        return;
                                    }
                                }
                            }
                        }
                        catch (IOException) { /* file locked momentarily — retry */ }
                    }

                    await Task.Delay(2000, token);
                    elapsed += 2;
                }

                if (!token.IsCancellationRequested && !HeadlessReady)
                {
                    _startupFailed = true;
                    _lastCrashReason = "Readiness timeout — headless never fully loaded";
                    _log("Headless readiness timeout — marking as failed");
                }
            }
            catch (TaskCanceledException) { /* probe cancelled — normal */ }
        }, token);
    }

    private async Task<bool> PollTelemetryForReadiness(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_serverUrl) || string.IsNullOrEmpty(_config.ProfileId))
            return false;

        try
        {
            var url = $"{_serverUrl.TrimEnd('/')}/zslayer/cc/telemetry/current";
            var resp = await _telemetryHttpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(resp);

            if (doc.RootElement.TryGetProperty("sources", out var sources) &&
                sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var src in sources.EnumerateArray())
                {
                    var srcId = src.TryGetProperty("sourceId", out var s) ? s.GetString() : null;
                    var status = src.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (srcId == _config.ProfileId && status != "idle")
                    {
                        HeadlessReady = true;
                        _startupFailed = false;
                        _log("Headless fully loaded (detected via telemetry endpoint)");
                        return true;
                    }
                }
            }
        }
        catch { /* server not reachable or telemetry not available yet */ }
        return false;
    }

    private void CancelReadinessProbe()
    {
        _readinessCts?.Cancel();
        _readinessCts = null;
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

        // Detect headless fully loaded via known markers
        if (!HeadlessReady && (e.Data.Contains(FikaHeadlessMarker)))
        {
            HeadlessReady = true;
            _startupFailed = false;
            CancelReadinessProbe();
            _log("Headless fully loaded (detected via stdout)");
        }

        // Detect critical startup errors via stdout
        if (!HeadlessReady && !_startupFailed)
        {
            foreach (var marker in StartupErrorMarkers)
            {
                if (e.Data.Contains(marker))
                {
                    _startupFailed = true;
                    _lastCrashReason = $"Startup error: {marker}";
                    CancelReadinessProbe();
                    _log($"Headless startup failure detected: {marker}");
                    break;
                }
            }
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
        _rarCts?.Cancel(); // Cancel any pending RAR — process already exited
        CancelReadinessProbe();

        if (_stopping) return;

        // If process exited before becoming ready, it's a startup failure
        if (!HeadlessReady)
            _startupFailed = true;
        HeadlessReady = false;

        var exitCode = _process?.ExitCode;
        _lastCrashReason = $"Process exited with code {exitCode}";
        _restartCount++;
        _process = null;
        _startedAt = null;
        _windowHandles.Clear();

        _log($"Headless exited (code {exitCode}), crash #{_restartCount}");

        if (_config.AutoRestart && _available && !string.IsNullOrEmpty(_config.ProfileId))
        {
            _log("Auto-restarting headless in 5s...");
            Task.Run(async () =>
            {
                await Task.Delay(5000);
                if (!IsRunning && !_stopping)
                    Start();
            });
        }
    }
}

public class HeadlessStatusDto
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

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("autoStartDelaySec")]
    public int AutoStartDelaySec { get; set; }

    [JsonPropertyName("autoRestart")]
    public bool AutoRestart { get; set; }

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("profileName")]
    public string ProfileName { get; set; } = "";

    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";
}
