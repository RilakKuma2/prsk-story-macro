using System.Drawing;

internal static partial class Program
{
    // Side story (サイドストーリー) automation.
    // Story menu -> character select (unit tab + nameplate check) -> per-character card list.
    // Only SKIP / unread(!) buttons are read; locked 前編 on owned cards only when the option is on.
    static volatile bool ReadingSideStory;
    sealed record SideTargetSelection(bool AllCharacters, int[] Characters);
    sealed class SideStoryOptions(bool unlockZenpen, IReadOnlyList<int>? characters = null)
    {
        public volatile bool UnlockZenpen = unlockZenpen;
        SideTargetSelection selection = MakeSideSelection(characters);
        public SideTargetSelection Selection => Volatile.Read(ref selection);
        public bool SetCharacters(IReadOnlyList<int>? characters)
        {
            var next = MakeSideSelection(characters);
            var current = Selection;
            if (current.AllCharacters == next.AllCharacters && current.Characters.SequenceEqual(next.Characters)) return false;
            Volatile.Write(ref selection, next);
            return true;
        }
    }
    static readonly HashSet<int> SideCompleted = [];
    static readonly int[] SideUnitTabY = [100, 166, 233, 300, 366, 433];
    // Reader taps land between the two card columns, so a stray tap after the story ends cannot hit a 前編/後編/unlock button.
    const int SideTapX = 614, SideTapY = 270;

    static (int Unit, int Column) SideCharacterSlot(int id) => id >= 21 ? (0, id - 21) : ((id - 1) / 4 + 1, (id - 1) % 4);
    static int SideColumnX(int unit, int column) => unit == 0 ? 200 + 136 * column : 234 + 204 * column;
    static IEnumerable<int> SideOrder(IEnumerable<int> ids) =>
        ids.Where(id => id is >= 1 and <= 26).Distinct().OrderBy(id => SideCharacterSlot(id).Unit).ThenBy(id => SideCharacterSlot(id).Column);
    static bool ValidSideCharacters(IReadOnlyList<int>? characters) =>
        characters == null || characters.Count > 0 && characters.All(id => id is >= 1 and <= 26);
    static SideTargetSelection MakeSideSelection(IReadOnlyList<int>? characters)
    {
        if (characters != null && characters.Any(id => id is < 1 or > 26))
            throw new InvalidOperationException("사이드스토리 캐릭터는 1~26번 중에서 선택하세요.");
        return new(characters == null, SideOrder(characters ?? (IEnumerable<int>)Enumerable.Range(1, 26)).ToArray());
    }

    static bool Is960(Bitmap s) => s.Width == 960 && s.Height == 540;
    // Only interior pixels of two opaque logos: no sidebar, outer outline,
    // selected-tab color, prompt, or portrait participates in screen recognition.
    static readonly Step[] SideSelectionAnchors =
    [
        new("side-unit-mmj", "side-unit-mmj-opaque.png", 0, 0, 43, 225, 20, 6),
        new("side-unit-vbs", "side-unit-vbs-opaque.png", 0, 0, 51, 301, 20, 6)
    ];
    static bool IsSideCharacterSelect(Bitmap s) => Is960(s) && SideSelectionAnchors.All(anchor => Matches(s, anchor));
    static bool SideTextGlyph(Bitmap s, string image, int left, int top)
    {
        if (!Is960(s)) return false;
        foreach (var dx in new[] { 0, -2, 2 }) foreach (var dy in new[] { 0, -2, 2 })
            if (LightGlyph(s, image, left + dx, top + dy, 230)) return true;
        return false;
    }
    static bool HasSideStoryListHeader(Bitmap s) => SideTextGlyph(s, "side-list-tab.png", 680, 68);
    static bool IsSideStoryList(Bitmap s) => HasSideStoryListHeader(s) && !IsSidePerformanceResult(s) && !IsSideRewardResult(s);
    static bool IsSideRewardResult(Bitmap s) => HasSideStoryListHeader(s) && HasRewardsObtainedText(s);
    static bool IsSidePerformanceResult(Bitmap s)
    {
        if (!HasSideStoryListHeader(s)) return false;
        var cyan = 0;
        for (var y = 311; y < 332; y++) for (var x = 550; x < 574; x++)
        { var p = s.GetPixel(x, y); if (p.G > 180 && p.B > 130 && p.R < 180 && p.G - p.R > 40) cyan++; }
        if (cyan < 30) return false;
        // Match only the small microphone's light glyph. Card art, translucent background and values can change.
        for (var x = 450; x <= 496; x++)
            if (LightGlyph(s, "side-performance-mic.png", x, 309, 220)) return true;
        return false;
    }
    static HomeAction? FindSideResultAction(Bitmap s) => IsSidePerformanceResult(s)
        ? new("side-performance-close", SideTapX, SideTapY) : IsSideRewardResult(s)
            ? new("side-reward-close", SideTapX, SideTapY) : null;
    static HomeAction? FindSideListProbeAction(Bitmap s) => HasSideStoryListHeader(s) && !IsSideVoice(s) && !IsSideUnlockConfirm(s) && FindNoticeAction(s) == null
        ? new("side-list-probe", SideTapX, SideTapY) : null;
    static bool IsSideVoice(Bitmap s) => Is960(s) && Matches(s, new("side-voice", "side-voice-no.png", 0, 0, 456, 481, 84, 20));
    static bool IsSideUnlockConfirm(Bitmap s) => Is960(s) &&
        Matches(s, new("side-unlock", "side-unlock-heading.png", 0, 0, 516, 58, 124, 18)) &&
        Matches(s, new("side-unlock-cancel", "side-unlock-cancel.png", 0, 0, 360, 481, 88, 20));
    // Enabled 解放 is bright cyan; a level/material lock turns it dark teal with a padlock.
    static bool SideUnlockEnabled(Bitmap s)
    {
        if (!IsSideUnlockConfirm(s)) return false;
        var cyan = 0;
        for (var y = 475; y < 508; y++) for (var x = 510; x < 670; x++)
        { var p = s.GetPixel(x, y); if (p.G > 210 && p.B > 190 && p.R < 120) cyan++; }
        return cyan >= 1500;
    }
    static HomeAction? FindSideUnlockAction(Bitmap s, bool unlockZenpen, string? requestedAction)
    {
        if (!IsSideUnlockConfirm(s)) return null;
        return unlockZenpen && requestedAction == "side-zenpen-unlock" && SideUnlockEnabled(s)
            ? new("side-unlock", 570, 491) : new("side-unlock-cancel", 390, 491);
    }

    // Each nameplate differs from others in the same slot; require the target to be the closest of all 26.
    static bool SideNameplateIs(Bitmap s, int id)
    {
        return IsSideCharacterSelect(s) && SideNameplateMatches(s, id);
    }
    static bool SideNameplateMatches(Bitmap s, int id)
    {
        if (!Is960(s)) return false;
        var (unit, column) = SideCharacterSlot(id);
        var left = SideColumnX(unit, column) - 38;
        double Diff(int c) { using var t = new Bitmap(Path.Combine(Root, "templates", $"side-char-{c:00}.png")); return DifferenceAt(s, t, left, 376, 76, 26); }
        var target = Diff(id);
        if (target >= 8) return false;
        for (var other = 1; other <= 26; other++) if (other != id && Diff(other) <= target) return false;
        return true;
    }

    record SideCard(int Column, int Y, bool ZenSkip, bool ZenUnread, bool KouSkip, bool KouUnread, bool ZenLocked);
    record SideScan(List<SideCard> Cards, bool HasFullLock);
    static readonly (int CardX, int Zen, int Kou, int Lock, int BigLock, int Thumb)[] SideColumns =
        [(560, 415, 523, 376, 435, 282), (892, 747, 855, 708, 767, 614)];

    static SideScan ScanSideList(Bitmap s)
    {
        var cards = new List<SideCard>(); var fullLock = false;
        if (!IsSideStoryList(s)) return new(cards, false);
        static bool White(Color p) => p.R > 238 && p.G > 238 && p.B > 238;
        double WhiteRatio(int y0, int y1, int x0, int x1)
        {
            var w = 0; var n = 0;
            for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) { n++; if (White(s.GetPixel(x, y))) w++; }
            return (double)w / n;
        }
        int Count(int y0, int y1, int x0, int x1, Func<Color, bool> f)
        {
            var c = 0;
            for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) if (f(s.GetPixel(x, y))) c++;
            return c;
        }
        for (var col = 0; col < 2; col++)
        {
            var (cardX, zen, kou, lockX, bigLock, _) = SideColumns[col];
            for (var y = 140; y < 485;)
            {
                if (WhiteRatio(y + 1, y + 4, kou - 18, kou + 8) > .8 && WhiteRatio(y + 23, y + 26, kou - 18, kou + 8) > .8 &&
                    WhiteRatio(y - 4, y - 1, kou - 18, kou + 8) < .3)
                {
                    var by = y + 13;
                    bool Skip(int bx) => Count(by - 19, by - 2, bx + 14, bx + 56, p => p.G > 200 && p.B > 170 && p.R < 140) >= 80;
                    bool Unread(int bx) => Count(by - 18, by, bx + 26, bx + 46, p => p.R > 225 && p.G < 110 && p.B > 120) >= 35;
                    var locked = Count(by - 11, by + 11, lockX, lockX + 22, p => p.R < 95 && p.G < 95 && p.B < 120) >= 80;
                    cards.Add(new(col, by, Skip(zen), Unread(zen), Skip(kou), Unread(kou), locked));
                    y += 50; continue;
                }
                y++;
            }
            for (var y = 140; y < 495;)
            {
                var pad = Count(y, y + 30, bigLock - 14, bigLock + 14, White);
                long bg = 0;
                for (var yy = y; yy < y + 20; yy++) for (var x = cardX; x < cardX + 20; x++) { var p = s.GetPixel(x, yy); bg += p.R + p.G + p.B; }
                if (pad >= 450 && bg / (20.0 * 20 * 3) < 155) { fullLock = true; break; }
                y += 2;
            }
        }
        return new(cards.OrderBy(c => c.Y).ThenBy(c => c.Column).ToList(), fullLock);
    }

    static Bitmap SideCardFingerprint(Bitmap s, SideCard card) =>
        s.Clone(new Rectangle(SideColumns[card.Column].Thumb, card.Y - 42, 60, 56), s.PixelFormat);
    static bool SideCardFailed(Bitmap s, SideCard card, IEnumerable<Bitmap> failed) =>
        failed.Any(f => DifferenceAt(s, f, SideColumns[card.Column].Thumb, card.Y - 42, 60, 56) < 6);

    // The next button to press on the visible list, topmost first. Partially visible bottom rows are left for the next scroll.
    static (HomeAction Action, SideCard Card, bool UnlockZenpen)? FindSideCandidate(Bitmap s, bool unlockZenpen, IReadOnlyCollection<Bitmap> failed)
    {
        foreach (var card in ScanSideList(s).Cards)
        {
            if (card.Y < 150 || card.Y > 470 || SideCardFailed(s, card, failed)) continue;
            var c = SideColumns[card.Column];
            if (card.ZenSkip || card.ZenUnread) return (new("side-zenpen", c.Zen, card.Y), card, unlockZenpen);
            if (card.KouSkip || card.KouUnread) return (new("side-kouhen", c.Kou, card.Y), card, unlockZenpen);
            if (unlockZenpen && card.ZenLocked) return (new("side-zenpen-unlock", c.Zen, card.Y), card, unlockZenpen);
        }
        return null;
    }

    static HomeAction? FindSideNavAction(Bitmap s)
    {
        if (!Is960(s)) return null;
        if (IsSideCharacterSelect(s)) return null;
        if (FindSideResultAction(s) is { } result) return result;
        if (IsStoryDownload(s)) return new("story-download", 571, 405);
        if (IsSideStoryList(s)) return new("side-list-back", 32, 32);
        if (IsStoryMenu(s)) return new("story-side", 200, 410);
        if (IsAreaMain(s)) return new("home-story", 594, 490);
        return null;
    }

    static async Task EnterSideCharacterSelect()
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var lastKnown = DateTime.UtcNow; var lastInput = DateTime.MinValue; var homeTried = false;
        while (DateTime.UtcNow < deadline)
        {
            await Guard();
            await EnsureGameForeground();
            using var screen = await Capture();
            if (IsSideCharacterSelect(screen) && FindColorDialogClose(screen) == null) { Log("사이드스토리 캐릭터 선택 화면 도착"); return; }
            if (await DismissNotice(screen)) { lastKnown = DateTime.UtcNow; continue; }
            if (IsStoryLoading(screen)) { lastKnown = DateTime.UtcNow; await Task.Delay(2000, Stop.Token); continue; }
            if (FindSideNavAction(screen) is { } action)
            {
                lastKnown = DateTime.UtcNow;
                if (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(3) && await GuardedTap(action, FindSideNavAction)) lastInput = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastKnown > TimeSpan.FromSeconds(homeTried ? 45 : 6))
            {
                if (homeTried) throw new RecoveryNeeded(RecoveryCause.UnknownState, "사이드스토리 화면으로 가는 중 알 수 없는 화면이 지속되었습니다.");
                homeTried = true; Log("사이드스토리 시작 화면이 아님 · 메인화면부터 진입");
                await ReturnHome(); lastKnown = DateTime.UtcNow;
            }
            await Task.Delay(1000, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "사이드스토리 캐릭터 선택 화면을 확인하지 못했습니다.");
    }

    static async Task OpenSideCharacter(int id)
    {
        var (unit, column) = SideCharacterSlot(id);
        var x = SideColumnX(unit, column);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Guard();
            using var screen = await Capture();
            if (IsSideStoryList(screen)) return;
            if (await DismissNotice(screen)) continue;
            if (IsSideCharacterSelect(screen))
            {
                if (SideNameplateIs(screen, id))
                {
                    var open = new HomeAction($"side-character-{id:00}", x, 300);
                    await GuardedTap(open, s => SideNameplateIs(s, id) ? open : null);
                }
                else
                {
                    var tab = new HomeAction($"side-unit-{unit}", 62, SideUnitTabY[unit]);
                    await GuardedTap(tab, s => IsSideCharacterSelect(s) ? tab : null);
                }
            }
            await Task.Delay(1200, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, $"사이드스토리 캐릭터 {id:00}의 이름칸을 확인하지 못했습니다.");
    }

    static bool SideListUnchanged(Bitmap before, Bitmap after)
    {
        if (!IsSideStoryList(before) || !IsSideStoryList(after)) return false;
        using var previous = before.Clone(new Rectangle(278, 120, 650, 380), before.PixelFormat);
        return DifferenceAt(after, previous, 278, 120, 650, 380) < 3;
    }

    static async Task<Bitmap?> ProbeSideList()
    {
        var action = new HomeAction("side-list-probe", SideTapX, SideTapY);
        if (!await GuardedTap(action, FindSideListProbeAction)) return null;
        Log("사이드스토리 목록 변화 없음 · 읽기 위치 한 번 터치 후 재확인");
        await Task.Delay(700, Stop.Token);
        return await Capture();
    }

    static bool SideListProbeDue(DateTime now, ref DateTime? stalledSince)
    {
        stalledSince ??= now;
        return now - stalledSince.Value >= TimeSpan.FromSeconds(5);
    }

    static async Task<bool> ScrollSideList()
    {
        await Guard();
        using var verify = await Capture();
        if (!IsSideStoryList(verify)) return false; // A result overlay can arrive between the two captures.
        AuthorizedInput = $"input swipe {SideTapX} 430 {SideTapX} 220 1200";
        try { await Shell(AuthorizedInput); } finally { AuthorizedInput = null; }
        Log("사이드스토리 목록 · 다음 카드 확인");
        await Task.Delay(800, Stop.Token);
        return true;
    }

    static async Task ClearSideCharacter(int id, SideStoryOptions options)
    {
        var failed = new List<Bitmap>();
        Bitmap? lastCard = null; string? lastKey = null; var repeats = 0;
        string? requestedAction = null;
        var storyUnlockZenpen = false;
        DateTime? stalledSince = null;
        var lastKnown = DateTime.UtcNow;
        try
        {
            for (var step = 0; step < 600; step++)
            {
                await Guard();
                await EnsureGameForeground();
                using var screen = await Capture();
                if (FindSideUnlockAction(screen, storyUnlockZenpen, requestedAction) is { } unlockAction)
                {
                    lastKnown = DateTime.UtcNow;
                    if (await GuardedTap(unlockAction, s => FindSideUnlockAction(s, storyUnlockZenpen, requestedAction)))
                    {
                        requestedAction = null;
                        if (unlockAction.Name == "side-unlock") Log("미해금 전편 해금 · 이어서 읽기");
                        else
                        {
                            Log("해금 불가 또는 요청하지 않은 해금창 · 취소하고 이 카드는 건너뜀");
                            if (lastCard != null) { failed.Add(lastCard); lastCard = null; }
                        }
                    }
                    await Task.Delay(900, Stop.Token); continue;
                }
                if (IsSideVoice(screen) || CanTapStory(screen))
                {
                    await ReadStory(); lastKnown = DateTime.UtcNow; lastKey = null; repeats = 0; requestedAction = null; stalledSince = null;
                    lastCard?.Dispose(); lastCard = null;
                    continue;
                }
                if (await DismissNotice(screen)) { lastKnown = DateTime.UtcNow; stalledSince = null; continue; }
                if (IsSideStoryList(screen))
                {
                    lastKnown = DateTime.UtcNow;
                    if (FindReaderAction(screen) is { } reward) { await GuardedTap(reward, FindReaderAction); continue; }
                    if (FindSideCandidate(screen, options.UnlockZenpen, failed) is { } next)
                    {
                        var key = $"{next.Card.Column}:{next.Card.Y / 8}:{next.Action.Name}";
                        if (key != lastKey) stalledSince = null;
                        else stalledSince ??= DateTime.UtcNow;
                        if (key == lastKey && repeats >= 3)
                        {
                            if (!SideListProbeDue(DateTime.UtcNow, ref stalledSince)) { await Task.Delay(350, Stop.Token); continue; }
                            using var afterProbe = await ProbeSideList();
                            stalledSince = null;
                            if (afterProbe == null || !SideListUnchanged(screen, afterProbe))
                            {
                                lastKey = null; repeats = 0; requestedAction = null;
                                lastCard?.Dispose(); lastCard = null;
                                continue;
                            }
                            Log("같은 버튼이 반응하지 않음 · 이 카드는 건너뜀");
                            failed.Add(SideCardFingerprint(screen, next.Card)); lastKey = null; repeats = 0; requestedAction = null;
                            lastCard?.Dispose(); lastCard = null;
                            continue;
                        }
                        var action = next.Action;
                        if (await GuardedTap(action, s => FindSideCandidate(s, options.UnlockZenpen, failed)?.Action))
                        {
                            // Keep this story's choice through its unlock/voice dialogs. Live changes affect the next selection.
                            storyUnlockZenpen = next.UnlockZenpen;
                            repeats = key == lastKey ? repeats + 1 : 1;
                            lastKey = key; requestedAction = action.Name;
                            lastCard?.Dispose(); lastCard = SideCardFingerprint(screen, next.Card);
                            Log(action.Name == "side-zenpen-unlock" ? "잠긴 전편 해금 시도" : action.Name == "side-zenpen" ? "전편 읽기" : "후편 읽기");
                        }
                        await Task.Delay(1200, Stop.Token); continue;
                    }
                    if (ScanSideList(screen).HasFullLock) { Log($"캐릭터 {id:00} · 미보유(전체 잠김) 카드 도달 · 다음 캐릭터"); return; }
                    using var before = new Bitmap(screen);
                    if (!await ScrollSideList()) continue;
                    using var after = await Capture();
                    if (SideListUnchanged(before, after))
                    {
                        if (!SideListProbeDue(DateTime.UtcNow, ref stalledSince)) continue;
                        using var afterProbe = await ProbeSideList();
                        stalledSince = null;
                        if (afterProbe != null && SideListUnchanged(after, afterProbe))
                        { Log($"캐릭터 {id:00} · 목록 끝 · 다음 캐릭터"); return; }
                    }
                    else stalledSince = null;
                    continue;
                }
                if (FindReaderAction(screen) is { } readerAction) { lastKnown = DateTime.UtcNow; stalledSince = null; await GuardedTap(readerAction, FindReaderAction); }
                else if (IsStoryLoading(screen)) lastKnown = DateTime.UtcNow;
                else if (DateTime.UtcNow - lastKnown > TimeSpan.FromSeconds(30))
                    throw new RecoveryNeeded(RecoveryCause.UnknownState, "사이드스토리 목록/대화창이 30초 동안 확인되지 않았습니다.");
                await Task.Delay(700, Stop.Token);
            }
            throw new RecoveryNeeded(RecoveryCause.UnknownState, $"캐릭터 {id:00} 사이드스토리 처리 횟수 제한을 넘었습니다.");
        }
        finally
        {
            lastCard?.Dispose();
            foreach (var card in failed) card.Dispose();
        }
    }

    static async Task BackToSideCharacterSelect()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Guard();
            using var screen = await Capture();
            if (IsSideCharacterSelect(screen)) return;
            if (await DismissNotice(screen)) continue;
            if (FindSideNavAction(screen) is { Name: "side-performance-close" or "side-reward-close" or "side-list-back" } action)
            {
                await GuardedTap(action, FindSideNavAction);
            }
            await Task.Delay(1200, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "사이드스토리 캐릭터 선택 화면으로 돌아가지 못했습니다.");
    }

    static async Task RunSideTargets(SideStoryOptions options, Action? entered = null, Func<Task>? enterCharacterSelect = null)
    {
        await (enterCharacterSelect?.Invoke() ?? EnterSideCharacterSelect());
        entered?.Invoke(); // Failed entry must retain the recovery failure count.
        var count = await RunSideCharacterQueue(options, SideCompleted, async id =>
        {
            Log($"사이드스토리 캐릭터 {id:00} 시작");
            await OpenSideCharacter(id);
            await ClearSideCharacter(id, options);
        }, BackToSideCharacterSelect);
        Log($"사이드스토리 완료 · 캐릭터 {count}명 처리");
    }

    static async Task<int> RunSideCharacterQueue(SideStoryOptions options, ISet<int> completed, Func<int, Task> readCharacter,
        Func<Task> backToCharacterSelect, Func<Task>? waitForSelection = null)
    {
        var waiting = false;
        while (true)
        {
            Stop.Token.ThrowIfCancellationRequested();
            // Re-read after every return to character selection, before choosing another character or completing.
            var targets = options.Selection;
            if (targets.Characters.Length == 0)
            {
                if (!waiting) { waiting = true; Log("사이드스토리 대상 없음 · 캐릭터 선택 대기"); StatusSink?.Invoke("캐릭터 선택 대기"); }
                await (waitForSelection?.Invoke() ?? Task.Delay(350, Stop.Token));
                continue;
            }
            if (waiting) { waiting = false; StatusSink?.Invoke("실행 중"); }
            var id = targets.Characters.FirstOrDefault(id => !completed.Contains(id));
            if (id == 0)
            {
                await Task.Yield();
                if (!ReferenceEquals(targets, options.Selection)) continue;
                return targets.Characters.Length;
            }
            await readCharacter(id);
            completed.Add(id);
            await backToCharacterSelect();
        }
    }

    static async Task ReadSideStories(SideStoryOptions options)
    {
        SideCompleted.Clear();
        Log($"사이드스토리 시작 · 캐릭터 {options.Selection.Characters.Length}명 · 미해금 전편 밀기 {(options.UnlockZenpen ? "켜짐" : "꺼짐")}");
        ReadingMainStory = false; ReadingSideStory = true;
        try
        {
            await RunWithNewDataRecovery(() => RunSideTargets(options),
                enter: reset => RunSideTargets(options, reset));
        }
        finally { ReadingSideStory = false; SideCompleted.Clear(); }
    }
}
