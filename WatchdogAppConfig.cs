using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZSlayerCommandCenter.Launcher;

/// <summary>
/// Maps the CC mod's config.json — only the fields the watchdog needs.
/// JsonExtensionData preserves all other sections when writing back.
/// </summary>
public class WatchdogAppConfig
{
    [JsonPropertyName("watchdog")]
    public WatchdogSection Watchdog { get; set; } = new();

    [JsonPropertyName("headless")]
    public HeadlessSection Headless { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class WatchdogSection
{
    [JsonPropertyName("sptServerExe")]
    public string SptServerExe { get; set; } = "auto";

    [JsonPropertyName("autoStartServer")]
    public bool AutoStartServer { get; set; } = true;

    [JsonPropertyName("autoStartDelaySec")]
    public int AutoStartDelaySec { get; set; } = 3;

    [JsonPropertyName("autoRestartOnCrash")]
    public bool AutoRestartOnCrash { get; set; } = true;

    [JsonPropertyName("restartDelaySec")]
    public int RestartDelaySec { get; set; } = 5;

    [JsonPropertyName("sessionTimeoutMin")]
    public int SessionTimeoutMin { get; set; } = 5;

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = false;

    [JsonPropertyName("startHidden")]
    public bool StartHidden { get; set; } = false;

    [JsonPropertyName("showHeadlessConsole")]
    public bool ShowHeadlessConsole { get; set; } = true;
}

public class HeadlessSection
{
    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; } = false;

    [JsonPropertyName("autoStartDelaySec")]
    public int AutoStartDelaySec { get; set; } = 30;

    [JsonPropertyName("autoRestart")]
    public bool AutoRestart { get; set; } = true;

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    [JsonPropertyName("restartAfterRaids")]
    public int RestartAfterRaids { get; set; } = 0;
}
