using System.IO;
using System.Text.Json;

namespace ZSlayerCommandCenter.Launcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global unhandled exception handler to surface crashes
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            System.Windows.MessageBox.Show(
                $"Fatal error:\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                "ZSlayer Watchdog — Crash", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                $"UI error:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "ZSlayer Watchdog — Crash", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {

        var watchdogConfigPath = Path.Combine(AppContext.BaseDirectory, "watchdog-config.json");
        var watchdogConfig = LoadWatchdogConfig(watchdogConfigPath);

        // Pre-render boot sound WAV while WebView2 initializes (plays on navigation complete)
        if (!watchdogConfig.Muted)
            BootSound.PreRender();

        var sptRoot = DiscoverSptRoot(watchdogConfig);
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

        // Migrate legacy single-headless config → headlessClients list
        MigrateHeadlessConfig(watchdogConfig, config.Headless, watchdogConfigPath);

        var serverUrl = DiscoverServerUrl(watchdogConfig, sptRoot, serverManager);
        var token = DiscoverToken(watchdogConfig, sptRoot);

        // Create one HeadlessProcessManager per configured headless client
        var headlessManagers = new List<HeadlessProcessManager>();
        foreach (var hc in watchdogConfig.HeadlessClients)
        {
            var mgr = new HeadlessProcessManager(
                new HeadlessSection
                {
                    AutoStart = hc.AutoStart,
                    AutoRestart = hc.AutoRestart,
                    AutoStartDelaySec = hc.AutoStartDelaySec,
                    ProfileId = hc.ProfileId,
                    ExePath = hc.ExePath,
                    RestartAfterRaids = hc.RestartAfterRaids
                },
                sptRoot, Log,
                explicitExePath: string.IsNullOrEmpty(hc.ExePath) ? null : hc.ExePath,
                explicitProfileId: string.IsNullOrEmpty(hc.ProfileId) ? null : hc.ProfileId,
                explicitBackendUrl: string.IsNullOrEmpty(hc.BackendUrl) ? null : hc.BackendUrl)
            {
                InstanceId = hc.Id,
                InstanceName = hc.Name
            };
            mgr.SetServerManager(serverManager);
            mgr.Configure();
            mgr.SetServerUrl(serverUrl);

            if (canManageServer)
            {
                var showHeadless = config.Watchdog.StartHidden ? false : config.Watchdog.ShowHeadlessConsole;
                mgr.SetConsoleVisible(showHeadless);
            }

            headlessManagers.Add(mgr);
        }

        var canManageHeadless = headlessManagers.Any(m => m.IsAvailable);

        var connection = new CommandCenterConnection(
            serverUrl, watchdogConfig.WatchdogId, watchdogConfig.Name,
            token, config, serverManager, headlessManagers, Log,
            canManageServer, canManageHeadless);

        var mainWindow = new MainWindow(config, configPath,
            watchdogConfig, watchdogConfigPath, sptRoot,
            serverManager, headlessManagers, connection,
            canManageServer, canManageHeadless);
        mainWindow.Show();

        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Startup failed:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "ZSlayer Watchdog — Startup Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? DiscoverSptRoot(WatchdogIdentityConfig wdConfig)
    {
        // 1. Manual override from watchdog-config.json
        if (!string.IsNullOrEmpty(wdConfig.SptRootPath))
        {
            var serverExe = Path.Combine(wdConfig.SptRootPath, "SPT.Server.exe");
            if (File.Exists(serverExe))
            {
                Log($"SPT root from manual override: {wdConfig.SptRootPath}");
                return Path.GetFullPath(wdConfig.SptRootPath);
            }
            Log($"Manual SPT root path invalid (SPT.Server.exe not found): {wdConfig.SptRootPath}");
        }

        // 2. Auto-detect from launcher directory
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

    /// <summary>
    /// Migrate legacy single-headless fields to the new headlessClients list.
    /// Runs once on first launch after upgrade.
    /// </summary>
    private static void MigrateHeadlessConfig(WatchdogIdentityConfig wdConfig, HeadlessSection sharedHeadless, string configPath)
    {
        if (wdConfig.HeadlessClients.Count > 0)
            return; // Already migrated or user-configured

        var hasLegacyWd = !string.IsNullOrEmpty(wdConfig.HeadlessExePath) ||
                          !string.IsNullOrEmpty(wdConfig.HeadlessProfileId) ||
                          !string.IsNullOrEmpty(wdConfig.HeadlessBackendUrl);
        var hasShared = !string.IsNullOrEmpty(sharedHeadless.ProfileId);

        if (!hasLegacyWd && !hasShared)
        {
            // No existing headless config — create one empty default entry
            wdConfig.HeadlessClients.Add(new HeadlessClientConfig { Name = "Headless 1" });
            SaveWatchdogConfigStatic(wdConfig, configPath);
            Log("Created default headless client entry");
            return;
        }

        // Migrate from legacy fields
        var migrated = new HeadlessClientConfig
        {
            Name = "Headless 1",
            ExePath = wdConfig.HeadlessExePath,
            ProfileId = !string.IsNullOrEmpty(wdConfig.HeadlessProfileId) ? wdConfig.HeadlessProfileId : sharedHeadless.ProfileId,
            BackendUrl = wdConfig.HeadlessBackendUrl,
            AutoStart = sharedHeadless.AutoStart,
            AutoRestart = sharedHeadless.AutoRestart,
            AutoStartDelaySec = sharedHeadless.AutoStartDelaySec,
            RestartAfterRaids = sharedHeadless.RestartAfterRaids
        };

        wdConfig.HeadlessClients.Add(migrated);

        // Clear legacy fields
        wdConfig.HeadlessExePath = "";
        wdConfig.HeadlessProfileId = "";
        wdConfig.HeadlessBackendUrl = "";

        SaveWatchdogConfigStatic(wdConfig, configPath);
        Log("Migrated legacy headless config to headlessClients[0]");
    }

    private static void SaveWatchdogConfigStatic(WatchdogIdentityConfig config, string path)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { /* best effort */ }
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
