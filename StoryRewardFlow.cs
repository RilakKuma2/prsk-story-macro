using System.Drawing;

internal static partial class Program
{
    static async Task<bool> TryDismissStalledStoryRewards(RecoveryNeeded failure)
    {
        if (failure.Cause != RecoveryCause.UnknownState) return false;
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) return false;
        if (ReaderTouch != null) await SetReaderLease(false);
        Log("상태 인식 정지 · 재시작 전에 티켓/크리스탈 보상 수령창 재검사");
        var cleared = await ClearStalledStoryRewards(() => Capture(), async action =>
        {
            CopyEvidence(ShotPath, Path.Combine(Artifacts, $"instance-{Selected!.Index}-stalled-reward.png"));
            return await GuardedTap(action, FindStoryRewardAction);
        }, () => Task.Delay(500, Stop.Token), () => DateTime.UtcNow);
        if (cleared) Log("보상 수령창 처리 완료 · 게임 재시작 없이 현재 스토리 이어 읽기");
        return cleared;
    }

    static async Task<bool> ClearStalledStoryRewards(Func<Task<Bitmap>> capture,
        Func<HomeAction, Task<bool>> tap, Func<Task> wait, Func<DateTime> now)
    {
        var until = now().AddSeconds(12);
        var lastTap = DateTime.MinValue;
        var attempts = 0;
        var handled = false;
        var settled = new StoryListReturnGate();
        while (now() < until)
        {
            using var screen = await capture();
            if (ProtectedScreenReason(screen) != null || IsNewDataNotice(screen)) return false;
            if (FindStoryRewardAction(screen) is { } reward)
            {
                settled.Observe(false, now());
                if (attempts < 4 && now() - lastTap >= TimeSpan.FromSeconds(2))
                {
                    attempts++;
                    handled |= await tap(reward);
                    lastTap = now();
                }
            }
            else
            {
                if (!handled) return false;
                var usable = IsEpisodeList(screen) || IsMainEventList(screen) || IsEmptyVirtualCatalogue(screen) ||
                    CanTapStory(screen) || ReadingSideStory && IsSideStoryList(screen);
                if (settled.Observe(usable, now())) return true;
            }
            await wait();
        }
        return false;
    }

    // A ticket banner can be followed by a separate 50-crystal banner. An uncovered
    // episode list between them must remain stable before the reader hands it back.
    sealed class StoryListReturnGate
    {
        static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
        DateTime? visibleSince;
        public bool Observe(bool uncoveredList, DateTime now)
        {
            if (!uncoveredList) { visibleSince = null; return false; }
            visibleSince ??= now;
            return now - visibleSince.Value >= QuietPeriod;
        }
    }
}
