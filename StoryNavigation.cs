using System.Drawing;

internal static partial class Program
{
    static bool IsMainEventList(Bitmap screen) => screen.Width == 960 && screen.Height == 540 &&
        HasCatalogueOpenButton(screen) &&
        (MatchesStoryFilterButton(screen, "story-filter.png") || MatchesStoryFilterButton(screen, "story-filter-active.png"));
    static bool IsEmptyVirtualCatalogue(Bitmap screen) => screen.Width == 960 && screen.Height == 540 &&
        !HasCatalogueOpenButton(screen) && (MatchesStoryFilterButton(screen,"story-filter.png") || MatchesStoryFilterButton(screen,"story-filter-active.png"));

    static bool HasCatalogueOpenButton(Bitmap screen)
    {
        var cyan = 0;
        for (var y = 486; y < 512; y += 2) for (var x = 782; x < 920; x += 2)
            if (IsButtonCyan(screen.GetPixel(x, y))) cyan++;
        return cyan > 400;
    }

    static bool MatchesStoryFilterButton(Bitmap screen, string image)
    {
        // As with the original song-search button, compare only the opaque circle.
        // The rectangular template's corners contain the selected story's changing artwork.
        using var expected = new Bitmap(Path.Combine(Root, "templates", image));
        long error = 0; var samples = 0;
        for (var y = 0; y < 26; y++) for (var x = 0; x < 25; x++)
        {
            if ((x - 12) * (x - 12) + (y - 13) * (y - 13) > 12 * 12) continue;
            var a = screen.GetPixel(746 + x, 20 + y); var b = expected.GetPixel(x, y);
            error += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); samples += 3;
        }
        return (double)error / samples < 12;
    }

    static bool IsStoryMenu(Bitmap screen) =>
        Matches(screen, new("main-event", "story-main-event-label.png", 0, 0, 56, 120, 263, 84)) &&
        Matches(screen, new("side", "story-side-label.png", 0, 0, 56, 367, 159, 80));

    static bool IsStoryDownload(Bitmap screen) =>
        Matches(screen, new("download-hint", "story-download-hint.png", 0, 0, 321, 274, 320, 38)) &&
        Matches(screen, new("download-button", "story-download-button.png", 0, 0, 517, 396, 108, 21));

    static bool IsStoryLoading(Bitmap screen) =>
        Matches(screen, new("tips", "story-loading-tips.png", 0, 0, 272, 201, 64, 21));

    static bool IsStoryGuide(Bitmap screen) =>
        Matches(screen, new("guide-heading", "story-guide-heading.png", 0, 0, 257, 95, 450, 43)) &&
        Matches(screen, new("guide-close", "story-guide-close.png", 0, 0, 449, 480, 64, 23));

    static HomeAction? FindStoryAction(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return null;
        // The download exception is checked before the generic gray/cyan popup closer.
        if (IsStoryDownload(screen)) return new("story-download", 571, 405);
        if (IsStoryGuide(screen)) return new("story-optional-guide-close", 480, 490);
        if (FindNoticeAction(screen) is { } notice) return notice;
        // Original home recognition requires BOTH the area globe and the cyan LIVE button.
        // STORY is translucent; its background is deliberately not part of the trigger.
        if (IsAreaMain(screen))
            return new("home-story", 594, 490);
        if (IsStoryMenu(screen)) return new("main-event-story", 340, 200);
        return null;
    }

    static async Task EnterMainEventStories()
    {
        using (var initial = await Capture())
        {
            if (!IsAreaMain(initial) && !IsStoryMenu(initial) && !IsStoryDownload(initial) && !IsStoryLoading(initial) && !IsStoryGuide(initial) && !IsMainEventList(initial))
                await ReturnHome();
        }
        var deadline = DateTime.UtcNow.AddMinutes(20);
        var lastRecognized = DateTime.UtcNow;
        var lastInput = DateTime.MinValue;
        var downloadSent = false;
        var taps = new Dictionary<string, int>();
        var loadingLogged = false;
        while (DateTime.UtcNow < deadline)
        {
            await Guard();
            await EnsureGameForeground();
            using var screen = await Capture();
            if (await DismissNotice(screen)) { lastRecognized = DateTime.UtcNow; continue; }
            if (IsStoryLoading(screen))
            {
                lastRecognized = DateTime.UtcNow;
                if (!loadingLogged) { Log("스토리 데이터 다운로드/로딩 중 · 입력 없이 대기"); loadingLogged = true; }
                await Task.Delay(2000, Stop.Token); continue;
            }
            if (IsMainEventList(screen) && !IsStoryGuide(screen) && FindColorDialogClose(screen) == null)
            {
                await Task.Delay(500, Stop.Token);
                using var verify = await Capture();
                if (IsMainEventList(verify) && !IsStoryGuide(verify) && FindColorDialogClose(verify) == null)
                {
                    CopyEvidence(ShotPath, Path.Combine(Artifacts, $"instance-{Selected!.Index}-story-list.png"));
                    Log("메인 / 이벤트 스토리 화면 도착. 다음 순서 대기 중입니다.");
                    return;
                }
            }
            var action = FindStoryAction(screen);
            if (action != null)
            {
                lastRecognized = DateTime.UtcNow;
                if (action.Name == "story-download" && downloadSent)
                {
                    if (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("다운로드 확인창이 닫히지 않아 중지했습니다. 중복 제출하지 않습니다.");
                }
                else if (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(3))
                {
                    if (taps.GetValueOrDefault(action.Name) >= 3) throw new InvalidOperationException("스토리 진입 화면이 반복되어 중지했습니다.");
                    if (await GuardedTap(action, FindStoryAction))
                    {
                        taps[action.Name] = taps.GetValueOrDefault(action.Name) + 1;
                        lastInput = DateTime.UtcNow;
                        if (action.Name == "story-download") downloadSent = true;
                    }
                }
            }
            else if (!downloadSent && DateTime.UtcNow - lastRecognized > TimeSpan.FromSeconds(45))
                throw new RecoveryNeeded(RecoveryCause.UnknownState, "알 수 없는 스토리 화면이 지속되었습니다.");
            await Task.Delay(1500, Stop.Token);
        }
        throw new InvalidOperationException("스토리 진입/다운로드 대기 제한 시간(20분)을 넘겨 중지했습니다.");
    }
}
