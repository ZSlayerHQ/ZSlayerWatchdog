using System.IO;
using System.Text.Json;

namespace ZSlayerCommandCenter.Launcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var sptRoot = DiscoverSptRoot();
        if (sptRoot == null)
        {
            System.Windows.MessageBox.Show(
                "Could not find SPT.Server.exe.\n\nPlace this launcher next to SPT.Server.exe or in a sibling folder.",
                "ZSlayer Watchdog", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Load CC shared config
        var configPath = Path.Combine(sptRoot, "user", "mods", "ZSlayerCommandCenter", "config", "config.json");
        var config = LoadConfig(configPath);

        // Load watchdog identity config (separate from CC config)
        var watchdogConfigPath = Path.Combine(AppContext.BaseDirectory, "watchdog-config.json");
        var watchdogConfig = LoadWatchdogConfig(watchdogConfigPath);

        var serverManager = new ServerProcessManager(config.Watchdog, sptRoot, Log);
        serverManager.Configure();

        var headlessManager = new HeadlessProcessManager(config.Headless, sptRoot, Log);
        headlessManager.SetServerManager(serverManager);
        headlessManager.Configure();

        // Discover server URL: watchdog-config → HeadlessConfig.json → http.json fallback
        var serverUrl = DiscoverServerUrl(watchdogConfig, sptRoot, serverManager);

        // Discover auth token: watchdog-config override → watchdog-token.txt auto-discovery
        var token = DiscoverToken(watchdogConfig, sptRoot);

        // Create WebSocket connection to CC server
        var connection = new CommandCenterConnection(
            serverUrl, watchdogConfig.WatchdogId, watchdogConfig.Name,
            token, config, serverManager, headlessManager, Log);

        var mainWindow = new MainWindow(config, configPath, serverManager, headlessManager, connection);
        mainWindow.Show();
    }

    private static string? DiscoverSptRoot()
    {
        var launcherDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(launcherDir, "SPT.Server.exe"),
            Path.Combine(launcherDir, "..", "SPT.Server.exe"),
            Path.Combine(launcherDir, "..", "SPT", "SPT.Server.exe"),
            Path.Combine(launcherDir, "SPT", "SPT.Server.exe"),
        };

        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full))
                return Path.GetDirectoryName(full);
        }

        return null;
    }

    private static WatchdogAppConfig LoadConfig(string configPath)
    {
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<WatchdogAppConfig>(json) ?? new WatchdogAppConfig();
            }
            catch
            {
                return new WatchdogAppConfig();
            }
        }

        return new WatchdogAppConfig();
    }

    private static WatchdogIdentityConfig LoadWatchdogConfig(string path)
    {
        WatchdogIdentityConfig config;

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<WatchdogIdentityConfig>(json) ?? new();
            }
            catch
            {
                config = new();
            }
        }
        else
        {
            config = new();
        }

        // Auto-generate watchdogId if missing
        var needsSave = false;
        if (string.IsNullOrEmpty(config.WatchdogId))
        {
            config.WatchdogId = Guid.NewGuid().ToString();
            needsSave = true;
        }

        if (needsSave || !File.Exists(path))
        {
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { /* best effort */ }
        }

        return config;
    }

    /// <summary>
    /// Resolve server URL: explicit config → HeadlessConfig.json BackendUrl → ServerProcessManager fallback.
    /// </summary>
    private static string DiscoverServerUrl(WatchdogIdentityConfig wdConfig, string sptRoot, ServerProcessManager server)
    {
        // 1. Explicit watchdog config
        if (!string.IsNullOrEmpty(wdConfig.ServerUrl))
            return wdConfig.ServerUrl;

        // 2. HeadlessConfig.json (game root, next to EFT exe)
        var gameRoot = Path.GetFullPath(Path.Combine(sptRoot, ".."));
        var headlessConfigPath = Path.Combine(gameRoot, "HeadlessConfig.json");
        if (File.Exists(headlessConfigPath))
        {
            try
            {
                var json = File.ReadAllText(headlessConfigPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("BackendUrl", out var bu))
                {
                    var url = bu.GetString();
                    if (!string.IsNullOrEmpty(url))
                        return url;
                }
            }
            catch { /* ignore */ }
        }

        // 3. ServerProcessManager already parsed http.json
        return server.ServerUrl;
    }

    /// <summary>
    /// Resolve auth token: explicit watchdog-config override → watchdog-token.txt in CC mod folder.
    /// </summary>
    private static string DiscoverToken(WatchdogIdentityConfig wdConfig, string sptRoot)
    {
        // 1. Explicit override in watchdog-config.json
        if (!string.IsNullOrEmpty(wdConfig.Token))
            return wdConfig.Token;

        // 2. Auto-discover from watchdog-token.txt written by CC server mod
        var tokenPath = Path.Combine(sptRoot, "user", "mods", "ZSlayerCommandCenter", "watchdog-token.txt");
        if (File.Exists(tokenPath))
        {
            try
            {
                var token = File.ReadAllText(tokenPath).Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    Log($"Auth token auto-discovered from {tokenPath}");
                    return token;
                }
            }
            catch { /* ignore */ }
        }

        return "";
    }

    private static void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[Watchdog] {msg}");
    }
}
