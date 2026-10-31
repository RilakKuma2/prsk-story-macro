using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    static async Task VerifyRecoveryCounting(Action<bool, string> assert)
    {
        async Task Check(RecoveryCause[] failures, bool resetOnEntry, int expectedRestarts, bool shouldFail)
        {
            var pending = new Queue<RecoveryCause>(failures); var restarts = 0; var revoked = 0; var resets = 0;
            Task Run() => pending.Count == 0 ? Task.CompletedTask : throw new RecoveryNeeded(pending.Dequeue(), "test interruption");
            Exception? failure = null;
            try
            {
                await RunRecoveryAttempts(Run, _ => { revoked++; return Task.CompletedTask; },
                    _ => { restarts++; return Task.FromResult(true); },
                    reset => { if (resetOnEntry) { reset(); resets++; } return Run(); }, () => true);
            }
            catch (InvalidOperationException ex) { failure = ex; }
            assert(restarts == expectedRestarts && (failure != null) == shouldFail && revoked == expectedRestarts + (shouldFail ? 1 : 0),
                $"recovery sequence {string.Join(',', failures)}, catalogue reset={resetOnEntry}");
            if (resetOnEntry) assert(resets == restarts, "every successful story entry clears the unknown-state counter");
        }
        await Check([RecoveryCause.GameExited, RecoveryCause.GameExited, RecoveryCause.GameExited], false, 3, false);
        await Check([RecoveryCause.NewData, RecoveryCause.GameExited, RecoveryCause.NewData], false, 3, false);
        await Check([RecoveryCause.UnknownState, RecoveryCause.UnknownState], false, 1, true);
        await Check([RecoveryCause.GameExited, RecoveryCause.UnknownState, RecoveryCause.UnknownState], false, 2, true);
        await Check([RecoveryCause.UnknownState, RecoveryCause.GameExited, RecoveryCause.UnknownState], false, 2, true);
        await Check([RecoveryCause.UnknownState, RecoveryCause.UnknownState, RecoveryCause.UnknownState], true, 3, false);
        await Check([RecoveryCause.GameExited, RecoveryCause.UnknownState, RecoveryCause.GameExited], true, 3, false);
        var disabledRestarts = 0;
        try
        {
            await RunRecoveryAttempts(() => throw new RecoveryNeeded(RecoveryCause.GameExited, "test exit"), _ => Task.CompletedTask,
                _ => { disabledRestarts++; return Task.FromResult(true); }, _ => Task.CompletedTask, () => false);
            assert(false, "auto resume off must stop");
        }
        catch (OperationCanceledException) { assert(disabledRestarts == 0, "auto resume off never restarts even after a crash"); }
    }
    static async Task VerifyRestoredHandling(Action<bool, string> assert)
    {
        var attempts = 0; var reconnects = 0; var waits = new List<int>();
        var recovered = await RunAdbReadWithReconnect(
            () => ++attempts < 4 ? throw new IOException("device offline") : Task.FromResult(new byte[] { 7 }),
            () => { reconnects++; return Task.CompletedTask; }, CancellationToken.None,
            attempt => { waits.Add(attempt); return Task.CompletedTask; });
        assert(attempts == 4 && reconnects == 3 && recovered[0] == 7 && waits.SequenceEqual(new[] { 1, 2, 3 }), "transient ADB failure reconnects with the original bounded backoff");
        attempts = reconnects = 0;
        try
        {
            await RunAdbReadWithReconnect(() => { attempts++; throw new IOException("connection reset"); },
                () => { reconnects++; return Task.CompletedTask; }, CancellationToken.None, _ => Task.CompletedTask);
            assert(false, "persistent ADB failure must surface");
        }
        catch (IOException) { assert(attempts == 4 && reconnects == 3, "persistent ADB failure stops after three reconnects"); }
        foreach (var input in new[] { "input tap 480 270", "input swipe 700 400 700 240 1200", "am force-stop " + Package, "monkey -p " + Package, "sh /data/local/tmp/taps.sh" })
            assert(!IsRepeatableAdbRead(["-s", "127.0.0.1:16384", "shell", input]), "ADB recovery cannot replay input: " + input);
        assert(IsRepeatableAdbRead(["-s", "127.0.0.1:16384", "exec-out", "screencap", "-p"]), "fresh screenshots permit transport retries");
        var selectedBeforeRetryCheck = Selected;
        try
        {
            Selected = new(4000, "Nox retry test", 1, true, true, 62001);
            assert(IsSelectedGameStop(["-s", Serial, "shell", "am force-stop " + Package]), "authorized selected-game stop is idempotent across transport reconnects");
            assert(!IsSelectedGameStop(["-s", "127.0.0.1:62024", "shell", "am force-stop " + Package]), "game-stop retry cannot target another instance");
            foreach (var command in new[] { "input tap 480 270", "input swipe 700 400 700 240 1200", "monkey -p " + Package, "pm clear " + Package, "am force-stop com.other.app" })
                assert(!IsSelectedGameStop(["-s", Serial, "shell", command]), "game-stop retry excludes other mutations: " + command);
        }
        finally { Selected = selectedBeforeRetryCheck; }
        using (var cancel = new CancellationTokenSource())
        {
            reconnects = 0;
            try
            {
                await RunAdbReadWithReconnect(() => throw new IOException("device offline"),
                    () => { reconnects++; return Task.CompletedTask; }, cancel.Token,
                    _ => { cancel.Cancel(); return Task.CompletedTask; });
                assert(false, "cancel during backoff must stop");
            }
            catch (OperationCanceledException) { assert(reconnects == 0, "stop request cancels reconnect backoff"); }
        }

        var path = Path.Combine(Artifacts, "record-lock-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "1");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                assert(!await SaveRecord(path, "2"), "locked status file does not terminate the workflow");
                assert(File.ReadAllText(path) == "1" && File.ReadAllText(path + ".pending") == "2", "replacement failure retains old file and newest pending record");
                LatestRecords.TryRemove(path, out _);
                assert(LoadRecord<int>(path) == 2, "fresh process can restore the newest pending record");
                using var pendingLock = new FileStream(path + ".pending", FileMode.Open, FileAccess.Read, FileShare.Read);
                assert(!await SaveRecord(path, "3") && LoadRecord<int>(path) == 3, "fully locked records preserve in-memory progress without stopping");
            }
            assert(await SaveRecord(path, "4") && File.ReadAllText(path) == "4", "record persistence resumes when the lock clears");
            var survived = false;
            SaveEvidence(path + ".png", () => throw new UnauthorizedAccessException("test image lock"));
            survived = true;
            assert(survived, "evidence write failure cannot prevent recovery");

            using var top = new Bitmap(Path.Combine(Root, "tests", "fixtures", "catalogue-top-filtered.png"));
            using var next = new Bitmap(Path.Combine(Root, "tests", "fixtures", "catalogue-next-filtered.png"));
            var vm = new Vm(0, "test", 123, true, true, 16384);
            var queue = NewQueueProgress(vm) with { Current = CatalogueSignature(top), Stage = "reading" };
            queue = MarkCurrentStoryComplete(queue) with { Current = CatalogueSignature(next), Stage = "reading" };
            await SaveRecord(path, JsonSerializer.Serialize(queue));
            LatestRecords.TryRemove(path, out _);
            var restored = LoadRecord<QueueProgress>(path)!;
            assert(restored.Completed.Count == 1 && SameCatalogueSignature(restored.Completed[0], CatalogueSignature(top)) &&
                SameCatalogueSignature(restored.Current!, CatalogueSignature(next)), "process restart restores completed stories and the unfinished story");
            assert(QueueBelongsTo(restored, vm) && !QueueBelongsTo(restored, vm with { Created = 124 }), "resume records belong to the selected instance identity");
        }
        finally
        {
            LatestRecords.TryRemove(path, out _); RecordWarnings.TryRemove(path, out _);
            File.Delete(path); File.Delete(path + ".pending");
        }

        using var reader = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-reader-fade.png"));
        using var voiceDialog = new Bitmap(Path.Combine(Root, "tests", "fixtures", "story-voice-dialog.png"));
        assert(FindColorDialogClose(voiceDialog) != null && FindNoticeAction(voiceDialog) == null &&
            FindHomeAction(voiceDialog) == null && FindStoryAction(voiceDialog) == null,
            "real story-start dialog with a gray/cyan/X layout must not be dismissed as a notice");
        assert(FindReaderAction(voiceDialog)?.Name is "story-continuous-on" or "story-no-voice",
            "story-start dialog retains the normal continuous-reading and voice choice route");
        foreach (var notice in new[]
        {
            ("feedback-heading.png", 425, 153, "feedback-do-not-send.png", 362, 397, "feedback-do-not-send"),
            ("challenge-unlock-heading.png", 334, 225, "challenge-unlock-close.png", 365, 309, "challenge-notice-close"),
            ("notice-header.png", 110, 22, "notice-close.png", 852, 24, "notice-x")
        })
        {
            using var overlay = new Bitmap(reader);
            using var heading = new Bitmap(Path.Combine(Root, "templates", notice.Item1));
            using var button = new Bitmap(Path.Combine(Root, "templates", notice.Item4));
            using (var graphics = Graphics.FromImage(overlay))
            {
                graphics.DrawImageUnscaled(heading, notice.Item2, notice.Item3);
                graphics.DrawImageUnscaled(button, notice.Item5, notice.Item6);
            }
            assert(FindNoticeAction(overlay)?.Name == notice.Item7 && !CanTapStory(overlay), "notice over a reader blocks dialogue input and retains the original close action");
        }
        using var shifted = new Bitmap(960, 540);
        using (var label = new Bitmap(Path.Combine(Root, "templates", "feedback-heading.png")))
        using (var graphics = Graphics.FromImage(shifted)) graphics.DrawImageUnscaled(label, 427, 155);
        assert(Matches(shifted, new("feedback", "feedback-heading.png", 0, 0, 425, 153, 112, 18)), "original local template alignment tolerates a two-pixel shift");
        using var episodes = new Bitmap(Path.Combine(Root, "tests", "fixtures", "top-story-episodes.png"));
        assert(EpisodeBadgeRows(episodes, true).Count > 0 && EpisodeBadgeRows(episodes, false).Count > 0, "real episode list detects both SKIP and new-story badges");
        using var onlyNew = new Bitmap(episodes);
        using (var graphics = Graphics.FromImage(onlyNew)) graphics.FillRectangle(Brushes.Black, 825, 155, 70, 230);
        assert(EpisodeBadgeRows(onlyNew, true).Count == 0 && FindEpisodeToRead(onlyNew, true) != null, "a new story remains readable without any SKIP badge");
    }
}
