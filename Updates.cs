using System.Text.Json;

internal static partial class Program
{
    const string ReleaseRepo = "RilakKuma2/prsk-story-macro";
    static Version CurrentVersion => Normalize(typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0));
    static Version Normalize(Version version) => version.Revision > 0
        ? new(version.Major, version.Minor, Math.Max(version.Build, 0), version.Revision)
        : new(version.Major, version.Minor, Math.Max(version.Build, 0));
    sealed record UpdateInfo(string Current, string? Latest, string Status, string? DownloadUrl, string Notes = "", bool CanSelfUpdate = false);

    static string? MacAppBundlePath()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var bundle = Path.GetFullPath(Path.Combine(Root, "..", "..", ".."));
        return bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(bundle, "Contents", "MacOS", "SekaiStorySkip")) ? bundle : null;
    }

    static async Task<UpdateInfo> ReadUpdateInfo()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiStorySkip/" + CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(CurrentVersion.ToString(), null, "unreleased", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var info=ParseUpdateInfo(document.RootElement, OperatingSystem.IsMacOS()) with { CanSelfUpdate = MacAppBundlePath() != null };
        return info.Status=="available" ? info with {Notes=await ReadPendingReleaseNotes(http,document.RootElement)} : info;
    }

    static async Task<string> ReadPendingReleaseNotes(HttpClient http,JsonElement latestRelease)
    {
        var latest=Normalize(Version.Parse(latestRelease.GetProperty("tag_name").GetString()!.TrimStart('v','V')));
        var releases=new List<JsonElement>{latestRelease.Clone()};
        try
        {
            for(var page=1;;page++)
            {
                using var response=await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases?per_page=100&page={page}");
                response.EnsureSuccessStatusCode();
                using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                releases.AddRange(document.RootElement.EnumerateArray().Select(r=>r.Clone()));
                if (!response.Headers.TryGetValues("Link",out var links) || !links.Any(l=>l.Contains("rel=\"next\"",StringComparison.Ordinal))) break;
            }
            return PendingReleaseNotes(releases,CurrentVersion,latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return T("이전 버전의 변경 내역을 불러오지 못했습니다.","以前のバージョンの変更履歴を取得できませんでした。","Earlier release notes could not be loaded.")+"\n\n"+
                PendingReleaseNotes([latestRelease],CurrentVersion,latest);
        }
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
        var notes=ChangesFromReleaseNotes(release.TryGetProperty("body",out var body)?body.GetString():null);
        return new(CurrentVersion.ToString(), latest.ToString(), "available", url, notes);
    }
}
