using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    record EpisodeVerification(bool HasSkip, bool HasUnread, bool BottomReached, int Pages, bool TopChecked = false, bool HasLocked = false);
    record EpisodePage(bool HasSkip, bool HasUnread, bool Opened, bool HasLocked);
    static async Task<Bitmap> CaptureUncoveredEpisodeList()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var lastTap = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            var screen = await Capture();
            if (await DismissNotice(screen)) { screen.Dispose(); continue; }
            if (IsEpisodeList(screen)) return screen;
            using (screen)
            {
                var action = FindReaderAction(screen);
                if (action is { Name: "story-rewards-banner-close" or "story-receipt-ok" or "story-reward-ok" or "story-received-ok" } &&
                    DateTime.UtcNow - lastTap > TimeSpan.FromSeconds(2) && await GuardedTap(action, FindReaderAction))
                    lastTap = DateTime.UtcNow;
            }
            await Task.Delay(500, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "보상 알림이 닫힌 에피소드 목록을 확인하지 못했습니다. 완료로 처리하지 않습니다.");
    }
    static async Task ScrollEpisodes(bool down)
    {
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) throw new InvalidOperationException("전면 앱이 바뀌었습니다.");
        using var verify = await CaptureUncoveredEpisodeList();
        if (!IsEpisodeList(verify)) throw new InvalidOperationException("에피소드 목록이 아니므로 스크롤하지 않습니다.");
        // Original verification uses slow overlapping drags so no row is skipped between observations.
        AuthorizedInput = down ? "input swipe 700 400 700 240 1200" : "input swipe 700 180 700 420 1200";
        try { await Shell(AuthorizedInput); } finally { AuthorizedInput = null; }
        await Task.Delay(650, Stop.Token);
    }
    static async Task RewindEpisodesToTop()
    {
        var stable = 0;
        for (var page = 0; page < 80; page++)
        {
            using var before = await CaptureUncoveredEpisodeList();
            using var strip = before.Clone(new Rectangle(500,85,390,415),before.PixelFormat);
            await ScrollEpisodes(false);
            using var after = await CaptureUncoveredEpisodeList();
            stable = DifferenceAt(after,strip,500,85,390,415) < 1.5 ? stable + 1 : 0;
            if (stable >= 2) { Log("완료 판정 전 목록 맨 위부터 미읽음·잠금 재확인"); return; }
        }
        throw new InvalidOperationException("에피소드 목록 맨 위를 확인하지 못했습니다. 다음 이벤트로 넘어가지 않습니다.");
    }
    static async Task<EpisodeVerification> ScanEpisodePages(Func<Task<EpisodePage>> observe,
        Func<Task<bool>> scrollDownChanged, Func<Task> rewind)
    {
        var stable = 0; var fromTop = false; var hasLocked = false;
        for (var page = 0; page < 160; page++)
        {
            var current = await observe();
            hasLocked |= current.HasLocked;
            if (current.HasSkip || current.HasUnread)
            {
                if (current.Opened) return new(current.HasSkip,current.HasUnread,false,page+1,fromTop,hasLocked);
                stable = 0; continue;
            }
            stable = await scrollDownChanged() ? 0 : stable + 1;
            if (stable < 2) continue;
            if (!fromTop)
            {
                await rewind(); fromTop = true; stable = 0; hasLocked = false;
                continue;
            }
            return new(false,false,true,page+1,true,hasLocked);
        }
        throw new InvalidOperationException("모든 에피소드 페이지를 확인하지 못했습니다. 완료로 처리하지 않습니다.");
    }
    static async Task<EpisodeVerification> CheckEpisodesAndOpenRemaining()
    {
        var folder = Path.Combine(Artifacts,"story-check",DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var page = 0;
        var result = await ScanEpisodePages(async () =>
        {
            using var screen = await CaptureUncoveredEpisodeList();
            var locked = EpisodeLockedRows(screen);
            var skips = EpisodeBadgeRows(screen,true,locked); var unread = EpisodeBadgeRows(screen,false,locked);
            CopyEvidence(ShotPath,Path.Combine(folder,$"page-{page++:D2}.png"));
            var opened = false;
            if (skips.Count > 0 || unread.Count > 0)
            {
                var action = FindEpisodeToRead(screen,true);
                if (action != null && await GuardedTap(action,s => FindEpisodeToRead(s,true)))
                {
                    opened = true;
                    Log("SKIP/새 스토리 발견 · 현재 페이지에서 이어 읽기");
                    await Task.Delay(800,Stop.Token);
                }
            }
            return new EpisodePage(skips.Count>0,unread.Count>0,opened,locked.Count>0);
        },async () =>
        {
            using var before = await CaptureUncoveredEpisodeList();
            using var strip = before.Clone(new Rectangle(500,85,390,415),before.PixelFormat);
            await ScrollEpisodes(true);
            using var after = await CaptureUncoveredEpisodeList();
            return DifferenceAt(after,strip,500,85,390,415) >= 1.5;
        },RewindEpisodesToTop);
        if (result.BottomReached)
            await SaveRecord(Path.Combine(Artifacts,$"instance-{Selected!.Index}-story-verification.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
        return result;
    }
    static HomeAction? FindEpisodeToRead(Bitmap screen, bool allowUnread)
    {
        var rows = EpisodeBadgeRows(screen, true);
        if (rows.Count == 0 && allowUnread) rows = EpisodeBadgeRows(screen, false);
        return rows.Count == 0 ? null : new("episode-read", 640, Math.Min(490, rows.Max() + 32));
    }
    static async Task<EpisodeVerification> ReadAndVerifyCurrentStory()
    {
        using (var screen = await Capture())
            if (!IsEpisodeList(screen)) await ReadStory();
        while (true)
        {
            var verification = await CheckEpisodesAndOpenRemaining();
            if (!verification.HasSkip && !verification.HasUnread)
            {
                Log(verification.HasLocked ? "읽을 수 있는 SKIP/미읽음 없음 · 잠긴 화는 미완독으로 남김" : "맨 위부터 끝까지 SKIP/새 스토리 없음 · 완독 확인");
                return verification;
            }
            await ReadStory();
        }
    }
}
