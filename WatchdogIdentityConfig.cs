using System.Text.Json.Serialization;

namespace ZSlayerCommandCenter.Launcher;

/// <summary>
/// Watchdog-specific identity config, stored in watchdog-config.json next to the exe.
/// Separate from the CC shared config.json.
/// </summary>
public class WatchdogIdentityConfig
{
    [JsonPropertyName("watchdogId")]
    public string WatchdogId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Watchdog";

    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>Manual SPT root override (empty = auto-detect).</summary>
    [JsonPropertyName("sptRootPath")]
    public string SptRootPath { get; set; } = "";

    /// <summary>Multi-headless client configurations. Replaces legacy single-headless fields.</summary>
    [JsonPropertyName("headlessClients")]
    public List<HeadlessClientConfig> HeadlessClients { get; set; } = [];

    // Legacy fields — kept for one-time migration, then cleared
    [JsonPropertyName("headlessExePath")]
    public string HeadlessExePath { get; set; } = "";

    [JsonPropertyName("headlessProfileId")]
    public string HeadlessProfileId { get; set; } = "";

    [JsonPropertyName("headlessBackendUrl")]
    public string HeadlessBackendUrl { get; set; } = "";

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }
}

public class HeadlessClientConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Headless 1";

    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("backendUrl")]
    public string BackendUrl { get; set; } = "";

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; } = false;

    [JsonPropertyName("autoRestart")]
    public bool AutoRestart { get; set; } = true;

    [JsonPropertyName("autoStartDelaySec")]
    public int AutoStartDelaySec { get; set; } = 30;

    [JsonPropertyName("restartAfterRaids")]
    public int RestartAfterRaids { get; set; } = 0;
}
