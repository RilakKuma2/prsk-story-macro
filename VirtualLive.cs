using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    static readonly TimeSpan VirtualWatchTime = TimeSpan.FromSeconds(15);
    enum VirtualWatchResult { Watched, Locked, CurrentEventLobby }
    static bool IsCurrentVirtualEvent(Bitmap screen) => IsMainEventList(screen) &&
        LightGlyph(screen,"catalogue-current-event.png",106,307,225);
    static bool IsVirtualLobby(Bitmap screen) => screen.Width==960 && screen.Height==540 &&
        LightGlyph(screen,"virtual-lobby-heading.png",503,98,210) &&
        LightGlyph(screen,"virtual-lobby-room.png",815,105,210);
    static bool IsVirtualLobbyGuide(Bitmap screen) => screen.Width==960 && screen.Height==540 &&
        Matches(screen,new("virtual-guide-pages","virtual-lobby-guide-pages.png",0,0,452,489,56,23)) &&
        (Matches(screen,new("virtual-guide-next","virtual-lobby-guide-next.png",0,0,702,490,37,21)) ||
         Matches(screen,new("virtual-guide-close","virtual-lobby-guide-close.png",0,0,691,490,60,21)));
    static string VirtualQueuePath => Path.Combine(Artifacts,$"instance-{Selected!.Index}-virtual-queue.json");
    static QueueProgress LoadVirtualProgress()
    {
        var saved=LoadRecord<QueueProgress>(VirtualQueuePath);
        return saved!=null && saved.Stage!="complete" && QueueBelongsTo(saved,Selected!) && saved.Completed!=null ? saved : NewQueueProgress(Selected!);
    }
    static Task SaveVirtualProgress(QueueProgress value) => SaveRecord(VirtualQueuePath,JsonSerializer.Serialize(value));

    // Compare the light glyph's shape using local contrast. Background RGB is never a template requirement.
    static bool LightGlyph(Bitmap screen,string image,int left,int top,int expectedThreshold,bool darkInk=false)
    {
        if(screen.Width!=960 || screen.Height!=540) return false;
        using var expected=new Bitmap(Path.Combine(Root,"templates",image));
        var points=new List<(bool Ink,double Value)>(); double inkSum=0,bgSum=0; int ink=0,bg=0;
        for(var y=0;y<expected.Height;y++) for(var x=0;x<expected.Width;x++)
        {
            var e=expected.GetPixel(x,y); var s=screen.GetPixel(left+x,top+y);
            var glyph=darkInk?(e.R+e.G+e.B)/3<expectedThreshold:(e.R+e.G+e.B)/3>expectedThreshold; var value=(s.R+s.G+s.B)/3d;
            points.Add((glyph,value)); if(glyph){ink++;inkSum+=value;}else{bg++;bgSum+=value;}
        }
        if(ink<20 || bg<20 || (darkInk?bgSum/bg-inkSum/ink:inkSum/ink-bgSum/bg)<15) return false;
        var cut=(inkSum/ink+bgSum/bg)/2; var missing=0; var extra=0;
        foreach(var p in points) { var observed=darkInk?p.Value<cut:p.Value>=cut;if(p.Ink && !observed)missing++;if(!p.Ink && observed)extra++; }
        return missing<ink*.25 && extra<bg*.30;
    }
    static bool IsVirtualLive(Bitmap s) => LightGlyph(s,"virtual-exit-glyph.png",25,23,120);
    // This small grey '参加済み' label is the only success criterion. Reward stamps can change freely.
    static bool IsVirtualWatched(Bitmap s) => LightGlyph(s,"virtual-watched.png",214,472,230);
    static bool IsVirtualLockNotice(Bitmap s) => Matches(s,new("virtual-lock-ok","virtual-locked-ok.png",0,0,457,307,45,22));
    static bool IsVirtualLocked(Bitmap s) => LightGlyph(s,"virtual-lock-glyph.png",118,463,220);
    static HomeAction? VirtualAction(Bitmap s)
    {
        if(IsVirtualLobbyGuide(s)) return new("virtual-lobby-guide-next-or-close",720,500);
        if(IsVirtualLobby(s)) return new("virtual-lobby-back",32,32);
        if(IsVirtualLockNotice(s)) return new("virtual-locked-close",480,318);
        if(HasRewardsObtainedText(s)) return new("virtual-reward-close",480,315);
        if(IsStoryDownload(s)) return new("story-download",571,405);
        if(Matches(s,new("virtual-intro-confirm","virtual-intro-confirm.png",0,0,509,308,123,20))) return new("virtual-intro-skip-confirm",570,318);
        if(Matches(s,new("virtual-intro-menu","virtual-intro-menu-skip.png",0,0,812,212,109,25))) return new("virtual-intro-skip",862,226);
        if(IsStoryReader(s)) return new("virtual-optional-intro-menu",928,32);
        if(IsVirtualLive(s)) return new("virtual-exit",42,41);
        if(IsEpisodeList(s)) return IsVirtualWatched(s) ? null : new("virtual-event-open",112,482);
        return null;
    }
    static async Task<VirtualWatchResult> WatchCurrentVirtualLive()
    {
        var lastKnown=DateTime.UtcNow; DateTime? entered=null; var lastTap=DateTime.MinValue;
        var downloadPending=false;
        var until=DateTime.UtcNow.AddMinutes(20);
        while(DateTime.UtcNow<until)
        {
            Stop.Token.ThrowIfCancellationRequested(); await EnsureGameForeground();
            using var screen=await Capture();
            if(IsVirtualLobbyGuide(screen))
            {
                if(DateTime.UtcNow-lastTap>TimeSpan.FromSeconds(1) &&
                    await GuardedTap(new("virtual-lobby-guide-next-or-close",720,500),VirtualAction))
                    lastTap=DateTime.UtcNow;
                lastKnown=DateTime.UtcNow;
                await Task.Delay(500,Stop.Token);continue;
            }
            if(IsVirtualLobby(screen))
            {
                CopyEvidence(ShotPath,Path.Combine(Artifacts,$"instance-{Selected!.Index}-virtual-current-event-lobby.webp"));
                if(await GuardedTap(new("virtual-lobby-back",32,32),VirtualAction))
                {
                    Log("진행 중 이벤트의 버라 선택창 · 뒤로가기 후 다음 버라로 이동");
                    return VirtualWatchResult.CurrentEventLobby;
                }
                continue;
            }
            if(IsVirtualLockNotice(screen))
            {
                await GuardedTap(new("virtual-locked-close",480,318),VirtualAction);
                await Task.Delay(500,Stop.Token);Log("버라 잠금 안내 · 건너뛰기");return VirtualWatchResult.Locked;
            }
            if(IsVirtualLocked(screen)) { Log("버라 자물쇠 표시 · 건너뛰기");return VirtualWatchResult.Locked; }
            // Close the variable-stamp reward banner before checking the small watched label behind it.
            if(HasRewardsObtainedText(screen))
            {
                await GuardedTap(new("virtual-reward-close",480,315),VirtualAction);
                lastKnown=DateTime.UtcNow; await Task.Delay(500,Stop.Token); continue;
            }
            if(IsVirtualWatched(screen)) { Log("버라 참가 완료 글자 확인 · 성공"); return VirtualWatchResult.Watched; }
            var action=VirtualAction(screen);
            if(entered!=null && action?.Name=="virtual-event-open")
            {
                // Returning to the list can precede the reward/参加済み overlay by a few frames.
                if(DateTime.UtcNow-lastKnown>TimeSpan.FromSeconds(15))
                    throw new RecoveryNeeded(RecoveryCause.UnknownState,"버라 퇴장 후 참가 완료 글자를 확인하지 못했습니다.");
                await Task.Delay(500,Stop.Token); continue;
            }
            if(action?.Name=="virtual-exit")
            {
                downloadPending=false;
                lastKnown=DateTime.UtcNow;
                if(entered==null) { entered=DateTime.UtcNow; Log("버라 입장 · 카운트다운 포함 15초 후 퇴장"); }
                if(DateTime.UtcNow-entered<VirtualWatchTime) { await Task.Delay(500,Stop.Token); continue; }
            }
            else if(IsStoryLoading(screen)) lastKnown=DateTime.UtcNow;
            if(action!=null)
            {
                lastKnown=DateTime.UtcNow;
                if(DateTime.UtcNow-lastTap>TimeSpan.FromSeconds(1))
                {
                    // A first-time control guide consumes taps anywhere: keep using only EXIT until it leaves.
                    if(await GuardedTap(action,VirtualAction)){lastTap=DateTime.UtcNow;if(action.Name=="story-download")downloadPending=true;}
                }
            }
            else if(await DismissNotice(screen)) lastKnown=DateTime.UtcNow;
            else if(!downloadPending && DateTime.UtcNow-lastKnown>TimeSpan.FromSeconds(45))
                throw new RecoveryNeeded(RecoveryCause.UnknownState,"버라 입장/퇴장 화면을 확인하지 못했습니다.");
            await Task.Delay(500,Stop.Token);
        }
        throw new InvalidOperationException("버라 다운로드/입장 대기 시간이 초과되었습니다.");
    }
    static async Task ReadVirtualQueue()
    {
        var progress=LoadVirtualProgress();
        var locked=new List<byte[]>();
        using(var start=await Capture())
        {
            if(IsMainEventList(start) || IsEmptyVirtualCatalogue(start) || IsFilterFrame(start)) await ApplyUnreadFilter(virtualOnly:true);
            else
            {
                var watched=await WatchCurrentVirtualLive();
                await ReturnToStoryCatalogue(virtualOnly:true);
                // Only a signature saved before entry can identify the event just completed.
                // The filter may already have selected a different card on returning.
                if(progress.Current is { } card)
                {
                    if(watched==VirtualWatchResult.Watched && !progress.Completed.Any(done=>SameCatalogueSignature(done,card))) progress=progress with { Completed=[..progress.Completed,card] };
                    if(watched!=VirtualWatchResult.Watched) locked.Add(card);
                    progress=progress with {Current=null,Stage="catalogue"};
                }
                await SaveVirtualProgress(progress);
                if(watched!=VirtualWatchResult.Watched && !await AdvanceAfterSkippedVirtual()){Log("시청 가능한 다음 버라 없음 · 처리 완료");return;}
                await ApplyUnreadFilter(virtualOnly:true);
            }
        }
        while(true)
        {
            await EnsureCatalogueFilter(virtualOnly:true);
            using var catalogue=await CaptureCatalogue(allowEmpty:true);
            if(IsEmptyVirtualCatalogue(catalogue))
            {
                await Task.Delay(700,Stop.Token); using var verify=await Capture();
                if(IsEmptyVirtualCatalogue(verify)){await SaveVirtualProgress(progress with {Stage="complete"});Log("미시청 버라 없음 · 전체 버라 완료");return;}
                continue;
            }
            var card=CatalogueSignature(catalogue);
            // The ongoing event opens the live-selection lobby instead of an archived replay.
            // Its 開催中 badge remains visible between manual runs, so skip before opening it.
            if(IsCurrentVirtualEvent(catalogue))
            {
                Log("開催中 표시 확인 · 진행 중 이벤트는 건너뛰고 한 칸 아래 버라 선택");
                progress=progress with {Current=null,Stage="catalogue"};await SaveVirtualProgress(progress);
                if(!await AdvanceAfterSkippedVirtual()){Log("시청 가능한 다음 버라 없음 · 처리 완료");return;}
                continue;
            }
            if(locked.Any(done=>SameCatalogueSignature(done,card)))
            {
                if(!await AdvanceAfterSkippedVirtual()){Log("남은 버라는 잠금 상태 · 처리 완료");return;}
                continue;
            }
            if(progress.Completed.Any(done=>SameCatalogueSignature(done,card)))
            {
                if(!await AdvanceCatalogue(virtualOnly:true)) { await SaveVirtualProgress(progress with { Stage="complete" }); Log("전체 버라 완료"); return; }
                continue;
            }
            progress=progress with { Current=card,Stage="watching" }; await SaveVirtualProgress(progress);
            await OpenSelectedCatalogueStory(virtualOnly:true);
            var watched=await WatchCurrentVirtualLive();
            if(watched!=VirtualWatchResult.Watched)
            {
                locked.Add(card);progress=progress with {Current=null,Stage="catalogue"};await SaveVirtualProgress(progress);
                await ReturnToStoryCatalogue(virtualOnly:true);
                if(!await AdvanceAfterSkippedVirtual()){Log("시청 가능한 다음 버라 없음 · 처리 완료");return;}
                continue;
            }
            // Never mark success from a timer or reward stamp; WatchCurrentVirtualLive returns only for 参加済み.
            progress=progress with { Completed=[..progress.Completed,card],Current=null,Stage="catalogue",Updated=DateTimeOffset.Now };
            await SaveVirtualProgress(progress); await ReturnToStoryCatalogue(virtualOnly:true);
        }
    }
    static async Task<bool> AdvanceAfterSkippedVirtual()
    {
        using var before=await CaptureCatalogue(allowEmpty:true);
        if(IsEmptyVirtualCatalogue(before) || !HasNextCatalogueCard(before)) return false;
        using var card=CatalogueCard(before);
        await Guard();
        if(!(await Foreground()).Contains(Package+"/")) throw new InvalidOperationException("전면 앱이 바뀌었습니다.");
        using(var verify=await Capture()) if(!IsMainEventList(verify)) throw new InvalidOperationException("스토리 선택 목록이 아닙니다.");
        // The catalogue snaps to the next event when dragged a little; never tap an episode to unlock a live.
        AuthorizedInput="input swipe 180 410 180 310 700";
        try{await Shell(AuthorizedInput);}finally{AuthorizedInput=null;}
        Log("버라 건너뜀 · 목록을 한 칸 내려 다음 버라 선택");
        for(var i=0;i<12;i++)
        {
            await Task.Delay(500,Stop.Token);using var next=await CaptureCatalogue(allowEmpty:true);
            if(IsEmptyVirtualCatalogue(next))return false;
            if(!SameCatalogueCard(next,card))return true;
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState,"다음 버라 선택이 반영되지 않았습니다.");
    }
}
