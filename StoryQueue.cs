using System.Drawing;

internal static partial class Program
{
    static bool IsStoryFilterActive(Bitmap screen)
    {
        if (!IsMainEventList(screen) && !IsEmptyVirtualCatalogue(screen)) return false;
        var pink = 0;
        for (var y = 20; y < 46; y++) for (var x = 746; x < 771; x++)
            if (PinkCheck(screen.GetPixel(x, y))) pink++;
        return pink >= 25;
    }
    static bool HasWhiteCardEdges(Bitmap screen, int left, int right, int top, int bottom)
    {
        static bool White(Color p) => p.R > 235 && p.G > 235 && p.B > 235;
        var count = 0;
        for (var y = top; y <= bottom; y++)
            if (White(screen.GetPixel(left, y)) && White(screen.GetPixel(right, y))) count++;
        return count >= (bottom - top + 1) * .8;
    }
    static bool IsCatalogueSelection(Bitmap screen) => IsMainEventList(screen);
    static bool HasNextCatalogueCard(Bitmap screen) => IsCatalogueSelection(screen) && HasWhiteCardEdges(screen, 98, 264, 352, 403);
    static Bitmap CatalogueCard(Bitmap screen) => screen.Clone(new Rectangle(110, 230, 220, 78), screen.PixelFormat);
    static bool SameCatalogueCard(Bitmap screen, Bitmap card) => DifferenceAt(screen, card, 110, 230, 220, 78) < 4;
    static HomeAction? FindCatalogueOpen(Bitmap screen) => IsStoryFilterActive(screen) && IsCatalogueSelection(screen)
        ? new("catalogue-open-selected", 848, 500) : null;
    static HomeAction? FindEpisodeBack(Bitmap screen) => IsEpisodeList(screen) &&
        Matches(screen, new("back", "story-back-arrow.png", 0, 0, 22, 22, 19, 20))
        ? new("story-back-to-catalogue", 32, 32) : null;

    static async Task<Bitmap> CaptureCatalogue(bool allowEmpty = false)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            await EnsureGameForeground();
            var screen = await Capture();
            if (await DismissNotice(screen)) { screen.Dispose(); continue; }
            if (IsCatalogueSelection(screen) || allowEmpty && IsEmptyVirtualCatalogue(screen)) return screen;
            screen.Dispose();
            await Task.Delay(500, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "스토리 선택 목록을 확인하지 못했습니다.");
    }
    static async Task EnsureCatalogueFilter(bool virtualOnly = false)
    {
        using var screen = await CaptureCatalogue(allowEmpty:virtualOnly);
        if (!IsStoryFilterActive(screen)) await ApplyUnreadFilter(virtualOnly);
    }
    static async Task ReturnToStoryCatalogue(bool virtualOnly = false)
    {
        var until=DateTime.UtcNow.AddSeconds(35); var lastTap=DateTime.MinValue;
        while(DateTime.UtcNow<until)
        {
            using var screen=await Capture();
            if(IsMainEventList(screen) || virtualOnly && IsEmptyVirtualCatalogue(screen))
            { Log("스토리 선택 목록 복귀"); return; }
            // A reward overlay can consume the first BACK tap; retry only while the episode list is still visible.
            if(HasRewardsObtainedText(screen)) await GuardedTap(new("reward-banner-close",480,315),s=>HasRewardsObtainedText(s)?new("reward-banner-close",480,315):null);
            else if(await DismissNotice(screen)) { }
            else if(FindReaderAction(screen) is { } reward) await GuardedTap(reward,FindReaderAction);
            else if(DateTime.UtcNow-lastTap>TimeSpan.FromSeconds(1) && FindEpisodeBack(screen) is { } back)
            { if(await GuardedTap(back,FindEpisodeBack)) lastTap=DateTime.UtcNow; }
            await Task.Delay(500,Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState,"스토리 목록으로 돌아오지 못했습니다.");
    }
    static async Task<bool> AdvanceCatalogue(bool virtualOnly = false)
    {
        await EnsureCatalogueFilter(virtualOnly);
        using var before = await CaptureCatalogue();
        using var card = CatalogueCard(before);
        if (!HasNextCatalogueCard(before))
        {
            await Task.Delay(700, Stop.Token);
            using var end = await CaptureCatalogue();
            if (SameCatalogueCard(end, card) && !HasNextCatalogueCard(end)) return false;
            return true;
        }
        HomeAction? Next(Bitmap s) => IsStoryFilterActive(s) && HasNextCatalogueCard(s) && SameCatalogueCard(s, card)
            ? new("catalogue-next-story", 180, 378) : null;
        if (!await GuardedTap(Next(before)!, Next)) throw new InvalidOperationException("다음 스토리를 선택하기 직전 화면이 바뀌었습니다.");
        for (var wait = 0; wait < 20; wait++)
        {
            await Task.Delay(500, Stop.Token);
            using var after = await CaptureCatalogue();
            if (!SameCatalogueCard(after, card)) return true;
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "다음 스토리 선택이 반영되지 않았습니다.");
    }
    static async Task OpenSelectedCatalogueStory(bool singleEvent = false, bool virtualOnly = false)
    {
        if (!singleEvent) await EnsureCatalogueFilter(virtualOnly);
        using var screen = await CaptureCatalogue();
        HomeAction? FindOpen(Bitmap s) => singleEvent && IsCatalogueSelection(s) ? new("single-event-open-selected",848,500) : FindCatalogueOpen(s);
        var open = FindOpen(screen) ?? throw new InvalidOperationException("필터 색과 스토리 결정 버튼을 확인하지 못했습니다.");
        if (!await GuardedTap(open, FindOpen)) throw new InvalidOperationException("스토리 진입 직전 화면이 바뀌었습니다.");
        // Same entry wait pattern as the invitation macro: wait for the episode list,
        // handle an explicit download if present, and never scroll the loading screen.
        var until = DateTime.UtcNow.AddMinutes(20);
        var lastKnown = DateTime.UtcNow;
        var downloaded = false;
        var listFrames = 0;
        while (DateTime.UtcNow < until)
        {
            using var current = await Capture();
            if (await DismissNotice(current)) { lastKnown = DateTime.UtcNow; continue; }
            if (IsEpisodeList(current))
            {
                if (++listFrames >= 2) return;
            }
            else
            {
                listFrames = 0;
                if (IsStoryLoading(current)) lastKnown = DateTime.UtcNow;
                else if (IsStoryDownload(current) && !downloaded)
                {
                    downloaded = await GuardedTap(new("story-download", 571, 405), FindStoryAction);
                    lastKnown = DateTime.UtcNow;
                }
                else if (IsStoryGuide(current))
                {
                    await GuardedTap(new("story-optional-guide-close", 480, 490), FindStoryAction);
                    lastKnown = DateTime.UtcNow;
                }
                else if (!downloaded && DateTime.UtcNow - lastKnown > TimeSpan.FromSeconds(30))
                    throw new RecoveryNeeded(RecoveryCause.UnknownState, "선택한 스토리의 에피소드 목록을 확인하지 못했습니다.");
            }
            await Task.Delay(600, Stop.Token);
        }
        throw new InvalidOperationException("선택한 스토리의 다운로드 대기 시간이 초과되었습니다.");
    }
    static async Task ReadStoryQueue()
    {
        using (var start = await Capture())
            if (IsMainEventList(start) || IsFilterFrame(start)) await ApplyUnreadFilter();
        var progress = LoadQueueProgress();
        using (var initial = await Capture())
        {
            if (!IsMainEventList(initial))
            {
                progress = progress with { Stage = "reading" };
                await SaveQueueProgress(progress);
                await ReadAndVerifyCurrentStory();
                progress = MarkCurrentStoryComplete(progress);
                await SaveQueueProgress(progress);
                await ReturnToStoryCatalogue();
            }
        }
        await EnsureCatalogueFilter();
        while (true)
        {
            Stop.Token.ThrowIfCancellationRequested();
            await EnsureCatalogueFilter();
            using var screen = await CaptureCatalogue();
            var card = CatalogueSignature(screen);
            // Continue from the game's current selection, including after a restart.
            // Saved records skip completed cards but never rewind the catalogue.
            if (progress.Completed.Any(done => SameCatalogueSignature(done, card)))
            {
                if (!await AdvanceCatalogue())
                {
                    await SaveQueueProgress(progress with { Current = null, Stage = "complete" });
                    Log("목록의 마지막 스토리까지 확인했습니다."); return;
                }
                continue;
            }
            progress = progress with { Current = card, Stage = "reading" };
            await SaveQueueProgress(progress);
            Log($"스토리 목록 확인 · 이전 확인 {progress.Completed.Count}개, SKIP/새 스토리 이어 읽기");
            await OpenSelectedCatalogueStory();
            await ReadAndVerifyCurrentStory();
            progress = MarkCurrentStoryComplete(progress);
            await SaveQueueProgress(progress);
            await ReturnToStoryCatalogue();
            progress = progress with { Stage = "catalogue" };
            await SaveQueueProgress(progress);
        }
    }
}
