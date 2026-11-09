using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    static void VerifyStoryRewardFlow(Action<bool, string> assert)
    {
        Bitmap Load(string name) => new(Path.Combine(Root, "tests", "fixtures", name + ".png"));
        using var tickets = Load("story-ticket-rewards");
        using var crystals = Load("story-completion-crystals");
        using var episodes = Load("episode-list-cleared");
        using var catalogue = Load("catalogue-top-filtered");
        using var reader = Load("story-reader-fade");
        var close = new HomeAction("story-rewards-banner-close", 480, 315);
        foreach (var (reward, require50) in new[] { (tickets, false), (crystals, true) })
        {
            assert(FindStoryRewardAction(reward) == close && FindReaderAction(reward) == close,
                "both actual sequential ticket and 50-crystal banners have an explicit close action");
            assert(!IsEpisodeList(reward) && !CanTapStory(reward),
                "neither reward banner is an uncovered list or a continuous-tap target");
            foreach (var background in new[] { catalogue, reader })
            {
                using var delayed = new Bitmap(background);
                using (var graphics = Graphics.FromImage(delayed))
                    graphics.DrawImage(reward, new Rectangle(0, 202, 960, 136), new Rectangle(0, 202, 960, 136), GraphicsUnit.Pixel);
                assert(!HasEpisodeListAnchor(delayed) && FindStoryRewardAction(delayed) == close,
                    "either banner arriving after navigation is recognized without the previous episode list");
                assert(!IsMainEventList(delayed) && FindCatalogueOpen(delayed) == null && FindEpisodeBack(delayed) == null,
                    "a delayed reward blocks opening another story and going back through its overlay");
                assert(!CanTapStory(delayed) && ReaderBlockReason(delayed) == "story-reward",
                    "a reward revokes continuous tapping even when the story ellipsis remains visible behind it");
            }
            using var changedQuantities = new Bitmap(reward);
            using (var graphics = Graphics.FromImage(changedQuantities))
                graphics.FillRectangle(Brushes.Black, 390, 274, 180, 19);
            assert(FindStoryRewardAction(changedQuantities) == (require50 ? null : close),
                $"crystal recognition requires x50 while ticket quantities may change (crystal={require50})");
            using var missingText = new Bitmap(reward);
            using (var graphics = Graphics.FromImage(missingText)) graphics.FillRectangle(Brushes.Black, 399, 307, 154, 18);
            assert(IsStoryRewardsBanner(missingText), "opaque ticket or crystal-x50 artwork identifies rewards without translucent text");
            using var missingTiles = new Bitmap(reward);
            using (var graphics = Graphics.FromImage(missingTiles)) graphics.FillRectangle(Brushes.Black, 200, 217, 560, 76);
            assert(!IsStoryRewardsBanner(missingTiles), "the reward message alone cannot authorize banner dismissal");
        }
        foreach (var name in new[] { "main-event-list", "top-story-episodes", "gacha-protected", "card-protected", "title-menu-protected", "story-voice-dialog", "story-download-dialog" })
        {
            using var unrelated = Load(name);
            assert(FindStoryRewardAction(unrelated) == null, "unrelated screens cannot trigger reward taps: " + name);
        }
        VerifyRewardItemLocations(assert);

        var gate = new StoryListReturnGate();
        var start = DateTime.UtcNow;
        assert(!gate.Observe(IsEpisodeList(tickets), start), "the first reward cannot complete a story return");
        foreach (var milliseconds in new[] { 100, 450, 800, 2600 })
            assert(!gate.Observe(IsEpisodeList(episodes), start.AddMilliseconds(milliseconds)),
                "the short uncovered-list gap between two rewards cannot complete a story return");
        assert(!gate.Observe(IsEpisodeList(crystals), start.AddMilliseconds(2800)),
            "the later 50-crystal reward restarts the uncovered-list wait");
        assert(!gate.Observe(true, start.AddSeconds(3)) && !gate.Observe(true, start.AddMilliseconds(5999)) && gate.Observe(true, start.AddSeconds(6)),
            "only a stable uncovered list after the second reward can return to episode verification");
        assert(!gate.Observe(false, start.AddSeconds(7)) && !gate.Observe(true, start.AddSeconds(8)),
            "a new overlay clears a previously completed return wait");
        var watchdog = new StoryActionWatchdog();
        assert(!watchdog.Observe(close, start) && !watchdog.Observe(close, start.AddSeconds(29)),
            "recognized rewards allow bounded dismissal retries");
        assert(watchdog.Observe(close, start.AddSeconds(30)), "an unchanged reward reaches recovery even though it is recognized");
        assert(!watchdog.Observe(null, start.AddSeconds(31)) && !watchdog.Observe(close, start.AddSeconds(32)),
            "leaving the dialog resets its timeout");
        assert(!watchdog.Observe(new("story-no-voice", 480, 406), start.AddSeconds(62)),
            "a different dialog starts a fresh timeout");
        VerifyStalledRewardRecovery(assert).GetAwaiter().GetResult();
        var savedAuto = AutoResumeEnabled;
        var savedMemory = RequestedMemoryGb;
        try
        {
            ApplyUserSettings(null);
            assert(AutoResumeEnabled && RequestedMemoryGb == 0, "missing or unreadable settings default to automatic recovery on");
            foreach (var json in new[] { "{}", "{\"MemoryGb\":6}" })
            {
                ApplyUserSettings(JsonSerializer.Deserialize<UserSettings>(json));
                assert(AutoResumeEnabled, "legacy settings without an automatic recovery choice default to on");
            }
            ApplyUserSettings(JsonSerializer.Deserialize<UserSettings>("{\"AutoResume\":false,\"MemoryGb\":6}"));
            assert(!AutoResumeEnabled && RequestedMemoryGb == 6, "an explicit saved off choice and RAM allocation remain intact");
            ApplyUserSettings(new UserSettings());
            assert(AutoResumeEnabled, "new settings use the enabled default");
        }
        finally { AutoResumeEnabled = savedAuto; RequestedMemoryGb = savedMemory; }
    }

    static void VerifyRewardItemLocations(Action<bool, string> assert)
    {
        using var ticket = new Bitmap(Path.Combine(Root, "templates", "story-reward-banner-ticket.png"));
        using var crystal = new Bitmap(Path.Combine(Root, "templates", "story-reward-crystal-50.png"));
        using var crystal25 = new Bitmap(Path.Combine(Root, "tests", "fixtures", "side-reward-result.png"));
        using var threeTickets = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-rewards-banner.png"));
        assert(IsStoryRewardsBanner(threeTickets), "the captured three-ticket layout is recognized as well as the two-ticket layout");
        foreach (var n in new[] { 1, 2, 3, 5, 8 })
        {
            using var layout = new Bitmap(960, 540);
            using (var graphics = Graphics.FromImage(layout))
                for (var i = 0; i < n; i++)
                    graphics.DrawImage(ticket, new Rectangle(449 - 44 * (n - 1) + 88 * i, 232, ticket.Width, ticket.Height),
                        new Rectangle(0, 0, ticket.Width, ticket.Height), GraphicsUnit.Pixel);
            assert(IsStoryRewardsBanner(layout), $"{n} centered ticket items are detected independently of the reward text");
        }
        foreach (var (left, top) in new[] { (43, 228), (98, 231), (317, 233), (449, 232), (665, 236), (852, 228) })
        {
            using var moved = new Bitmap(960, 540);
            using (var graphics = Graphics.FromImage(moved))
                graphics.DrawImage(ticket, new Rectangle(left, top, ticket.Width, ticket.Height),
                    new Rectangle(0, 0, ticket.Width, ticket.Height), GraphicsUnit.Pixel);
            assert(IsStoryRewardsBanner(moved), "ticket matching searches the reward row, including odd pixel positions and both edges");
        }
        foreach (var (left, top) in new[] { (454, 225), (452, 223), (458, 229), (410, 225), (498, 225), (454, 232) })
        {
            using var moved = new Bitmap(960, 540);
            using (var graphics = Graphics.FromImage(moved))
                graphics.DrawImage(crystal, new Rectangle(left, top, crystal.Width, crystal.Height),
                    new Rectangle(0, 0, crystal.Width, crystal.Height), GraphicsUnit.Pixel);
            assert(IsStoryRewardsBanner(moved), "crystal plus x50 tolerates small position shifts");
        }
        assert(!IsStoryRewardsBanner(crystal25), "a crystal with x25 is not a 50-crystal completion reward");
        using var onlyQuantity = new Bitmap(960, 540);
        using (var graphics = Graphics.FromImage(onlyQuantity))
            graphics.DrawImage(crystal, new Rectangle(477, 276, 37, 16), new Rectangle(23, 51, 37, 16), GraphicsUnit.Pixel);
        assert(!IsStoryRewardsBanner(onlyQuantity), "x50 alone is insufficient without the crystal artwork");
        using var outsideRow = new Bitmap(960, 540);
        using (var graphics = Graphics.FromImage(outsideRow))
            graphics.DrawImage(ticket, new Rectangle(449, 100, ticket.Width, ticket.Height),
                new Rectangle(0, 0, ticket.Width, ticket.Height), GraphicsUnit.Pixel);
        assert(!IsStoryRewardsBanner(outsideRow), "a ticket elsewhere on the screen cannot dismiss as a reward banner");
    }

    static async Task VerifyStalledRewardRecovery(Action<bool, string> assert)
    {
        Bitmap Load(string name) => new(Path.Combine(Root, "tests", "fixtures", name + ".png"));
        using var tickets = Load("story-ticket-rewards");
        using var crystals = Load("story-completion-crystals");
        using var episodes = Load("episode-list-cleared");
        var start = DateTime.UtcNow;
        var now = start;
        var firstClosed = false;
        var secondClosed = false;
        var taps = 0;
        var cleared = await ClearStalledStoryRewards(() =>
            Task.FromResult(new Bitmap(!firstClosed ? tickets : now - start >= TimeSpan.FromSeconds(2.5) && !secondClosed ? crystals : episodes)),
            action =>
            {
                assert(action == new HomeAction("story-rewards-banner-close", 480, 315), "stall recovery uses only the recognized reward action");
                taps++;
                if (!firstClosed) firstClosed = true; else secondClosed = true;
                return Task.FromResult(true);
            }, () => { now = now.AddMilliseconds(500); return Task.CompletedTask; }, () => now);
        assert(cleared && firstClosed && secondClosed && taps == 2 && now - start >= TimeSpan.FromSeconds(6),
            "stalled ticket then delayed crystal rewards each close once and resume only after the second settles");
        foreach (var reward in new[] { tickets, crystals })
        {
            now = start; taps = 0;
            cleared = await ClearStalledStoryRewards(() => Task.FromResult(new Bitmap(reward)),
                _ => { taps++; return Task.FromResult(true); },
                () => { now = now.AddMilliseconds(500); return Task.CompletedTask; }, () => now);
            assert(!cleared && taps == 4 && now - start == TimeSpan.FromSeconds(12),
                "an unresponsive reward cannot suppress automatic restart indefinitely");
        }
        foreach (var name in new[] { "episode-list-cleared", "gacha-protected", "title-menu-protected", "new-data-interruption" })
        {
            using var other = Load(name);
            now = start; taps = 0;
            cleared = await ClearStalledStoryRewards(() => Task.FromResult(new Bitmap(other)),
                _ => { taps++; return Task.FromResult(true); },
                () => { now = now.AddMilliseconds(500); return Task.CompletedTask; }, () => now);
            assert(!cleared && taps == 0, "stall recheck never taps a non-reward screen: " + name);
        }
        foreach (var auto in new[] { false, true })
        foreach (var single in new[] { false, true })
        {
            var rechecked = 0; var restarted = 0; var revoked = 0; var continued = 0;
            await RunRecoveryAttempts(() => throw new RecoveryNeeded(RecoveryCause.UnknownState, "reward timeout"),
                _ => { revoked++; return Task.CompletedTask; },
                _ => { restarted++; return Task.FromResult(true); }, _ => Task.CompletedTask, () => auto, single,
                _ => { rechecked++; return Task.FromResult(true); }, () => { continued++; return Task.CompletedTask; });
            assert(rechecked == 1 && continued == 1 && restarted == 0 && revoked == 0,
                $"reward recheck resumes in place before recovery, automatic={auto}, single={single}");
        }
        var fallback = 0;
        await RunRecoveryAttempts(() => throw new RecoveryNeeded(RecoveryCause.UnknownState, "not a reward"),
            _ => Task.CompletedTask, _ => { fallback++; return Task.FromResult(true); }, _ => Task.CompletedTask, () => true,
            beforeRecovery: _ => Task.FromResult(false));
        assert(fallback == 1, "a failed reward recheck falls through to ordinary automatic recovery");
        var protectedRestart = false;
        await RunRecoveryAttempts(() => throw new RecoveryNeeded(RecoveryCause.UnknownState, "changed screen"),
            _ => Task.CompletedTask, failure => { protectedRestart = failure.Cause == RecoveryCause.AccountSafety; return Task.FromResult(true); },
            _ => Task.CompletedTask, () => false,
            beforeRecovery: _ => throw new RecoveryNeeded(RecoveryCause.AccountSafety, "protected during fresh capture"));
        assert(protectedRestart, "fresh account protection during a reward recheck follows the protected recovery path");
    }
}
