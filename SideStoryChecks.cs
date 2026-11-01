using System.Drawing;

internal static partial class Program
{
    static void VerifySideStory(Action<bool, string> assert)
    {
        Bitmap F(string name) => new(Path.Combine(Root, "tests", "fixtures", name));
        using var vs = F("side-select-vs.png"); using var vbs = F("side-select-vbs.png");
        using var badges = F("side-list-badges.png"); using var locked = F("side-list-locked.png"); using var read = F("side-list-read.png");
        using var voice = F("side-voice.png"); using var voiceUnlocked = F("side-voice-unlocked.png");
        using var unlockOn = F("side-unlock-enabled.png"); using var unlockOff = F("side-unlock-disabled.png");
        using var reader = F("side-reader.png"); using var menu = F("story-menu.png");
        using var performance = F("side-performance-result.png");
        using var sideReward = F("side-reward-result.png");

        assert(IsSideCharacterSelect(vs) && IsSideCharacterSelect(vbs) && !IsSideCharacterSelect(badges), "side character select prompt");
        assert(Enumerable.Range(21, 6).All(id => SideNameplateIs(vs, id)) && Enumerable.Range(9, 4).All(id => SideNameplateIs(vbs, id)), "nameplate identifies every visible character");
        assert(!SideNameplateIs(vs, 1) && !SideNameplateIs(vbs, 1) && !SideNameplateIs(vbs, 5), "another unit's character in the same slot is rejected");
        assert(SideOrder([20, 21, 1, 9, 1, 0, 27]).SequenceEqual([21, 1, 9, 20]), "targets follow the unit tab order without duplicates");
        assert(ValidSideCharacters(null) && ValidSideCharacters([1, 26, 1]), "omitted characters means all; explicit valid selections allow duplicates");
        assert(!ValidSideCharacters([]) && !ValidSideCharacters([0]) && !ValidSideCharacters([9, 27]), "empty or invalid explicit selections never expand to all characters");

        foreach (var list in new[] { badges, locked, read }) assert(IsSideStoryList(list), "side list tab recognized");
        foreach (var other in new[] { vs, voice, unlockOn, reader, menu }) assert(!IsSideStoryList(other), "non-list screens are not the side list");

        var none = Array.Empty<Bitmap>();
        assert(FindSideCandidate(badges, false, none) is { Action: { Name: "side-zenpen", X: 415, Y: >= 168 and <= 176 } }, "SKIP 前編 at the top-left is read first");
        var scan = ScanSideList(badges);
        assert(scan.Cards.Count(c => c.ZenSkip) == 1 && scan.Cards.Count(c => c.ZenUnread) == 1 && !scan.HasFullLock, "SKIP and unlocked-but-unread (!) badges both detected");
        using (var fp = SideCardFingerprint(badges, scan.Cards.First(c => c.ZenSkip)))
            assert(FindSideCandidate(badges, false, [fp]) is { Action: { Name: "side-zenpen", X: 747, Y: >= 252 and <= 260 } }, "unread (!) 前編 is read after the first card");
        assert(FindSideCandidate(read, false, none) == null, "fully read owned cards give no input");
        assert(FindSideCandidate(read, true, none) is { Action.Name: "side-zenpen-unlock" }, "locked 前編 is tried only with the unlock option");
        var lockedScan = ScanSideList(locked);
        assert(lockedScan.HasFullLock && lockedScan.Cards.All(c => c.Y < 200), "gray full-card locks end the character and are never tapped");
        assert(FindSideCandidate(locked, false, none) == null, "locked cards without the option send no input");
        assert(IsSidePerformanceResult(performance) && HasSideStoryListHeader(performance) && !IsSideStoryList(performance), "microphone result overlay retains the side header but is not a ready card list");
        assert(FindSideCandidate(performance, false, none) == null && FindSideCandidate(performance, true, none) == null, "performance results never select or unlock cards behind the overlay");
        assert(IsSideRewardResult(sideReward) && !IsSideStoryList(sideReward) && FindSideCandidate(sideReward, true, none) == null, "the crystal reward following performance is also a result overlay");
        foreach (var normal in new[] { badges, locked, read, voice, unlockOn, reader, menu })
            assert(!IsSidePerformanceResult(normal), "normal screens are not microphone result overlays");
        using (var changed = new Bitmap(performance))
        using (var microphone = new Bitmap(Path.Combine(Root, "templates", "side-performance-mic.png")))
        using (var g = Graphics.FromImage(changed))
        using (var background = new SolidBrush(Color.FromArgb(105, 115, 145)))
        using (var ink = new SolidBrush(Color.FromArgb(240, 245, 250)))
        {
            g.FillRectangle(background, 336, 311, 118, 20); // Remove the performance text completely.
            g.FillRectangle(background, 450, 307, 65, 30);
            for (var y = 0; y < microphone.Height; y++) for (var x = 0; x < microphone.Width; x++)
            {
                var p = microphone.GetPixel(x, y);
                if ((p.R + p.G + p.B) / 3 > 220) g.FillRectangle(ink, 464 + x, 309 + y, 1, 1);
            }
            assert(IsSidePerformanceResult(changed), "microphone detection ignores text, translucent background RGB, values and horizontal position");
        }
        using (var missedMicrophone = new Bitmap(performance))
        {
            using (var g = Graphics.FromImage(missedMicrophone))
            using (var brush = new SolidBrush(Color.FromArgb(90, 90, 120))) g.FillRectangle(brush, 450, 307, 65, 30);
            assert(!IsSidePerformanceResult(missedMicrophone) && IsSideStoryList(missedMicrophone), "a missed microphone still leaves the stable side-story header available");
            assert(SideListUnchanged(missedMicrophone, missedMicrophone) && FindSideListProbeAction(missedMicrophone) is { X: SideTapX, Y: SideTapY }, "stalled unrecognized overlays qualify for one safe probe through the header");
            assert(!SideListUnchanged(missedMicrophone, sideReward), "a fallback revealing the next reward overlay continues handling it instead of ending the character");
        }
        assert(SideListUnchanged(badges, badges) && !SideListUnchanged(badges, locked), "scroll comparison checks the same card rectangle in both frames");
        assert(!SideListUnchanged(performance, performance) && !SideListUnchanged(badges, menu), "recognized overlays and other pages cannot be treated as a list end");
        using (var changedPortrait = new Bitmap(badges))
        {
            using (var g = Graphics.FromImage(changedPortrait)) g.FillRectangle(Brushes.Black, 0, 120, 250, 380);
            assert(SideListUnchanged(badges, changedPortrait), "portrait changes outside the card list do not prevent detecting its end");
        }
        DateTime? stalledSince = null;
        var now = DateTime.UtcNow;
        assert(!SideListProbeDue(now, ref stalledSince) && !SideListProbeDue(now.AddSeconds(4.9), ref stalledSince), "fallback never taps before five seconds without progress");
        assert(SideListProbeDue(now.AddSeconds(5), ref stalledSince), "five seconds without progress permits the safe one-shot fallback");
        stalledSince = null;
        assert(!SideListProbeDue(now.AddMinutes(1), ref stalledSince), "new progress starts a fresh five-second fallback interval");
        assert(FindSideListProbeAction(voice) == null && FindSideListProbeAction(unlockOn) == null && FindSideListProbeAction(menu) == null, "fallback cannot tap voice/unlock dialogs or other pages");

        assert(IsSideVoice(voice) && IsSideVoice(voiceUnlocked) && !IsSideUnlockConfirm(voice), "side voice dialog (normal and just unlocked)");
        assert(IsSideUnlockConfirm(unlockOn) && SideUnlockEnabled(unlockOn) && !IsSideVoice(unlockOn), "enabled 解放 dialog");
        assert(IsSideUnlockConfirm(unlockOff) && !SideUnlockEnabled(unlockOff), "locked 解放 (後編 / materials) is cancelled");
        assert(FindSideUnlockAction(unlockOn, true, "side-zenpen-unlock") is { Name: "side-unlock", X: 570, Y: 491 }, "enabled unlock requires the requested locked part 1");
        foreach (var requested in new string?[] { null, "side-zenpen", "side-kouhen" })
            assert(FindSideUnlockAction(unlockOn, true, requested)?.Name == "side-unlock-cancel", "unsolicited or already consumed unlock dialog is cancelled");
        assert(FindSideUnlockAction(unlockOn, false, "side-zenpen-unlock")?.Name == "side-unlock-cancel" &&
            FindSideUnlockAction(unlockOff, true, "side-zenpen-unlock")?.Name == "side-unlock-cancel", "disabled option or insufficient materials never unlocks");
        using (var small = new Bitmap(480, 270))
            assert(!SideUnlockEnabled(small) && FindSideUnlockAction(small, true, "side-zenpen-unlock") == null, "wrong resolution has no unlock action");
        assert(!SideUnlockEnabled(badges) && FindSideUnlockAction(voice, true, "side-zenpen-unlock") == null, "unlock buttons are recognized only inside their own confirmation");
        var options = new SideStoryOptions(false);
        assert(FindSideCandidate(read, options.UnlockZenpen, none) == null, "live unlock option starts disabled");
        options.UnlockZenpen = true;
        var selected = FindSideCandidate(read, options.UnlockZenpen, none)!.Value;
        assert(selected.Action.Name == "side-zenpen-unlock", "enabling unlock during a run makes the next locked part 1 selectable");
        options.UnlockZenpen = false;
        assert(FindSideUnlockAction(unlockOn, selected.UnlockZenpen, selected.Action.Name)?.Name == "side-unlock", "disabling the option keeps the already selected story's unlock choice");
        assert(FindSideCandidate(read, options.UnlockZenpen, none) == null && FindSideCandidate(badges, options.UnlockZenpen, none)?.Action.Name == "side-zenpen", "the next story stops unlocking but continues reading SKIP and unread stories");
        options.UnlockZenpen = true;
        assert(FindSideCandidate(read, options.UnlockZenpen, none)?.Action.Name == "side-zenpen-unlock", "unlock can be enabled again later in the same run");
        var saved = ReadingSideStory;
        try
        {
            ReadingSideStory = true;
            assert(FindNoticeAction(voice) == null && FindNoticeAction(unlockOn) == null, "side dialogs are not closed by the generic X");
            assert(FindReaderAction(voice) is { Name: "side-no-voice", X: 494, Y: 491 }, "side voice dialog chooses no voice");
            assert(!CanTapStory(badges) && !CanTapStory(voice) && CanTapStory(reader), "dialogue taps only on the reader, never on the side list");
            assert(FindReaderAction(performance) is { Name: "side-performance-close", X: SideTapX, Y: SideTapY } && !CanTapStory(performance), "microphone results close with one safe reader-position tap and never continuous taps");
            assert(FindNoticeAction(performance) == null, "performance results use their own safe tap instead of a generic X");
            assert(FindReaderAction(sideReward) is { Name: "side-reward-close", X: SideTapX, Y: SideTapY } && !CanTapStory(sideReward), "the follow-up crystal reward closes at the same safe position");
        }
        finally { ReadingSideStory = saved; }
        assert(FindSideNavAction(menu)?.Name == "story-side" && FindSideNavAction(badges)?.Name == "side-list-back", "side story entry and back navigation");

        var air = new StoryTouch("/dev/input/event2", 32767, 32767, false, true);
        static bool Has(byte[] packet, short code, int value)
        {
            using var r = new BinaryReader(new MemoryStream(packet));
            while (r.BaseStream.Position < packet.Length) { r.ReadBytes(16); var t = r.ReadInt16(); var c = r.ReadInt16(); var v = r.ReadInt32(); if (t == 3 && c == code && v == value) return true; }
            return false;
        }
        var side = StoryTouchPackets(air, side: true).Down;
        assert(Has(side, 53, 32768 * SideTapX / 960) && Has(side, 54, 32768 * SideTapY / 540) && Has(StoryTouchPackets(air).Down, 53, 16384), "side reader taps the gap between card columns; main stories keep the center");
        VerifySideEntryRecovery(assert).GetAwaiter().GetResult();
        VerifySideTargetChanges(assert).GetAwaiter().GetResult();
    }

    static async Task VerifySideEntryRecovery(Action<bool, string> assert)
    {
        var restarts = 0; var resets = 0; var failed = false;
        Task EntryFailure() => Task.FromException(new RecoveryNeeded(RecoveryCause.UnknownState, "side entry unavailable"));
        try
        {
            await RunRecoveryAttempts(EntryFailure, _ => Task.CompletedTask,
                _ => { restarts++; return Task.FromResult(true); },
                reset => RunSideTargets(new(false), () => { resets++; reset(); }, EntryFailure), () => true);
        }
        catch (InvalidOperationException) { failed = true; }
        assert(failed && restarts == 1 && resets == 0, "failed side-story re-entry stops after two unknown states without resetting the counter");

        var entered = false;
        try
        {
            await RunSideTargets(new(false), () =>
            {
                assert(entered, "recovery counter resets only after character selection succeeds"); resets++;
                throw new OperationCanceledException("Entry verified; do not run a device queue in this check.");
            }, async () => { await Task.Yield(); entered = true; });
        }
        catch (OperationCanceledException) { }
        assert(resets == 1, "successful side-story entry resets the counter once");
    }

    static async Task VerifySideTargetChanges(Action<bool, string> assert)
    {
        async Task<List<int>> Collect(SideStoryOptions options, Action<int>? changeAfterReturn = null,
            Action? selectWhileWaiting = null, ISet<int>? completed = null)
        {
            var visited = new List<int>();
            await RunSideCharacterQueue(options, completed ?? new HashSet<int>(), id =>
            {
                visited.Add(id);
                if (visited.Count > 26) throw new Exception("Side queue repeated completed characters.");
                return Task.CompletedTask;
            }, () => { changeAfterReturn?.Invoke(visited[^1]); return Task.CompletedTask; }, () =>
            {
                if (selectWhileWaiting == null) throw new Exception("Unexpected empty selection.");
                selectWhileWaiting(); return Task.CompletedTask;
            });
            return visited;
        }

        var expand = new SideStoryOptions(false, [2]);
        var expanded = await Collect(expand, id => { if (id == 2) expand.SetCharacters(null); });
        assert(expanded.SequenceEqual(new[] { 2 }.Concat(SideOrder(Enumerable.Range(1, 26)).Where(id => id != 2))),
            "selected-to-all on return from the last already-read character continues through every remaining character");

        var narrow = new SideStoryOptions(false);
        var narrowed = await Collect(narrow, id => { if (id == 21) narrow.SetCharacters([9, 17]); });
        assert(narrowed.SequenceEqual([21, 9, 17]), "all-to-selected applies at the character-selection boundary without visiting removed targets");

        var add = new SideStoryOptions(false, [2]);
        var added = await Collect(add, id => { if (id == 2) add.SetCharacters([2, 14]); });
        assert(added.SequenceEqual([2, 14]), "adding a character at the old completion boundary prevents premature completion");

        var remove = new SideStoryOptions(false, [2, 14]);
        var removed = await Collect(remove, id => { if (id == 2) remove.SetCharacters([9]); });
        assert(removed.SequenceEqual([2, 9]), "changing picked icons drops removed future targets and adds the latest ones");

        var empty = new SideStoryOptions(false, [2]); var waits = 0;
        var resumed = await Collect(empty, id => { if (id == 2) empty.SetCharacters([]); },
            () => { waits++; empty.SetCharacters([14]); });
        assert(waits == 1 && resumed.SequenceEqual([2, 14]), "an empty live selection waits at character selection and resumes when a target is picked");

        var latest = new SideStoryOptions(false, [9, 17]);
        assert((await Collect(latest, completed: new HashSet<int> { 9 })).SequenceEqual([17]),
            "recovery uses the latest targets and preserves completed characters");

        var input = new[] { 17, 9, 9 }; var copied = new SideStoryOptions(false, input); input[0] = 0;
        assert(!copied.Selection.AllCharacters && copied.Selection.Characters.SequenceEqual([9, 17]), "live selections are normalized copies of request arrays");
        var original = copied.Selection;
        assert(!copied.SetCharacters([17, 9]) && ReferenceEquals(original, copied.Selection), "equivalent updates preserve the current selection snapshot");
        var rejected = false;
        try { copied.SetCharacters([27]); } catch (InvalidOperationException) { rejected = true; }
        assert(rejected && ReferenceEquals(original, copied.Selection), "invalid live targets leave the existing selection intact");
    }
}
