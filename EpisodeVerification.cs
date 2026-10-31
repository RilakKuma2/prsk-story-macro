using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    record EpisodeVerification(bool HasSkip, bool HasUnread, bool BottomReached, int Pages);
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
    static async Task ScrollEpisodesDown()
    {
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) throw new InvalidOperationException("전면 앱이 바뀌었습니다.");
        using var verify = await CaptureUncoveredEpisodeList();
        if (!IsEpisodeList(verify)) throw new InvalidOperationException("에피소드 목록이 아니므로 스크롤하지 않습니다.");
        // Original verification uses slow overlapping drags so no row is skipped between observations.
        AuthorizedInput = "input swipe 700 400 700 240 1200";
        try { await Shell(AuthorizedInput); } finally { AuthorizedInput = null; }
        await Task.Delay(650, Stop.Token);
    }
    static async Task<EpisodeVerification> CheckEpisodesAndOpenRemaining()
    {
        var stable = 0;
        var folder = Path.Combine(Artifacts, "story-check", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        for (var page = 0; page < 80; page++)
        {
            using var screen = await CaptureUncoveredEpisodeList();
            if (!IsEpisodeList(screen)) throw new InvalidOperationException("SKIP 검사 중 에피소드 목록을 벗어났습니다.");
            var skips = EpisodeBadgeRows(screen, true); var unreads = EpisodeBadgeRows(screen, false);
            CopyEvidence(ShotPath, Path.Combine(folder, $"page-{page:D2}.png"));
            if (skips.Count > 0 || unreads.Count > 0)
            {
                // Read where the badge is found; do not scroll past it and search upward again.
                var action = FindEpisodeToRead(screen, true);
                if (action != null && await GuardedTap(action, s => FindEpisodeToRead(s, true)))
                {
                    Log("SKIP/새 스토리 발견 · 현재 페이지에서 이어 읽기");
                    await Task.Delay(800, Stop.Token);
                    return new(skips.Count > 0, unreads.Count > 0, false, page + 1);
                }
                continue;
            }
            using var strip = screen.Clone(new Rectangle(500, 85, 390, 415), screen.PixelFormat);
            await ScrollEpisodesDown();
            using var after = await CaptureUncoveredEpisodeList();
            stable = DifferenceAt(after, strip, 500, 85, 390, 415) < 1.5 ? stable + 1 : 0;
            if (stable >= 2)
            {
                var result = new EpisodeVerification(false, false, true, page + 1);
                await SaveRecord(Path.Combine(Artifacts, $"instance-{Selected!.Index}-story-verification.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Log($"현재 위치부터 아래로 검사 완료 · SKIP/새 스토리 없음, {page + 1}페이지");
                return result;
            }
        }
        throw new InvalidOperationException("모든 에피소드 페이지를 확인하지 못했습니다. 완료로 처리하지 않습니다.");
    }
    static HomeAction? FindEpisodeToRead(Bitmap screen, bool allowUnread)
    {
        var rows = EpisodeBadgeRows(screen, true);
        if (rows.Count == 0 && allowUnread) rows = EpisodeBadgeRows(screen, false);
        return rows.Count == 0 ? null : new("episode-read", 640, Math.Min(490, rows.Max() + 32));
    }
    static async Task ReadAndVerifyCurrentStory()
    {
        using (var screen = await Capture())
            if (!IsEpisodeList(screen)) await ReadStory();
        while (true)
        {
            var verification = await CheckEpisodesAndOpenRemaining();
            if (!verification.HasSkip && !verification.HasUnread)
            {
                Log("아래쪽 끝까지 SKIP/새 스토리 없음 · 완독 확인");
                return;
            }
            await ReadStory();
        }
    }
}
