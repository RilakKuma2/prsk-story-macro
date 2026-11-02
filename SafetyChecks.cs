using System.Drawing;

internal static partial class Program
{
    static void VerifySafety()
    {
        VerifyEmulatorChanges();
        var checks = 0;
        void Assert(bool ok, string name) { if (!ok) throw new InvalidOperationException("검증 실패: " + name); checks++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException("차단 실패: " + name);
        }
        foreach (var command in new[] { "run", "reset", "restore", "tutorial", "resume", "stage", "tap", "swipe", "provision", "read-story", "web" })
            Reject(() => ValidateArguments([command]), "legacy " + command);
        Reject(() => ValidateArguments(["home", "--instance", "all"]), "broadcast instance");
        Reject(() => ValidateArguments(["home", "--instance", "0", "--from", "reset"]), "extra options");
        Selected = new(1000, "test", 1, true, true, 16384);
        try
        {
            foreach (var command in new[] { "pm clear com.sega.pjsekai", "pm uninstall com.sega.pjsekai", "rm -rf /sdcard/Android/data/com.sega.pjsekai", "input tap 928 32", "input keyevent 4", "getprop sys.boot_completed; input tap 1 1" })
                Reject(() => ValidateProcessArgs(["-s", Serial, "shell", command], false), command);
            Reject(() => ValidateProcessArgs(["shell", "getprop sys.boot_completed"], false), "missing serial");
            Reject(() => ValidateProcessArgs(["-s", "127.0.0.1:16416", "shell", "getprop sys.boot_completed"], false), "other instance");
            Reject(() => ValidateProcessArgs(["delete", "-v", "0"], true), "manager destructive verb");
            Reject(() => ValidateProcessArgs(["control", "-v", "0", "shutdown"], true), "VM shutdown requires the RAM adjustment authorization");
            Reject(() => ValidateProcessArgs(MemoryWriteArguments(0, 2), true), "RAM write requires an explicit one-shot capability");
            MemoryShutdownAuthorized = true;
            Reject(() => ValidateProcessArgs(["control", "-v", "1", "shutdown"], true), "RAM adjustment cannot stop another VM");
            ValidateProcessArgs(["control", "-v", "0", "shutdown"], true);
            Reject(() => ValidateProcessArgs(["control", "-v", "0", "shutdown"], true), "RAM shutdown authorization is consumed");
            var memoryArgs = MemoryWriteArguments(0, 2);
            AuthorizedMemoryCommand = string.Join('\0', memoryArgs);
            var excessiveMemory = memoryArgs.ToArray(); excessiveMemory[^1] = "16";
            Reject(() => ValidateProcessArgs(excessiveMemory, true), "RAM write cannot exceed the authorized 8GB");
            ValidateProcessArgs(memoryArgs, true);
            Reject(() => ValidateProcessArgs(memoryArgs, true), "RAM settings capability is consumed");
            Assert(CanIncreaseMemory(3, true, new(32, 17)), "host has room for 3GB to 8GB plus reserve");
            Assert(!CanIncreaseMemory(3, true, new(32, 10)), "host reserve prevents memory pressure");
            Assert(!CanIncreaseMemory(3, false, new(32, 12)), "an offline VM needs the entire new allocation, not only the increase");
            Assert(!CanIncreaseMemory(8, true, new(32, 20)) && !CanIncreaseMemory(12, true, new(32, 20)), "existing 8GB or higher settings are retained");
            Assert(MaximumMemoryGb(3, true, new(32, 17)) == 12 && MaximumMemoryGb(3, true, new(32, 10)) == 6 && MaximumMemoryGb(3, true, new(32, 1)) == 3, "RAM slider follows host headroom within 3 to 12GB");
            Assert(TargetMemoryGb(3, true, new(32, 17), 4) == 4 && TargetMemoryGb(3, true, new(32, 10), 8) == 6, "RAM choice is retained and limited by current headroom");
            Assert(MemoryWriteArguments(0, 2, 4)[^1] == "4" && CanSetMemory(8, 4, true, new(32, 2)), "manual lower RAM setting is supported without increasing memory pressure");
            Assert(TargetMemoryGb(3, true, new(32, 17), 0) == 8 && TargetMemoryGb(8, true, new(32, 17), 12) == 12, "automatic RAM target stays at 8GB while manual 12GB remains available");
            Assert(MemoryWriteArguments(0, 2, 3)[^1] == "3" && MemoryWriteArguments(0, 2, 12)[^1] == "12", "RAM endpoints 3GB and 12GB are supported");
            Reject(() => MemoryWriteArguments(0, 2, 13), "RAM write builder rejects values over 12GB");
            Reject(() => MemoryWriteArguments(0, 2, 2), "RAM write builder rejects values below 3GB");
            var savedAutoResume = AutoResumeEnabled;
            try
            {
                AutoResumeEnabled = false; RestartAuthorized = true;
                Reject(() => ValidateProcessArgs(["-s", Serial, "shell", "am force-stop " + Package], false), "auto resume off blocks restart");
                AutoResumeEnabled = true; RestartAuthorized = false;
                Reject(() => ValidateProcessArgs(["-s", Serial, "shell", "am force-stop " + Package], false), "new data authorization required");
                RestartAuthorized = true;
                Reject(() => ValidateProcessArgs(["-s", Serial, "shell", "am force-stop com.other.app"], false), "only Sekai can be stopped");
                ValidateProcessArgs(["-s", Serial, "shell", "am force-stop " + Package], false);
                Reject(() => ValidateProcessArgs(["-s", Serial, "shell", "am force-stop " + Package], false), "restart authorization consumed");
            }
            finally { AutoResumeEnabled = savedAutoResume; RestartAuthorized = false; }
            AuthorizedInput = "input tap 754 205";
            ValidateProcessArgs(["-s", Serial, "shell", "input tap 754 205"], false);
            Reject(() => ValidateProcessArgs(["-s", Serial, "shell", "input tap 754 205"], false), "one-shot input capability");
        }
        finally { Selected = null; AuthorizedInput = null; MemoryShutdownAuthorized = false; AuthorizedMemoryCommand = null; }
        using var original = new Bitmap(Path.Combine(Root, "tests", "fixtures", "first-login-dialog.png"));
        var found = FindColorDialogClose(original);
        Assert(found is { X: >= 750 and <= 757, Y: >= 200 and <= 208 }, "real gray/cyan/X popup");
        using var changedText = new Bitmap(original);
        using (var g = Graphics.FromImage(changedText))
        {
            g.FillRectangle(new SolidBrush(Color.FromArgb(235, 235, 242)), 310, 220, 350, 65);
            g.DrawString("Different notice / 12345", SystemFonts.DefaultFont, Brushes.DarkSlateBlue, 340, 240);
        }
        Assert(FindColorDialogClose(changedText) == found, "different content still closes X");
        using var noButton = new Bitmap(original);
        using (var g = Graphics.FromImage(noButton)) g.FillRectangle(new SolidBrush(Color.FromArgb(235, 235, 242)), 485, 292, 175, 55);
        Assert(FindColorDialogClose(noButton) == null, "no cyan button means no click");
        using var noX = new Bitmap(original);
        using (var g = Graphics.FromImage(noX)) g.FillRectangle(new SolidBrush(Color.FromArgb(235, 235, 242)), 740, 191, 28, 29);
        Assert(FindColorDialogClose(noX) == null, "no X means no click");
        using var shifted = new Bitmap(960, 540);
        using (var g = Graphics.FromImage(shifted)) { g.Clear(Color.Black); g.DrawImageUnscaled(original, 20, 30); }
        Assert(FindColorDialogClose(shifted) == new Point(found!.Value.X + 20, found.Value.Y + 30), "X position is searched, not fixed");
        using var blank = new Bitmap(960, 540);
        Assert(FindHomeAction(blank) == null && !IsAreaMain(blank), "unknown screen has no action");
        using var wrongSize = new Bitmap(1280, 720);
        Assert(FindHomeAction(wrongSize) == null && !IsAreaMain(wrongSize), "unexpected resolution has no action");
        using var main = new Bitmap(Path.Combine(Root, "tests", "fixtures", "main.png"));
        using var menu = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-menu.png"));
        using var download = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-download-dialog.png"));
        using var loading = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-downloading.png"));
        using var guide = new Bitmap(Path.Combine(Root, "tests", "fixtures", "main-event-guide.png"));
        using var list = new Bitmap(Path.Combine(Root, "tests", "fixtures", "main-event-list.png"));
        Assert(FindStoryAction(main)?.Name == "home-story", "home STORY entry");
        using var alternateHome = new Bitmap(Path.Combine(Root, "tests", "fixtures", "main-alternate-background.png"));
        Assert(FindStoryAction(alternateHome)?.Name == "home-story", "globe and LIVE allow STORY on a different area background");
        using var noGlobe = new Bitmap(alternateHome);
        using (var graphics = Graphics.FromImage(noGlobe)) graphics.FillRectangle(Brushes.Black, 18, 18, 27, 27);
        Assert(FindStoryAction(noGlobe)?.Name != "home-story", "missing globe blocks STORY");
        using var noLive = new Bitmap(alternateHome);
        using (var graphics = Graphics.FromImage(noLive)) graphics.FillRectangle(Brushes.Black, 883, 477, 46, 24);
        Assert(FindStoryAction(noLive)?.Name != "home-story", "missing LIVE blocks STORY");
        Assert(FindStoryAction(menu)?.Name == "main-event-story", "main/event card entry");
        Assert(FindStoryAction(download)?.Name == "story-download", "explicit download wins over generic X");
        Assert(IsStoryLoading(loading) && FindStoryAction(loading) == null, "download/loading sends no input");
        Assert(FindStoryAction(guide)?.Name == "story-optional-guide-close" && !IsMainEventList(guide), "optional guide only when visible");
        Assert(IsMainEventList(list) && FindStoryAction(list) == null && !IsStoryGuide(list), "no guide required on later visits");
        Assert(ReadStoryPercent(list).GetAwaiter().GetResult() == 1, "real screen percent OCR");
        foreach (var value in new[] { 0, 1, 9, 10, 48, 99, 100 }) Assert(ParsePercent(value + " %") == value, "parse " + value);
        foreach (var value in new[] { 1.5m, 1.7m, 99.9m }) Assert(ParsePercent(value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%") == value, "decimal percent " + value);
        foreach (var invalid in new[] { "101%", "1000%", "-1%", "100.1%", "1%2%", "1", "I%" }) Assert(ParsePercent(invalid) == null, "reject " + invalid);
        using var newData = new Bitmap(Path.Combine(Root, "tests", "fixtures", "new-data-interruption.png"));
        Assert(IsNewDataNotice(newData) && !CanTapStory(newData), "new data stops dialogue input");
        Assert(FindHomeAction(newData) == null && FindReaderAction(newData) == null, "new data is not a generic dismissible popup");
        foreach (var other in new[] { original, menu, download, guide, list }) Assert(!IsNewDataNotice(other), "other dialog cannot trigger restart");
        using var missingMessage = new Bitmap(newData);
        using (var graphics = Graphics.FromImage(missingMessage)) graphics.FillRectangle(Brushes.Black, 384, 234, 188, 38);
        Assert(!IsNewDataNotice(missingMessage), "new-data message anchor required");
        using var missingOk = new Bitmap(newData);
        using (var graphics = Graphics.FromImage(missingOk)) graphics.FillRectangle(Brushes.Black, 467, 308, 27, 21);
        Assert(!IsNewDataNotice(missingOk), "new-data OK anchor required");
        Assert(!CanAutoRecover(false, 0), "auto resume off never restarts");
        Assert(CanAutoRecover(true, 0), "first interruption may restart");
        Assert(CanAutoRecover(true, 1) && !CanAutoRecover(true, 2), "only two unresolved unknown-state failures stop recovery");
        VerifyRecoveryCounting(Assert).GetAwaiter().GetResult();
        foreach (var command in new[] { "reset", "delete", "remove", "tutorial", "tap", "from-start", "run" })
            Assert(!IsPanelCommand(command), "panel rejects " + command);
        Assert(IsGoogleSignIn("topResumedActivity=ActivityRecord{ com.google.android.gms/.auth.uiflows.minutemaid.MinuteMaidActivity }"), "original Google sign-in return path");
        using var title = new Bitmap(Path.Combine(Root, "tests", "fixtures", "title-animated.png"));
        Assert(FindHomeAction(title)?.Name == "title-start", "original title recognition tolerates animated prompt backdrop");
        using var fadedReader = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-reader-fade.png"));
        Assert(CanTapStory(fadedReader), "visible three-dot glyph survives a light fade");
        using var absentDots = new Bitmap(fadedReader);
        using (var graphics = Graphics.FromImage(absentDots)) graphics.FillRectangle(Brushes.White, 918, 26, 20, 12);
        Assert(!CanTapStory(absentDots), "missing dots still revoke touch");
        foreach (var other in new[] { original, menu, download, guide, list, title, blank, newData })
            Assert(!CanTapStory(other), "non-reader cannot receive dialogue taps");
        using var rewardBanner = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-rewards-banner.png"));
        Assert(IsStoryRewardsBanner(rewardBanner), "real rewards-obtained banner");
        Assert(!IsEpisodeList(rewardBanner), "covered list cannot count as completed or at an edge");
        Assert(FindReaderAction(rewardBanner)?.Name == "story-rewards-banner-close", "completion banner closes before list verification");
        Assert(!CanTapStory(rewardBanner), "reward banner never permits dialogue tapping");
        using var missingRewardText = new Bitmap(rewardBanner);
        using (var graphics = Graphics.FromImage(missingRewardText)) graphics.FillRectangle(Brushes.Black, 399, 307, 154, 18);
        Assert(IsStoryRewardsBanner(missingRewardText), "opaque ticket artwork does not require the translucent reward message");
        using var missingRewardTiles = new Bitmap(rewardBanner);
        using (var graphics = Graphics.FromImage(missingRewardTiles)) graphics.FillRectangle(Brushes.Black, 200, 217, 560, 76);
        Assert(!IsStoryRewardsBanner(missingRewardTiles), "reward tile is required");
        using var differentRewardBackground = new Bitmap(rewardBanner);
        using (var graphics = Graphics.FromImage(differentRewardBackground))
        {
            graphics.FillRectangle(Brushes.Black, 0, 0, 350, 540);
            graphics.FillRectangle(Brushes.Navy, 350, 0, 550, 200);
            graphics.FillRectangle(Brushes.Black, 390, 275, 210, 18); // different quantities
        }
        Assert(IsStoryRewardsBanner(differentRewardBackground), "background and item quantities do not determine the banner");
        foreach (var other in new[] { original, menu, download, guide, list, title, blank, newData, fadedReader })
            Assert(!IsStoryRewardsBanner(other), "unrelated screens do not dismiss as rewards");
        using var catalogueTop = new Bitmap(Path.Combine(Root, "tests", "fixtures", "catalogue-top-filtered.png"));
        using var catalogueNext = new Bitmap(Path.Combine(Root, "tests", "fixtures", "catalogue-next-filtered.png"));
        using var clearedEpisodes = new Bitmap(Path.Combine(Root, "tests", "fixtures", "episode-list-cleared.png"));
        Assert(IsStoryFilterActive(catalogueTop) && !IsStoryFilterActive(list), "filter is checked by its pink color");
        Assert(IsCatalogueSelection(catalogueTop) && HasNextCatalogueCard(catalogueTop), "selected card and following card recognized");
        Assert(FindCatalogueOpen(catalogueTop)?.Name == "catalogue-open-selected" && FindCatalogueOpen(list) == null, "entry requires the active filter");
        using var selectedCatalogueCard = CatalogueCard(catalogueTop);
        Assert(SameCatalogueCard(catalogueTop, selectedCatalogueCard) && !SameCatalogueCard(catalogueNext, selectedCatalogueCard), "already-checked stories are distinguished from the next story");
        using var lastCatalogue = new Bitmap(catalogueTop);
        using (var graphics = Graphics.FromImage(lastCatalogue)) graphics.FillRectangle(Brushes.Black, 94, 340, 178, 80);
        Assert(IsCatalogueSelection(lastCatalogue) && !HasNextCatalogueCard(lastCatalogue), "absent next card cannot be tapped");
        Assert(FindEpisodeBack(clearedEpisodes)?.Name == "story-back-to-catalogue", "verified episode list can return through back");
        Assert(FindEpisodeBack(rewardBanner) == null && FindEpisodeBack(main) == null, "reward overlay and home cannot trigger episode back");
        Assert(ReadStoryPercent(catalogueTop).GetAwaiter().GetResult() == 1.7m, "current decimal completion rate OCR");
        using var returnedCatalogue = new Bitmap(Path.Combine(Root, "tests", "fixtures", "catalogue-after-completion.png"));
        Assert(IsMainEventList(returnedCatalogue) && IsCatalogueSelection(returnedCatalogue), "actual return after completion is a selectable catalogue despite changed artwork");
        Assert(IsStoryFilterActive(returnedCatalogue) && HasNextCatalogueCard(returnedCatalogue) && FindCatalogueOpen(returnedCatalogue) != null,
            "active filter and next story remain available after completion");
        Assert(ReadStoryPercent(returnedCatalogue).GetAwaiter().GetResult() == 2.5m, "completion percentage remains readable after the story background changes");
        using var missingFilter = new Bitmap(returnedCatalogue);
        using (var graphics = Graphics.FromImage(missingFilter)) graphics.FillRectangle(Brushes.White, 746, 20, 25, 26);
        Assert(!IsMainEventList(missingFilter) && FindCatalogueOpen(missingFilter) == null, "a blank circle without the filter glyph does not permit catalogue input");
        using var missingCatalogueHeading = new Bitmap(returnedCatalogue);
        using (var graphics = Graphics.FromImage(missingCatalogueHeading)) graphics.FillRectangle(Brushes.Black, 412, 14, 85, 14);
        Assert(IsCatalogueSelection(missingCatalogueHeading), "catalogue recognition does not depend on its heading");
        using var missingSelectedEdges = new Bitmap(returnedCatalogue);
        using (var graphics = Graphics.FromImage(missingSelectedEdges)) graphics.FillRectangle(Brushes.Black, 95, 215, 255, 110);
        Assert(IsCatalogueSelection(missingSelectedEdges), "catalogue recognition does not depend on selected card borders or artwork");
        using var missingPercent = new Bitmap(returnedCatalogue);
        using (var graphics = Graphics.FromImage(missingPercent)) graphics.FillRectangle(Brushes.Black, 407, 28, 98, 28);
        Assert(IsCatalogueSelection(missingPercent) && FindCatalogueOpen(missingPercent) != null,
            "filter and bottom-right button permit entry even when the percentage is unreadable");
        using var missingCatalogueButton = new Bitmap(returnedCatalogue);
        using (var graphics = Graphics.FromImage(missingCatalogueButton)) graphics.FillRectangle(Brushes.Black, 768, 480, 162, 42);
        Assert(!IsCatalogueSelection(missingCatalogueButton), "bottom-right story button is required for catalogue recognition");
        VerifyCaptureContention(Assert).GetAwaiter().GetResult();
        VerifyRestoredHandling(Assert).GetAwaiter().GetResult();
        Log($"안전 검증 {checks}개 통과. 장치 명령은 전송하지 않았습니다.");
    }
}
