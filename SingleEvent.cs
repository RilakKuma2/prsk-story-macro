using System.Drawing;
internal static partial class Program
{
    // This route deliberately never calls ReadStoryQueue, AdvanceCatalogue or ReturnToStoryCatalogue.
    // Capture the event artwork once so a restart cannot silently continue in a different event.
    static async Task ReadSingleEvent()
    {
        await ConnectVm();
        using var first = await CaptureUncoveredEpisodeList();
        using var eventArt = first.Clone(new Rectangle(45,300,350,130), first.PixelFormat);
        await RunWithNewDataRecovery(async () =>
        {
            await ReadAndVerifyCurrentStory();
            Log("1개 이벤스 완독 완료 · 현재 에피소드 목록에서 중지");
        }, async () =>
        {
            await OpenSelectedCatalogueStory(singleEvent: true);
            using var resumed = await CaptureUncoveredEpisodeList();
            if (DifferenceAt(resumed,eventArt,45,300,350,130) >= 12)
                throw new InvalidOperationException("재접속 후 다른 이벤스가 선택되어 중지했습니다. 원래 이벤스의 에피소드 목록에서 다시 시작하세요.");
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
