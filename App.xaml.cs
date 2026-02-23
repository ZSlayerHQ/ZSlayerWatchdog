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

        var configPath = Path.Combine(sptRoot, "user", "mods", "ZSlayerCommandCenter", "config", "config.json");
        var config = LoadConfig(configPath);

        var serverManager = new ServerProcessManager(config.Watchdog, sptRoot, Log);
        serverManager.Configure();

        var headlessManager = new HeadlessProcessManager(config.Headless, sptRoot, Log);
        headlessManager.SetServerManager(serverManager);
        headlessManager.Configure();

        var mainWindow = new MainWindow(config, configPath, serverManager, headlessManager);
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

    private static void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[Watchdog] {msg}");
    }
}
