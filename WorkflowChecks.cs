using System.Text.Json;

internal static partial class Program
{
    static async Task VerifyFilterRecovery(Action<bool, string> assert)
    {
        Task LoseFilter() => ClearVisibleFilterChecks(() =>
            Task.FromResult(new Bitmap(Path.Combine(Root,"tests","fixtures","catalogue-after-completion.png"))));
        foreach (var (autoResume, singleEvent) in new[] { (true,false), (false,false), (true,true) })
        {
            var revoked=0;var restarted=0;var resumed=0;var stopped=false;
            try
            {
                await RunRecoveryAttempts(LoseFilter,
                    _ => { revoked++;return Task.CompletedTask; },
                    interruption =>
                    {
                        assert(interruption.Cause==RecoveryCause.UnknownState,"a disappearing filter reaches state recovery instead of the terminal error handler");
                        restarted++;return Task.FromResult(true);
                    },
                    _ => { resumed++;return Task.CompletedTask; },() => autoResume,singleEvent);
            }
            catch (OperationCanceledException) { stopped=true; }
            var recover=autoResume && !singleEvent;
            assert(revoked==1 && restarted==(recover?1:0) && resumed==(recover?1:0) && stopped!=recover,
                $"filter loss obeys auto resume={autoResume}, single event={singleEvent}");
        }
        var restarts=0;var failed=false;
        try
        {
            await RunRecoveryAttempts(LoseFilter,_ => Task.CompletedTask,
                _ => { restarts++;return Task.FromResult(true); },_ => LoseFilter(),() => true);
        }
        catch (InvalidOperationException) { failed=true; }
        assert(failed && restarts==1,"repeated filter restoration failure stops after two unknown states without endless restarts");
    }

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
        var revision = new Version(CurrentVersion.Major, CurrentVersion.Minor, CurrentVersion.Build, Math.Max(CurrentVersion.Revision, 0) + 1).ToString();
        const string dmg = "SekaiStorySkip-mac-arm64.dmg", zip = "SekaiStorySkip-win-x64.zip";
        assert(Normalize(new Version(0,0,2,1)) > Normalize(new Version(0,0,2)), "four-part versions retain their revision for update comparisons");
        assert(Normalize(new Version(0,0,2,0)) == Normalize(new Version(0,0,2)), "zero revision remains equivalent to a three-part version");
        assert(Parse(current, [dmg, zip], true).Status == "current", "manual update check reports the current version");
        assert(Parse(revision, [dmg, zip], true) is {Status:"available",Latest:var latest} && latest == revision, "update checks detect a newer fourth version component");
        assert(Parse(newer, [dmg, zip], true) is { Status: "available", DownloadUrl: { } macUrl } && macUrl.EndsWith(dmg), "Mac update button selects the DMG");
        assert(Parse(newer, [dmg, zip], false) is { Status: "available", DownloadUrl: { } winUrl } && winUrl.EndsWith(zip), "Windows update check selects the ZIP");
        assert(Parse(newer, [zip], true).Status == "unavailable", "missing Mac asset does not offer a Windows download");
        assert(Parse(newer, [dmg], false).Status == "unavailable", "missing Windows asset does not offer a Mac download");
    }
}
