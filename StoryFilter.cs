using System.Drawing;

internal static partial class Program
{
    static readonly HashSet<int> VirtualFilteredInstances = [];
    static bool NeedsStoryFilterAtStart(Bitmap screen) => IsFilterFrame(screen) || IsMainEventList(screen) &&
        (!IsStoryFilterActive(screen) || Selected is { } vm && VirtualFilteredInstances.Contains(vm.Index));
    static bool IsFilterFrame(Bitmap screen) => screen.Width == 960 && screen.Height == 540 &&
        Matches(screen, new("filter-apply", "filter-apply-label.png", 0, 0, 552, 482, 38, 21)) &&
        Matches(screen, new("filter-cancel", "filter-cancel-label.png", 0, 0, 348, 482, 85, 21)) &&
        Matches(screen, new("filter-x", "rating-close.png", 0, 0, 745, 22, 18, 18));
    static bool IsFilterTop(Bitmap screen) => IsFilterFrame(screen) &&
        Matches(screen, new("read-status", "filter-reading-heading.png", 0, 0, 222, 129, 75, 21)) &&
        Matches(screen, new("unread", "filter-unread-label.png", 0, 0, 515, 168, 174, 20));
    static bool RadioSelected(Bitmap screen, int x, int y)
    {
        var cyan = 0;
        for (var dy = -6; dy <= 6; dy++) for (var dx = -6; dx <= 6; dx++) if (IsButtonCyan(screen.GetPixel(x + dx, y + dy))) cyan++;
        return cyan >= 90;
    }
    static bool DesiredRadios(Bitmap screen) => IsFilterTop(screen) && RadioSelected(screen, 234, 96) &&
        RadioSelected(screen, 494, 178) && !RadioSelected(screen, 234, 178) && RadioSelected(screen, 234, 256);
    static HomeAction? FindFilterRadioAction(Bitmap screen)
        => FindFilterRadioActionFor(screen, false);
    static bool DesiredVirtualRadios(Bitmap screen) => IsFilterTop(screen) && RadioSelected(screen, 234, 96) &&
        RadioSelected(screen, 234, 178) && RadioSelected(screen, 494, 256);
    static HomeAction? FindFilterRadioActionFor(Bitmap screen, bool virtualOnly)
    {
        if (!IsFilterTop(screen)) return null;
        if (!RadioSelected(screen, 234, 96)) return new("filter-type-all", 278, 96);
        if (virtualOnly)
        {
            if (!RadioSelected(screen, 234, 178)) return new("filter-reading-all", 278, 178);
            if (!RadioSelected(screen, 494, 256)) return new("filter-virtual-unwatched", 600, 256);
        }
        else
        {
            if (!RadioSelected(screen, 494, 178)) return new("filter-unread-skip", 600, 178);
            if (!RadioSelected(screen, 234, 256)) return new("filter-virtual-all", 278, 256);
        }
        return null;
    }
    static bool PinkCheck(Color p) => p.R > 225 && p.G is > 65 and < 180 && p.B > 125 && p.R - p.G > 65 && p.B - p.G > 10;
    static List<Point> CheckedFilterBoxes(Bitmap screen)
    {
        var result = new List<Point>();
        if (!IsFilterFrame(screen)) return result;
        // Fixed checkbox columns, variable row positions while the modal scrolls. Never touch empty boxes.
        foreach (var x in new[] { 234, 354, 450, 546, 642 })
        {
            for (var y = 56; y < 447; y++)
            {
                var count = 0;
                for (var dx = -11; dx <= 11; dx++) if (PinkCheck(screen.GetPixel(x + dx, y))) count++;
                if (count < 2) continue;
                var start = y; var last = y; var ink = 0;
                for (; y < 447 && y - last <= 2 && y - start <= 22; y++)
                {
                    var rowInk = 0;
                    for (var dx = -11; dx <= 11; dx++) if (PinkCheck(screen.GetPixel(x + dx, y))) rowInk++;
                    ink += rowInk; if (rowInk > 0) last = y;
                }
                var center = (start + last) / 2;
                // White checkbox margins distinguish a tick from character artwork or text.
                bool White(int px, int py) { var p = screen.GetPixel(px, py); return p.R > 240 && p.G > 240 && p.B > 240; }
                if (ink >= 35 && last - start >= 8 && last - start <= 20 && White(x - 11, center - 8) && White(x + 11, center + 8))
                    result.Add(new Point(x, center));
            }
        }
        return result.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
    }
    static HomeAction? FindCheckedFilterAction(Bitmap screen) => CheckedFilterBoxes(screen).FirstOrDefault() is { X: > 0 } point
        ? new("filter-uncheck-existing", point.X, point.Y) : null;

    static async Task ClearVisibleFilterChecks(Func<Task<Bitmap>>? capture = null)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var screen = await (capture?.Invoke() ?? Capture());
            if (!IsFilterFrame(screen)) throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터창이 사라져 체크 해제를 중지했습니다.");
            var action = FindCheckedFilterAction(screen);
            if (action == null) return;
            if (!await GuardedTap(action, FindCheckedFilterAction)) continue;
            await Task.Delay(650, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "캐릭터 체크 해제 확인에 실패했습니다. 적용하지 않습니다.");
    }
    static async Task ScrollFilter(bool down)
    {
        await Guard();
        await EnsureGameForeground();
        using var verify = await Capture();
        if (!IsFilterFrame(verify)) throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터창이 아니므로 스크롤하지 않습니다.");
        AuthorizedInput = down ? "input swipe 718 426 718 150 450" : "input swipe 718 150 718 426 450";
        try { await Shell(AuthorizedInput); } finally { AuthorizedInput = null; }
        await Task.Delay(800, Stop.Token);
    }
    static async Task VerifyAndClearAllFilterChecks()
    {
        // Clear the visible top, then visit every lower page until the content stops moving.
        for (var page = 0; page < 8; page++)
        {
            await ClearVisibleFilterChecks();
            using var before = await Capture();
            using var strip = before.Clone(new Rectangle(215, 55, 520, 395), before.PixelFormat);
            await ScrollFilter(true);
            using var after = await Capture();
            if (DifferenceAt(after, strip, 215, 55, 520, 395) < 1.5)
            {
                await ClearVisibleFilterChecks();
                return;
            }
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터 목록 끝을 확인하지 못했습니다. 적용하지 않습니다.");
    }
    static async Task ReachFilterTop()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var screen = await Capture();
            if (IsFilterTop(screen)) return;
            await ScrollFilter(false);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터 상단을 찾지 못했습니다.");
    }
    static async Task ApplyUnreadFilter(bool virtualOnly = false)
    {
        bool Desired(Bitmap s) => virtualOnly ? DesiredVirtualRadios(s) : DesiredRadios(s);
        HomeAction? FindRadio(Bitmap s) => FindFilterRadioActionFor(s, virtualOnly);
        using (var screen = await Capture())
        {
            if (IsMainEventList(screen) || virtualOnly && IsEmptyVirtualCatalogue(screen))
            {
                if (!await GuardedTap(new("filter-open", 759, 33), s => IsMainEventList(s) || virtualOnly && IsEmptyVirtualCatalogue(s) ? new("filter-open", 759, 33) : null))
                    throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터 열기 전 화면이 바뀌었습니다.");
                await Task.Delay(800, Stop.Token);
            }
            else if (!IsFilterFrame(screen)) throw new RecoveryNeeded(RecoveryCause.UnknownState, "스토리 목록이나 필터창이 아닙니다.");
        }
        await ReachFilterTop();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var screen = await Capture();
            var action = FindRadio(screen);
            if (action == null) break;
            await GuardedTap(action, FindRadio);
            await Task.Delay(700, Stop.Token);
        }
        using (var screen = await Capture())
            if (!Desired(screen)) throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터 설정을 확인하지 못했습니다.");
        await VerifyAndClearAllFilterChecks();
        await ReachFilterTop();
        using (var screen = await Capture())
            if (!Desired(screen) || CheckedFilterBoxes(screen).Count != 0) throw new RecoveryNeeded(RecoveryCause.UnknownState, "적용 직전 필터 상태가 달라졌습니다.");
        if (!await GuardedTap(new("filter-apply", 570, 491), s => Desired(s) && CheckedFilterBoxes(s).Count == 0 ? new("filter-apply", 570, 491) : null))
            throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터 적용 직전 화면이 바뀌었습니다.");
        for (var wait = 0; wait < 15; wait++)
        {
            await Task.Delay(600, Stop.Token);
            using var screen = await Capture();
            if (IsStoryFilterActive(screen))
            {
                if (virtualOnly) VirtualFilteredInstances.Add(Selected!.Index); else VirtualFilteredInstances.Remove(Selected!.Index);
                CopyEvidence(ShotPath, Path.Combine(Artifacts, $"instance-{Selected!.Index}-filter-applied.png"));
                Log(virtualOnly ? "필터 적용 완료: 버라 미시청만 · 읽음 상태 전체 · 캐릭터 체크 전부 해제" : "필터 적용 완료: 미읽음/스킵·빨리감기만 · 종류/가상 라이브 전체 · 캐릭터 체크 전부 해제");
                return;
            }
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "필터 적용 결과를 확인하지 못했습니다.");
    }
}
