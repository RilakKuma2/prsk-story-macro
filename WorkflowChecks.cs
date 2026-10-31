using System.Text.Json;

internal static partial class Program
{
    static async Task VerifySingleEventRecovery(Action<bool, string> assert)
    {
        foreach (var cause in Enum.GetValues<RecoveryCause>())
        foreach (var autoResume in new[] { false, true })
        {
            var revoked = 0; var restarted = 0; var resumed = 0;
            try
            {
                await RunRecoveryAttempts(() => throw new RecoveryNeeded(cause, "single-event interruption"),
                    _ => { revoked++; return Task.CompletedTask; },
                    _ => { restarted++; return Task.FromResult(true); },
                    _ => { resumed++; return Task.CompletedTask; }, () => autoResume, singleEvent: true);
                assert(false, "single-event interruption must stop");
            }
            catch (OperationCanceledException ex)
            {
                assert(revoked == 1 && restarted == 0 && resumed == 0, $"single event never restarts after {cause}, auto resume={autoResume}");
                assert(ex.Message.Contains("1개 이벤스만"), "single-event stop explains why recovery is disabled");
            }
        }
        var completed = false;
        await RunRecoveryAttempts(() => { completed = true; return Task.CompletedTask; },
            _ => throw new Exception("Unexpected revoke"), _ => throw new Exception("Unexpected restart"),
            _ => throw new Exception("Unexpected resume"), () => true, singleEvent: true);
        assert(completed, "single-event completion does not trigger recovery");
    }

    static void VerifyUpdateInfo(Action<bool, string> assert)
    {
        UpdateInfo Parse(string version, string[] assets, bool mac)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                tag_name = "v" + version,
                assets = assets.Select(name => new { name, browser_download_url = $"https://github.com/{ReleaseRepo}/releases/download/v{version}/{name}" })
            }));
            return ParseUpdateInfo(document.RootElement, mac);
        }
        var current = CurrentVersion.ToString();
        var newer = new Version(CurrentVersion.Major, CurrentVersion.Minor, CurrentVersion.Build + 1).ToString();
        const string dmg = "SekaiStorySkip-mac-arm64.dmg", zip = "SekaiStorySkip-win-x64.zip";
        assert(Parse(current, [dmg, zip], true).Status == "current", "manual update check reports the current version");
        assert(Parse(newer, [dmg, zip], true) is { Status: "available", DownloadUrl: { } macUrl } && macUrl.EndsWith(dmg), "Mac update button selects the DMG");
        assert(Parse(newer, [dmg, zip], false) is { Status: "available", DownloadUrl: { } winUrl } && winUrl.EndsWith(zip), "Windows update check selects the ZIP");
        assert(Parse(newer, [zip], true).Status == "unavailable", "missing Mac asset does not offer a Windows download");
        assert(Parse(newer, [dmg], false).Status == "unavailable", "missing Windows asset does not offer a Mac download");
    }
}
