using System.Drawing;
internal static partial class Program
{
    // This route deliberately never calls ReadStoryQueue, AdvanceCatalogue or ReturnToStoryCatalogue.
    // A single-event run stops on every interruption, even when automatic recovery is enabled.
    static async Task ReadSingleEvent()
    {
        await ConnectVm();
        using var first = await CaptureUncoveredEpisodeList();
        await RunWithNewDataRecovery(async () =>
        {
            await ReadAndVerifyCurrentStory();
            Log("1개 이벤스 완독 완료 · 현재 에피소드 목록에서 중지");
        }, singleEvent: true);
    }
    static int ChooseInstance(IReadOnlyList<Vm> instances, int selected)
    {
        var running=instances.Where(vm=>vm.Running).ToList();
        if(running.Count==1) return running[0].Index;
        if(running.Count>1) return running.Any(vm=>vm.Index==selected) ? selected : -1;
        return instances.Any(vm=>vm.Index==selected) ? selected : instances.Count==1 ? instances[0].Index : -1;
    }
}
