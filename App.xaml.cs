using System.IO;
using System.Text.Json;

namespace ZSlayerCommandCenter.Launcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var watchdogConfigPath = Path.Combine(AppContext.BaseDirectory, "watchdog-config.json");
        var watchdogConfig = LoadWatchdogConfig(watchdogConfigPath);

        // Pre-render boot sound WAV while WebView2 initializes (plays on navigation complete)
        if (!watchdogConfig.Muted)
            BootSound.PreRender();

        var sptRoot = DiscoverSptRoot();
        var canManageServer = sptRoot != null;

        // Load CC shared config (only if SPT root found)
        WatchdogAppConfig config;
        string configPath;
        if (canManageServer)
        {
            configPath = Path.Combine(sptRoot!, "user", "mods", "ZSlayerCommandCenter", "config", "config.json");
            config = LoadConfig(configPath);
        }
        else
        {
            configPath = "";
            config = new WatchdogAppConfig();
        }

        var serverManager = new ServerProcessManager(config.Watchdog, sptRoot, Log);
        serverManager.Configure();

        if (canManageServer)
        {
            var showServer = !config.Watchdog.StartHidden;
            serverManager.SetConsoleVisible(showServer);
        }

        var headlessManager = new HeadlessProcessManager(
            config.Headless, sptRoot, Log,
            explicitExePath: string.IsNullOrEmpty(watchdogConfig.HeadlessExePath) ? null : watchdogConfig.HeadlessExePath,
            explicitProfileId: string.IsNullOrEmpty(watchdogConfig.HeadlessProfileId) ? null : watchdogConfig.HeadlessProfileId,
            explicitBackendUrl: string.IsNullOrEmpty(watchdogConfig.HeadlessBackendUrl) ? null : watchdogConfig.HeadlessBackendUrl);
        headlessManager.SetServerManager(serverManager);
        headlessManager.Configure();

        var canManageHeadless = headlessManager.IsAvailable;

        if (canManageServer)
        {
            var showHeadless = config.Watchdog.StartHidden ? false : config.Watchdog.ShowHeadlessConsole;
            headlessManager.SetConsoleVisible(showHeadless);
        }

        var serverUrl = DiscoverServerUrl(watchdogConfig, sptRoot, serverManager);
        headlessManager.SetServerUrl(serverUrl);

        var token = DiscoverToken(watchdogConfig, sptRoot);

        var connection = new CommandCenterConnection(
            serverUrl, watchdogConfig.WatchdogId, watchdogConfig.Name,
            token, config, serverManager, headlessManager, Log,
            canManageServer, canManageHeadless);

        var mainWindow = new MainWindow(config, configPath,
            watchdogConfig, watchdogConfigPath, sptRoot,
            serverManager, headlessManager, connection,
            canManageServer, canManageHeadless);
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
    private static string DiscoverServerUrl(WatchdogIdentityConfig wdConfig, string? sptRoot, ServerProcessManager server)
    {
        // 1. Explicit watchdog config
        if (!string.IsNullOrEmpty(wdConfig.ServerUrl))
            return wdConfig.ServerUrl;

        // 2. HeadlessConfig.json (game root, next to EFT exe — only if SPT root found)
        if (sptRoot != null)
        {
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
        }

        // 3. ServerProcessManager already parsed http.json
        if (!string.IsNullOrEmpty(server.ServerUrl))
            return server.ServerUrl;

        // 4. Fallback to localhost default
        const string fallback = "https://127.0.0.1:6969";
        Log($"No server URL discovered — defaulting to {fallback}");
        return fallback;
    }

    /// <summary>
    /// Resolve auth token: explicit watchdog-config override → watchdog-token.txt in CC mod folder.
    /// </summary>
    private static string DiscoverToken(WatchdogIdentityConfig wdConfig, string? sptRoot)
    {
        // 1. Explicit override in watchdog-config.json
        if (!string.IsNullOrEmpty(wdConfig.Token))
            return wdConfig.Token;

        // 2. Auto-discover from watchdog-token.txt written by CC server mod (only if SPT root found)
        if (sptRoot != null)
        {
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
        }

        return "";
    }

    private static void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[Watchdog] {msg}");
    }
}
