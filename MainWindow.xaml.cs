using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Media = System.Windows.Media;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ZSlayerCommandCenter.Launcher;

public partial class MainWindow : Window
{
    private readonly WatchdogAppConfig _config;
    private readonly string _configPath;
    private readonly WatchdogIdentityConfig _watchdogConfig;
    private readonly string _watchdogConfigPath;
    private readonly string? _sptRoot;
    private readonly ServerProcessManager _server;
    private readonly HeadlessProcessManager _headless;
    private readonly CommandCenterConnection _connection;
    private readonly bool _canManageServer;
    private readonly bool _canManageHeadless;
    private readonly DispatcherTimer _pollTimer;
    private DispatcherTimer? _saveTimer;
    private int _attachScanCounter;
    private int _statusSendCounter;
    private WinForms.NotifyIcon _trayIcon = null!;
    private WinForms.ContextMenuStrip _trayMenu = null!;
    private bool _quitting;
    private string? _updateUrl;
    private string _tokenSource = "none";
    private bool _tokenVisible;
    private bool _webViewReady;
    private bool _pendingCrashEvent;
    private int _lastSvrCrashes;
    private int _lastHdlCrashes;

    public MainWindow(WatchdogAppConfig config, string configPath,
                      WatchdogIdentityConfig watchdogConfig, string watchdogConfigPath,
                      string? sptRoot,
                      ServerProcessManager server, HeadlessProcessManager headless,
                      CommandCenterConnection connection,
                      bool canManageServer = true, bool canManageHeadless = true)
    {
        _config = config;
        _configPath = configPath;
        _watchdogConfig = watchdogConfig;
        _watchdogConfigPath = watchdogConfigPath;
        _sptRoot = sptRoot;
        _server = server;
        _headless = headless;
        _connection = connection;
        _canManageServer = canManageServer;
        _canManageHeadless = canManageHeadless;

        InitializeComponent();
        InitializeWebView();

        BuildTrayIcon();

        // Listen for connection state changes (fires from background thread)
        _connection.StateChanged += state =>
            Dispatcher.Invoke(() => PushStateToUI());

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        _connection.Start();
        CheckForUpdatesAsync();
        ScheduleAutoStart();
    }

    // ── WebView2 Initialization ─────────────────────────────
    private async void InitializeWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // Handle messages from the HTML frontend
            webView.CoreWebView2.WebMessageReceived += WebView_MessageReceived;

            // Navigate to the bundled HTML file
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var htmlPath = Path.Combine(exeDir, "watchdog-ui.html");
            webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webViewReady = true;
            PushStateToUI();
        }
    }

    // ── Push state to HTML frontend ─────────────────────────
    private async void PushStateToUI()
    {
        if (!_webViewReady) return;

        try
        {
            var svr = _server.GetStatus();
            var hdl = _headless.GetStatus();
            var connState = _connection.ConnectionState;
            var stats = SystemStats.Get();

            var state = new
            {
                spt = new
                {
                    running = svr.Running,
                    ready = _server.ServerReady,
                    uptime = svr.Running ? svr.Uptime : "--",
                    pid = svr.Running ? (svr.Pid?.ToString() ?? "--") : "--",
                    autoRestart = _config.Watchdog.AutoRestartOnCrash,
                    autoStart = _config.Watchdog.AutoStartServer,
                    sessionTimeout = _config.Watchdog.SessionTimeoutMin,
                    crashesToday = svr.RestartCount,
                    startHidden = _config.Watchdog.StartHidden
                },
                headless = new
                {
                    running = hdl.Running,
                    ready = _headless.HeadlessReady,
                    startupFailed = _headless.StartupFailed,
                    waitingForServer = _headless.WaitingForServer,
                    uptime = hdl.Running ? hdl.Uptime : "--",
                    pid = hdl.Running ? (hdl.Pid?.ToString() ?? "--") : "--",
                    autoRestart = _config.Headless.AutoRestart,
                    autoStart = _config.Headless.AutoStart,
                    rarCount = _config.Headless.RestartAfterRaids,
                    crashesToday = hdl.RestartCount,
                    showConsole = _config.Watchdog.ShowHeadlessConsole,
                    profileId = hdl.ProfileId,
                    profileName = hdl.ProfileName
                },
                connection = new
                {
                    status = connState == CommandCenterConnection.State.Connected ? "connected" : "disconnected",
                    authOverride = !string.IsNullOrEmpty(_watchdogConfig.Token)
                },
                system = new
                {
                    cpuPercent = Math.Round(stats.CpuPercent, 1),
                    ramUsedGB = Math.Round(stats.RamUsedGB, 1),
                    ramTotalGB = Math.Round(stats.RamTotalGB, 1)
                },
                mode = new
                {
                    canManageServer = _canManageServer,
                    canManageHeadless = _canManageHeadless,
                    label = (_canManageServer, _canManageHeadless) switch
                    {
                        (true, true) => "Full",
                        (true, false) => "Server Only",
                        (false, true) => "Headless Only",
                        (false, false) => "Monitor"
                    }
                },
                minimizeToTray = _config.Watchdog.MinimizeToTray,
                crashEvent = _pendingCrashEvent
            };

            _pendingCrashEvent = false;

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await webView.CoreWebView2.ExecuteScriptAsync($"updateState({json})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PushState error: {ex.Message}");
        }
    }

    // ── Handle actions from HTML frontend ────────────────────
    private void WebView_MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var msg = JsonDocument.Parse(args.WebMessageAsJson);
            var action = msg.RootElement.GetProperty("action").GetString();
            var value = msg.RootElement.TryGetProperty("value", out var val) ? val : default;

            switch (action)
            {
                // Server controls
                case "spt_start":
                    if (!_canManageServer) break;
                    _server.Start();
                    break;
                case "spt_stop":
                    if (!_canManageServer) break;
                    if (_headless.IsRunning) _headless.Stop();
                    _server.Stop();
                    break;
                case "spt_restart":
                    if (!_canManageServer) break;
                    if (_headless.IsRunning) _headless.Stop();
                    _server.Restart();
                    break;

                // Headless controls
                case "hl_start":
                    _headless.Start();
                    break;
                case "hl_stop":
                    _headless.Stop();
                    break;
                case "hl_restart":
                    _headless.Restart();
                    break;

                // Server toggles
                case "toggle_spt_autorst":
                    _config.Watchdog.AutoRestartOnCrash = !_config.Watchdog.AutoRestartOnCrash;
                    DebounceSaveConfig();
                    break;
                case "toggle_spt_autostart":
                    _config.Watchdog.AutoStartServer = !_config.Watchdog.AutoStartServer;
                    DebounceSaveConfig();
                    break;

                // Headless toggles
                case "toggle_hl_autorst":
                    _config.Headless.AutoRestart = !_config.Headless.AutoRestart;
                    DebounceSaveConfig();
                    break;
                case "toggle_hl_autostart":
                    _config.Headless.AutoStart = !_config.Headless.AutoStart;
                    if (_config.Headless.AutoStart)
                        _headless.StartAutoStartTimer(() => _server.ServerReady);
                    else
                        _headless.CancelAutoStart();
                    DebounceSaveConfig();
                    break;
                case "set_rar_count":
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        _config.Headless.RestartAfterRaids = Math.Clamp(value.GetInt32(), 0, 6);
                        DebounceSaveConfig();
                    }
                    break;

                // Session timeout slider
                case "set_session_timeout":
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        _config.Watchdog.SessionTimeoutMin = Math.Clamp(value.GetInt32(), 1, 30);
                        DebounceSaveConfig();
                    }
                    break;

                // Auth token
                case "auth_show":
                    // Handled client-side (toggles password visibility)
                    break;
                case "auth_save":
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var token = value.GetString()?.Trim() ?? "";
                        _watchdogConfig.Token = token;
                        SaveWatchdogConfig();
                    }
                    break;
                case "auth_clear":
                    _watchdogConfig.Token = "";
                    SaveWatchdogConfig();
                    break;

                // Tray
                case "toggle_tray":
                    _config.Watchdog.MinimizeToTray = !_config.Watchdog.MinimizeToTray;
                    DebounceSaveConfig();
                    break;

                // Console window toggles
                case "toggle_start_hidden":
                    _config.Watchdog.StartHidden = !_config.Watchdog.StartHidden;
                    // Apply to both managers (takes effect on next start/restart)
                    _server.SetConsoleVisible(!_config.Watchdog.StartHidden);
                    if (_config.Watchdog.StartHidden)
                        _headless.SetConsoleVisible(false);
                    DebounceSaveConfig();
                    break;
                case "toggle_hl_console":
                    // "Headless Hidden" toggle — ON means hidden, so invert for visibility
                    _config.Watchdog.ShowHeadlessConsole = !_config.Watchdog.ShowHeadlessConsole;
                    _headless.SetConsoleVisible(_config.Watchdog.ShowHeadlessConsole);
                    DebounceSaveConfig();
                    break;

                // Window controls
                case "minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "close":
                    Close();
                    break;
                case "quit":
                    _quitting = true;
                    Close();
                    break;
                case "dragMove":
                    StartWindowDrag();
                    return; // skip PushStateToUI for drag
                case "toggleMaximize":
                    WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    break;

                // Command center
                case "open_command_center":
                    var ccUrl = _canManageServer ? _server.ServerUrl : _connection.ServerUrl;
                    Process.Start(new ProcessStartInfo($"{ccUrl}/zslayer/cc/")
                        { UseShellExecute = true });
                    break;
            }

            // Push updated state back after action
            PushStateToUI();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebMessage error: {ex.Message}");
        }
    }

    // ── Title bar drag via Win32 ──
    // WebView2 HWND sits on top of WPF (airspace), so XAML overlays don't work.
    // Instead, when HTML sends "dragMove", we release WebView2's mouse capture
    // and simulate a caption-bar mouse-down so Windows handles the drag natively.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private void StartWindowDrag()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.Watchdog.MinimizeToTray)
        {
            Hide();
            WindowState = WindowState.Normal;
        }
        base.OnStateChanged(e);
    }

    // ── Polling Timer ────────────────────────────────────────
    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        // Periodic re-scan for externally-launched processes every ~5s
        _attachScanCounter++;
        if (_attachScanCounter >= 5)
        {
            _attachScanCounter = 0;
            _server.TryAttachExisting();
            _headless.TryAttachExisting();
        }

        // Send status to CC server every ~5s
        _statusSendCounter++;
        if (_statusSendCounter >= 5)
        {
            _statusSendCounter = 0;
            _ = _connection.SendStatusAsync();
        }

        // Push state to frontend every tick
        PushStateToUI();

        var svr = _server.GetStatus();
        var hdl = _headless.GetStatus();

        // Detect crashes by watching RestartCount increases
        if (svr.RestartCount > _lastSvrCrashes || hdl.RestartCount > _lastHdlCrashes)
        {
            _pendingCrashEvent = true;
            PushStateToUI();
        }
        _lastSvrCrashes = svr.RestartCount;
        _lastHdlCrashes = hdl.RestartCount;

        var svrState = svr.Running ? "Running" : "Stopped";
        var hdlState = hdl.Running ? "Running" : "Stopped";
        _trayIcon.Text = $"ZSlayer Watchdog \u2014 Server: {svrState}, Headless: {hdlState}";
    }

    // ── System Tray ──────────────────────────────────────────
    private void BuildTrayIcon()
    {
        _trayMenu = new WinForms.ContextMenuStrip();
        _trayMenu.Renderer = new DarkMenuRenderer();

        _trayMenu.Items.Add("Show Window", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _trayMenu.Items.Add("Open Command Center", null, (_, _) =>
        {
            var url = _canManageServer ? _server.ServerUrl : _connection.ServerUrl;
            Process.Start(new ProcessStartInfo($"{url}/zslayer/cc/")
                { UseShellExecute = true });
        });
        _trayMenu.Items.Add("-");
        if (_canManageServer)
        {
            _trayMenu.Items.Add("Restart Server", null, (_, _) => Dispatcher.Invoke(() =>
            {
                if (_headless.IsRunning) _headless.Stop();
                _server.Restart();
            }));
        }
        if (_canManageHeadless)
        {
            _trayMenu.Items.Add("Restart Headless", null, (_, _) => Dispatcher.Invoke(() => _headless.Restart()));
        }
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(() => { _quitting = true; Close(); }));

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ZSlayer Watchdog",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private static Drawing.Icon LoadAppIcon()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var icoPath = Path.Combine(exeDir, "app.ico");
        return File.Exists(icoPath)
            ? new Drawing.Icon(icoPath)
            : Drawing.SystemIcons.Application;
    }

    // ── Token Management ──────────────────────────────────────
    private string DiscoverCurrentToken()
    {
        // Manual override takes priority
        if (!string.IsNullOrEmpty(_watchdogConfig.Token))
        {
            _tokenSource = "manual";
            return _watchdogConfig.Token;
        }

        // Auto-discover from watchdog-token.txt
        if (_sptRoot == null)
        {
            _tokenSource = "none";
            return "";
        }
        var tokenPath = Path.Combine(_sptRoot, "user", "mods", "ZSlayerCommandCenter", "watchdog-token.txt");
        if (File.Exists(tokenPath))
        {
            try
            {
                var token = File.ReadAllText(tokenPath).Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    _tokenSource = "auto";
                    return token;
                }
            }
            catch { /* ignore */ }
        }

        _tokenSource = "none";
        return "";
    }

    private void SaveWatchdogConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_watchdogConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_watchdogConfigPath, json);
        }
        catch { /* best effort */ }
    }

    // ── Config Save (debounced) ──────────────────────────────
    private void DebounceSaveConfig()
    {
        _saveTimer?.Stop();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveConfig();
        };
        _saveTimer.Start();
    }

    private void SaveConfig()
    {
        if (string.IsNullOrEmpty(_configPath)) return;
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { /* best effort */ }
    }

    // ── Window Close → Tray ──────────────────────────────────
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting && _config.Watchdog.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _quitting = true;

        _pollTimer.Stop();
        // Flush any pending debounced save before shutting down
        _saveTimer?.Stop();
        SaveConfig();
        _connection.Stop();

        if (_canManageHeadless && _headless.IsRunning) _headless.Stop();
        if (_canManageServer && _server.IsRunning) _server.Stop();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        base.OnClosing(e);
    }

    // ── Update Check ─────────────────────────────────────────
    private async void CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckAsync(WatchdogVersion.Version);
        if (result is { available: true } update)
        {
            _updateUrl = update.url;
        }
    }

    // ── Auto-Start ───────────────────────────────────────────
    private void ScheduleAutoStart()
    {
        if (_canManageServer && _config.Watchdog.AutoStartServer)
        {
            var delay = Math.Clamp(_config.Watchdog.AutoStartDelaySec, 1, 300);
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (!_server.IsRunning)
                    _server.Start();
            });
        }

        if (_canManageHeadless)
            _headless.StartAutoStartTimer(() => _server.ServerReady);
    }

    // ── Dark Context Menu Renderer (for tray) ────────────────
    private class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color BgCard = Drawing.ColorTranslator.FromHtml("#242018");
        private static readonly Drawing.Color Hover = Drawing.ColorTranslator.FromHtml("#352f28");
        private static readonly Drawing.Color TextClr = Drawing.ColorTranslator.FromHtml("#e8dcc8");
        private static readonly Drawing.Color BorderClr = Drawing.Color.FromArgb(56, 49, 33);

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            var rc = new Drawing.Rectangle(Drawing.Point.Empty, e.Item.Size);
            var color = e.Item.Selected ? Hover : BgCard;
            using var brush = new Drawing.SolidBrush(color);
            e.Graphics.FillRectangle(brush, rc);
        }

        protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(BgCard);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = TextClr;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            var rc = new Drawing.Rectangle(Drawing.Point.Empty, e.Item.Size);
            using var brush = new Drawing.SolidBrush(BgCard);
            e.Graphics.FillRectangle(brush, rc);
            using var pen = new Drawing.Pen(BorderClr);
            e.Graphics.DrawLine(pen, 4, rc.Height / 2, rc.Width - 4, rc.Height / 2);
        }
    }
}
