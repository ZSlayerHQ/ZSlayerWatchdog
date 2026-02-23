using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Media = System.Windows.Media;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ZSlayerCommandCenter.Launcher;

public partial class MainWindow : Window
{
    private readonly WatchdogAppConfig _config;
    private readonly string _configPath;
    private readonly ServerProcessManager _server;
    private readonly HeadlessProcessManager _headless;
    private readonly WatchdogApi _api;
    private readonly DispatcherTimer _pollTimer;
    private DispatcherTimer? _saveTimer;
    private int _attachScanCounter;
    private WinForms.NotifyIcon _trayIcon = null!;
    private WinForms.ContextMenuStrip _trayMenu = null!;
    private bool _quitting;
    private string? _updateUrl;

    // Cached brushes for code-behind status updates
    private static readonly Media.SolidColorBrush GreenBrush = new(Media.Color.FromRgb(0x4a, 0x7c, 0x59));
    private static readonly Media.SolidColorBrush RedBrush = new(Media.Color.FromRgb(0x8b, 0x3a, 0x3a));
    private static readonly Media.SolidColorBrush OrangeBrush = new(Media.Color.FromRgb(0xc8, 0x7a, 0x3e));
    private static readonly Media.SolidColorBrush PrimaryBrush = new(Media.Color.FromRgb(0xe8, 0xdc, 0xc8));
    private static readonly Media.SolidColorBrush DimmedBrush = new(Media.Color.FromRgb(0x7a, 0x70, 0x60));

    public MainWindow(WatchdogAppConfig config, string configPath,
                      ServerProcessManager server, HeadlessProcessManager headless)
    {
        _config = config;
        _configPath = configPath;
        _server = server;
        _headless = headless;
        _api = new WatchdogApi(server, headless, config, _ => { });

        InitializeComponent();
        InitializeValues();

        // Wire slider events after InitializeComponent to avoid spurious firing
        SvrTimeoutSlider.ValueChanged += SvrTimeout_ValueChanged;
        HdlRaidSlider.ValueChanged += HdlRaid_ValueChanged;

        BuildTrayIcon();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        _api.Start();
        CheckForUpdatesAsync();
        ScheduleAutoStart();
    }

    private void InitializeValues()
    {
        ApiLabel.Text = $"API: http://127.0.0.1:{_config.Watchdog.Port}";

        // Server card
        SvrAutoRstVal.IsChecked = _config.Watchdog.AutoRestartOnCrash;
        SvrAutoStartVal.IsChecked = _config.Watchdog.AutoStartServer;
        var timeout = Math.Clamp(_config.Watchdog.SessionTimeoutMin, 1, 30);
        SvrTimeoutSlider.Value = timeout;
        SvrTimeoutLabel.Text = $"{timeout} min";

        // Headless card
        HdlAutoRstVal.IsChecked = _config.Headless.AutoRestart;
        HdlAutoStartVal.IsChecked = _config.Headless.AutoStart;
        TrayToggleVal.IsChecked = _config.Watchdog.MinimizeToTray;
        HdlDelayVal.Text = $"{_config.Headless.AutoStartDelaySec}s";

        var profileName = _headless.GetStatus().ProfileName;
        BtnHdlProfile.ToolTip = string.IsNullOrEmpty(profileName) ? "No profile" : $"\U0001F916 {profileName}";

        var raids = Math.Clamp(_config.Headless.RestartAfterRaids, 0, 10);
        HdlRaidSlider.Value = raids;
        HdlRaidLabel.Text = raids == 0 ? "Off" : raids.ToString();
    }

    // ── Title bar ────────────────────────────────────────────
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.Watchdog.MinimizeToTray)
        {
            Hide();
            WindowState = WindowState.Normal;
        }
        base.OnStateChanged(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── Server buttons ───────────────────────────────────────
    private void SvrStart_Click(object sender, RoutedEventArgs e) => _server.Start();
    private void SvrStop_Click(object sender, RoutedEventArgs e) => _server.Stop();
    private void SvrRestart_Click(object sender, RoutedEventArgs e) => _server.Restart();

    // ── Headless buttons ─────────────────────────────────────
    private void HdlStart_Click(object sender, RoutedEventArgs e) => _headless.Start();
    private void HdlStop_Click(object sender, RoutedEventArgs e) => _headless.Stop();
    private void HdlRestart_Click(object sender, RoutedEventArgs e) => _headless.Restart();

    // ── Slider handlers ──────────────────────────────────────
    private void SvrTimeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var val = (int)e.NewValue;
        SvrTimeoutLabel.Text = $"{val} min";
        _config.Watchdog.SessionTimeoutMin = val;
        DebounceSaveConfig();
    }

    private void HdlRaid_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var val = (int)e.NewValue;
        HdlRaidLabel.Text = val == 0 ? "Off" : val.ToString();
        _config.Headless.RestartAfterRaids = val;
        DebounceSaveConfig();
    }

    private void TrayToggle_Click(object sender, RoutedEventArgs e)
    {
        _config.Watchdog.MinimizeToTray = TrayToggleVal.IsChecked == true;
        DebounceSaveConfig();
    }

    // ── Toggle handlers ─────────────────────────────────────
    private void SvrAutoRst_Toggle(object sender, RoutedEventArgs e)
    {
        _config.Watchdog.AutoRestartOnCrash = SvrAutoRstVal.IsChecked == true;
        DebounceSaveConfig();
    }

    private void SvrAutoStart_Toggle(object sender, RoutedEventArgs e)
    {
        _config.Watchdog.AutoStartServer = SvrAutoStartVal.IsChecked == true;
        DebounceSaveConfig();
    }

    private void HdlAutoRst_Toggle(object sender, RoutedEventArgs e)
    {
        _config.Headless.AutoRestart = HdlAutoRstVal.IsChecked == true;
        DebounceSaveConfig();
    }

    private void HdlAutoStart_Toggle(object sender, RoutedEventArgs e)
    {
        _config.Headless.AutoStart = HdlAutoStartVal.IsChecked == true;
        DebounceSaveConfig();
    }

    // ── Profile button ──────────────────────────────────────
    private void HdlProfile_Click(object sender, RoutedEventArgs e)
    {
        var profilesDir = Path.Combine(_server.SptRoot, "user", "profiles");
        if (Directory.Exists(profilesDir))
            Process.Start(new ProcessStartInfo(profilesDir) { UseShellExecute = true });
    }

    // ── Footer buttons ───────────────────────────────────────
    private void OpenCC_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://127.0.0.1:6969/zslayer/cc/")
            { UseShellExecute = true });
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _quitting = true;
        Close();
    }

    // ── Update link ──────────────────────────────────────────
    private void UpdateLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (_updateUrl is not null)
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
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

        var svr = _server.GetStatus();
        UpdateServerCard(svr);

        var hdl = _headless.GetStatus();
        UpdateHeadlessCard(hdl);

        // Headless start only allowed after server has been up long enough (same delay as auto-start)
        var serverReady = svr.Running && svr.UptimeSeconds >= _config.Headless.AutoStartDelaySec;
        BtnHdlStart.IsEnabled = serverReady && !hdl.Running;

        BtnCC.IsEnabled = svr.Running;

        var svrState = svr.Running ? "Running" : "Stopped";
        var hdlState = hdl.Running ? "Running" : "Stopped";
        _trayIcon.Text = $"ZSlayer Watchdog \u2014 Server: {svrState}, Headless: {hdlState}";
    }

    private void UpdateServerCard(WatchdogStatus svr)
    {
        if (svr.Running)
        {
            SvrStatusPill.Background = GreenBrush;
            SvrStatusText.Text = "\u25CF Running";
            SvrUptimeVal.Text = svr.Uptime;
            SvrUptimeVal.Foreground = GreenBrush;
            SvrPidVal.Text = svr.Pid?.ToString() ?? "--";
            SvrPidVal.Foreground = DimmedBrush;
        }
        else
        {
            SvrStatusPill.Background = RedBrush;
            SvrStatusText.Text = "\u25CF Stopped";
            SvrUptimeVal.Text = "--";
            SvrUptimeVal.Foreground = DimmedBrush;
            SvrPidVal.Text = "--";
            SvrPidVal.Foreground = DimmedBrush;
        }

        SvrCrashVal.Text = svr.RestartCount.ToString();
        SvrCrashVal.Foreground = svr.RestartCount switch
        {
            0 => DimmedBrush,
            1 or 2 => OrangeBrush,
            _ => RedBrush
        };

        BtnSvrStart.IsEnabled = !svr.Running;
        BtnSvrStop.IsEnabled = svr.Running;
        BtnSvrRestart.IsEnabled = svr.Running;
    }

    private void UpdateHeadlessCard(HeadlessStatusDto hdl)
    {
        if (hdl.Running)
        {
            HdlStatusPill.Background = GreenBrush;
            HdlStatusText.Text = "\u25CF Running";
            HdlUptimeVal.Text = hdl.Uptime;
            HdlUptimeVal.Foreground = GreenBrush;
            HdlPidVal.Text = hdl.Pid?.ToString() ?? "--";
            HdlPidVal.Foreground = DimmedBrush;
        }
        else
        {
            HdlStatusPill.Background = RedBrush;
            HdlStatusText.Text = "\u25CF Stopped";
            HdlUptimeVal.Text = "--";
            HdlUptimeVal.Foreground = DimmedBrush;
            HdlPidVal.Text = "--";
            HdlPidVal.Foreground = DimmedBrush;
        }

        HdlCrashVal.Text = hdl.RestartCount.ToString();
        HdlCrashVal.Foreground = hdl.RestartCount switch
        {
            0 => DimmedBrush,
            1 or 2 => OrangeBrush,
            _ => RedBrush
        };

        BtnHdlStop.IsEnabled = hdl.Running;
        BtnHdlRestart.IsEnabled = hdl.Running;
    }

    // ── System Tray ──────────────────────────────────────────
    private void BuildTrayIcon()
    {
        _trayMenu = new WinForms.ContextMenuStrip();
        _trayMenu.Renderer = new DarkMenuRenderer();

        _trayMenu.Items.Add("Show Window", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _trayMenu.Items.Add("Open Command Center", null, (_, _) =>
            Process.Start(new ProcessStartInfo("https://127.0.0.1:6969/zslayer/cc/")
                { UseShellExecute = true }));
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Restart Server", null, (_, _) => Dispatcher.Invoke(() => _server.Restart()));
        _trayMenu.Items.Add("Restart Headless", null, (_, _) => Dispatcher.Invoke(() => _headless.Restart()));
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
        _saveTimer?.Stop();
        _api.Stop();

        if (_server.IsRunning) _server.Stop();
        if (_headless.IsRunning) _headless.Stop();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        base.OnClosing(e);
    }

    // ── Update Check ─────────────────────────────────────────
    private async void CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckAsync("2.4.0");
        if (result is { available: true } update)
        {
            Dispatcher.Invoke(() =>
            {
                _updateUrl = update.url;
                UpdateLink.Text = $"\u2B06 {update.tag}";
                UpdateLink.Visibility = Visibility.Visible;
                VersionLabel.Visibility = Visibility.Collapsed;
            });
        }
    }

    // ── Auto-Start ───────────────────────────────────────────
    private void ScheduleAutoStart()
    {
        if (_config.Watchdog.AutoStartServer)
        {
            var delay = Math.Clamp(_config.Watchdog.AutoStartDelaySec, 1, 300);
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (!_server.IsRunning)
                    _server.Start();
            });
        }

        _headless.StartAutoStartTimer(() =>
        {
            var s = _server.GetStatus();
            return s.Running && s.UptimeSeconds >= _config.Headless.AutoStartDelaySec;
        });
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
