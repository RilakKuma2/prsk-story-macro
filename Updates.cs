using System.Text.Json;

internal static partial class Program
{
    const string ReleaseRepo = "RilakKuma2/prsk-story-macro";
    static Version CurrentVersion => Normalize(typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0));
    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
    sealed record UpdateInfo(string Current, string? Latest, string Status, string? DownloadUrl);

    static async Task<UpdateInfo> ReadUpdateInfo()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiStorySkip/" + CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(CurrentVersion.ToString(), null, "unreleased", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseUpdateInfo(document.RootElement, OperatingSystem.IsMacOS());
    }

    static UpdateInfo ParseUpdateInfo(JsonElement release, bool mac)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new InvalidDataException("Invalid release version: " + tag);
        latest = Normalize(latest);
        if (latest <= CurrentVersion) return new(CurrentVersion.ToString(), latest.ToString(), "current", null);
        var name = mac ? "SekaiStorySkip-mac-arm64.dmg" : "SekaiStorySkip-win-x64.zip";
        var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
        var url = asset.ValueKind == JsonValueKind.Object ? asset.GetProperty("browser_download_url").GetString() : null;
        if (url == null) return new(CurrentVersion.ToString(), latest.ToString(), "unavailable", null);
        if (!url.StartsWith($"https://github.com/{ReleaseRepo}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected release download URL.");
        return new(CurrentVersion.ToString(), latest.ToString(), "available", url);
    }
}
