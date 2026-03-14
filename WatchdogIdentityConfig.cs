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

    [JsonPropertyName("headlessExePath")]
    public string HeadlessExePath { get; set; } = "";

    [JsonPropertyName("headlessProfileId")]
    public string HeadlessProfileId { get; set; } = "";

    [JsonPropertyName("headlessBackendUrl")]
    public string HeadlessBackendUrl { get; set; } = "";

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }
}
