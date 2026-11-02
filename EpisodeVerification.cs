using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    record EpisodeVerification(bool HasSkip, bool HasUnread, bool BottomReached, int Pages, bool HasLocked = false);
    record EpisodePage(bool HasSkip, bool HasUnread, bool Opened, bool HasLocked, bool NeedsLowerCheck = false);
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
                var action = FindStoryRewardAction(screen);
                if (action != null && DateTime.UtcNow - lastTap > TimeSpan.FromSeconds(2) && await GuardedTap(action, FindStoryRewardAction))
                    lastTap = DateTime.UtcNow;
            }
            await Task.Delay(500, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "보상 알림이 닫힌 에피소드 목록을 확인하지 못했습니다. 완료로 처리하지 않습니다.");
    }
    static async Task ScrollEpisodesDown()
    {
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) throw new InvalidOperationException("전면 앱이 바뀌었습니다.");
        using var verify = await CaptureUncoveredEpisodeList();
        if (!IsEpisodeList(verify)) throw new InvalidOperationException("에피소드 목록이 아니므로 스크롤하지 않습니다.");
        // Original verification uses slow overlapping drags so no row is skipped between observations.
        AuthorizedInput = "input swipe 700 400 700 240 1200";
        try { await Shell(AuthorizedInput); } finally { AuthorizedInput = null; }
        Log("남은 화 확인 · 이전 화 쪽으로 목록 이동");
        await Task.Delay(650, Stop.Token);
    }
    static async Task ScrollEpisodesUp()
    {
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) throw new InvalidOperationException("전면 앱이 바뀌었습니다.");
        using var verify = await CaptureUncoveredEpisodeList();
        if (!IsEpisodeList(verify)) throw new InvalidOperationException("에피소드 목록이 아니므로 스크롤하지 않습니다.");
        AuthorizedInput = "input swipe 700 240 700 400 1200";
        try { await Shell(AuthorizedInput); } finally { AuthorizedInput = null; }
        Log("맨 아래 화 읽음 확인 · 위로 살짝 올리며 SKIP 화 탐색");
        await Task.Delay(650, Stop.Token);
    }
    static Task<EpisodeVerification> ScanEpisodePages(Func<Task<EpisodePage>> observe,
        Func<Task<bool>> scrollDownChanged, Func<Task<bool>>? scrollUpChanged = null) =>
        ScanEpisodePages(_ => observe(), scrollDownChanged, scrollUpChanged);
    static async Task<EpisodeVerification> ScanEpisodePages(Func<bool, Task<EpisodePage>> observe,
        Func<Task<bool>> scrollDownChanged, Func<Task<bool>>? scrollUpChanged = null)
    {
        var stable = 0; var hasLocked = false; var atBottom = false;
        var sawEligible = false; var searchingUp = false;
        for (var page = 0; page < 80; page++)
        {
            var current = await observe(atBottom || searchingUp);
            hasLocked |= current.HasLocked;
            if (current.HasSkip || current.HasUnread)
            {
                sawEligible = true;
                if (current.Opened) return new(current.HasSkip,current.HasUnread,false,page+1,hasLocked);
                if (!current.NeedsLowerCheck) { stable = 0; continue; }
                atBottom = !await scrollDownChanged();
                stable = 0; continue;
            }
            if (searchingUp || (atBottom && sawEligible && scrollUpChanged != null))
            {
                searchingUp = true;
                if (scrollUpChanged != null && await scrollUpChanged()) { stable = 0; continue; }
                throw new InvalidOperationException("맨 아래까지 내린 뒤 위로 스크롤하며 SKIP/미읽음 화를 찾지 못했습니다.");
            }
            var changed = await scrollDownChanged();
            atBottom = !changed;
            if (atBottom && sawEligible && scrollUpChanged != null)
            {
                searchingUp = true;
                stable = 0;
                continue;
            }
            stable = changed ? 0 : stable + 1;
            if (stable < 2) continue;
            return new(false,false,true,page+1,hasLocked);
        }
        throw new InvalidOperationException("모든 에피소드 페이지를 확인하지 못했습니다. 완료로 처리하지 않습니다.");
    }
    static async Task<EpisodeVerification> CheckEpisodesAndOpenRemaining()
    {
        var folder = Path.Combine(Artifacts,"story-check",DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var page = 0;
        var result = await ScanEpisodePages(async canOpen =>
        {
            using var screen = await CaptureUncoveredEpisodeList();
            ReadingMainStory = IsMainStoryEpisodeList(screen);
            var locked = EpisodeLockedRows(screen);
            var skips = EpisodeBadgeRows(screen,true,locked); var unread = EpisodeBadgeRows(screen,false,locked);
            CopyEvidence(ShotPath,Path.Combine(folder,$"page-{page++:D2}.png"));
            var opened = false;
            var needsLowerCheck = false;
            if (skips.Count > 0 || unread.Count > 0)
            {
                var lowest = skips.Concat(unread).Max();
                if (!canOpen && (skips.Count > 0 || lowest >= 250))
                {
                    needsLowerCheck = true;
                }
                else
                {
                    var action = FindEpisodeToRead(screen,true);
                    if (action != null && await GuardedTap(action,s => FindEpisodeToRead(s,true)))
                    {
                        opened = true;
                        Log("SKIP/새 스토리 발견 · 가장 아래 화부터 이어 읽기");
                        await Task.Delay(800,Stop.Token);
                    }
                }
            }
            return new EpisodePage(skips.Count>0,unread.Count>0,opened,locked.Count>0,needsLowerCheck);
        },async () =>
        {
            using var before = await CaptureUncoveredEpisodeList();
            using var strip = before.Clone(new Rectangle(500,85,390,415),before.PixelFormat);
            await ScrollEpisodesDown();
            using var after = await CaptureUncoveredEpisodeList();
            return DifferenceAt(after,strip,500,85,390,415) >= 1.5;
        },async () =>
        {
            using var before = await CaptureUncoveredEpisodeList();
            using var strip = before.Clone(new Rectangle(500,85,390,415),before.PixelFormat);
            await ScrollEpisodesUp();
            using var after = await CaptureUncoveredEpisodeList();
            return DifferenceAt(after,strip,500,85,390,415) >= 1.5;
        });
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
        {
            if (IsEpisodeList(screen)) ReadingMainStory = IsMainStoryEpisodeList(screen);
            else await ReadStory();
        }
        while (true)
        {
            var verification = await CheckEpisodesAndOpenRemaining();
            if (!verification.HasSkip && !verification.HasUnread)
            {
                ReadingMainStory = false;
                Log(verification.HasLocked ? "읽을 수 있는 SKIP/미읽음 없음 · 잠긴 화는 미완독으로 남김" : "아래쪽 끝까지 SKIP/새 스토리 없음 · 완독 확인");
                return verification;
            }
            await ReadStory();
        }
    }
}
