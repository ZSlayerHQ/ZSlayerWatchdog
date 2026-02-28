using System.Text.Json;

namespace ZSlayerCommandCenter.Launcher;

public static class UpdateChecker
{
    public static async Task<(bool available, string tag, string url)?> CheckAsync(string currentVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZSlayerWatchdog/1.0");

            var json = await http.GetStringAsync(
                "https://api.github.com/repos/ZSlayerHQ/ZSlayerWatchdog/releases/latest");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var url = root.GetProperty("html_url").GetString() ?? "";

            // Normalize: strip leading 'v' for comparison
            var remote = tag.TrimStart('v');
            var local = currentVersion.TrimStart('v');

            if (Version.TryParse(remote, out var remoteVer) &&
                Version.TryParse(local, out var localVer) &&
                remoteVer > localVer)
            {
                return (true, tag, url);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
