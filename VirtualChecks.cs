using System.Drawing;
internal static partial class Program
{
    static void VerifyVirtualLive()
    {
        int checks=0;
        void Assert(bool condition,string label){if(!condition)throw new Exception("Virtual check failed: "+label);checks++;}
        Bitmap Load(string name)=>new(Path.Combine(Root,"tests","fixtures",name+".png"));
        using var dark=Load("virtual-live-dark"); using var bright=Load("virtual-live-guide");
        using var intro=Load("virtual-intro"); using var menu=Load("virtual-intro-menu");
        using var confirm=Load("virtual-intro-skip"); using var eventList=Load("virtual-event");
        using var reward=Load("virtual-reward"); using var catalogue=Load("virtual-catalogue");
        using var filter=Load("virtual-filter-only");
        using var locked=Load("virtual-lock-badge");using var lockNotice=Load("virtual-locked-dialog");
        Assert(IsVirtualLocked(locked) && !IsVirtualLocked(eventList),"lock glyph skips without reading a story");
        Assert(IsVirtualLockNotice(lockNotice) && VirtualAction(lockNotice)?.Name=="virtual-locked-close","variable event-name lock notice closes without restart");
        Assert(!IsVirtualWatched(locked),"locked virtual is never counted as watched");
        Assert(DesiredVirtualRadios(filter) && !DesiredRadios(filter) && FindFilterRadioActionFor(filter,true)==null,"virtual filter keeps reading status All and virtual Unwatched");
        Assert(IsVirtualLive(dark) && IsVirtualLive(bright),"exit glyph works on dark/bright translucent backgrounds");
        Assert(!IsVirtualLive(eventList) && !IsVirtualLive(intro),"episode back arrow and dialogue are not virtual exit");
        Assert(VirtualAction(intro)?.Name=="virtual-optional-intro-menu","optional intro uses ellipsis only");
        Assert(VirtualAction(menu)?.Name=="virtual-intro-skip" && VirtualAction(confirm)?.Name=="virtual-intro-skip-confirm","optional skip menu and confirmation");
        Assert(IsVirtualWatched(reward) && !IsVirtualWatched(eventList),"参加済み is the success mark");
        Assert(HasRewardsObtainedText(reward),"reward text independent of stamp");
        using(var otherStamp=new Bitmap(reward))
        {
            using(var graphics=Graphics.FromImage(otherStamp)) graphics.FillRectangle(Brushes.Black,400,210,160,90);
            Assert(HasRewardsObtainedText(otherStamp) && IsVirtualWatched(otherStamp),"changing stamp cannot affect success or reward close");
        }
        Assert(VirtualAction(eventList)?.Name=="virtual-event-open" && VirtualAction(reward)?.Name=="virtual-reward-close","event banner and reward actions");
        Assert(IsMainEventList(catalogue) && VirtualWatchTime==TimeSpan.FromSeconds(15),"15 seconds from entry includes countdown");
        Assert(IsPanelCommand("virtual-all"),"virtual button routes to separate queue");
        using(var empty=new Bitmap(catalogue))
        {
            using(var graphics=Graphics.FromImage(empty)) graphics.FillRectangle(Brushes.Black,768,480,192,60);
            Assert(IsEmptyVirtualCatalogue(empty) && !IsMainEventList(empty),"empty filtered catalogue completes without reopening or restarting");
        }
        Assert(ValidListenPort(8788) && !ValidListenPort(1023) && !ValidListenPort(65536),"Listen port range");
        using(var home=Load("home-account-guard-regression")) Assert(!IsAccountDeleteConfirm(home),"normal home cannot be mistaken for account deletion");
        foreach(var fixture in Directory.GetFiles(Path.Combine(Root,"tests","fixtures"),"*.png"))
        {
            if(Path.GetFileName(fixture)=="account-delete-confirm.png")continue;
            using var candidate=new Bitmap(fixture);Assert(!IsAccountDeleteConfirm(candidate),"account guard negative fixture "+Path.GetFileName(fixture));
        }
        Assert(!IsAccountDeleteConfirm(eventList) && !IsAccountDeleteConfirm(lockNotice),"normal event and lock modal do not trigger account protection");
        using(var syntheticPanel=new Bitmap(960,540))
        {
            using(var graphics=Graphics.FromImage(syntheticPanel))
            {
                graphics.Clear(Color.FromArgb(100,100,110));
                using var panelBrush=new SolidBrush(Color.FromArgb(233,233,238));
                graphics.FillRectangle(panelBrush,160,20,640,500);
                using var warningBrush=new SolidBrush(Color.FromArgb(255,90,130));
                foreach(var y in new[]{296,317,338})graphics.FillRectangle(warningBrush,310,y,340,7);
            }
            Assert(IsAccountDeleteConfirm(syntheticPanel),"synthetic inset confirmation panel with three warning rows is protected");
        }
        using(var voiceWithoutOption=Load("story-voice-without-continuous"))
            Assert(IsStoryVoice(voiceWithoutOption) && !StoryContinuousOptionVisible(voiceWithoutOption) && FindReaderAction(voiceWithoutOption)?.Name=="story-no-voice","absent continuous option proceeds directly without touching blank space");
        using(var normalVoice=Load("story-voice-dialog"))
            Assert(StoryContinuousOptionVisible(normalVoice) && FindReaderAction(normalVoice)?.Name=="story-continuous-on","present unchecked continuous option is still enabled");
        var safetyRestarts=0;var safetyResumed=false;
        RunRecoveryAttempts(()=>Task.FromException(new RecoveryNeeded(RecoveryCause.AccountSafety,"test")),_=>Task.CompletedTask,
            _=>{safetyRestarts++;return Task.FromResult(true);},reset=>{reset();safetyResumed=true;return Task.CompletedTask;},()=>false).GetAwaiter().GetResult();
        Assert(safetyRestarts==1 && safetyResumed,"account protection restarts even with automatic resume disabled");
        using var gacha=Load("gacha-protected");using var card=Load("card-protected");
        Assert(IsGachaProtected(gacha) && ProtectedScreenReason(gacha)!=null,"gacha crystal and paid label at the specified top-right position");
        Assert(IsCardProtected(card) && ProtectedScreenReason(card)!=null,"member card performance and master rank labels");
        using(var changedBalance=new Bitmap(gacha))
        {
            using(var graphics=Graphics.FromImage(changedBalance))graphics.FillRectangle(Brushes.Black,800,24,42,18);
            Assert(IsGachaProtected(changedBalance),"gacha protection ignores the crystal balance");
        }
        Assert(ProtectedScreenReason(eventList)==null && ProtectedScreenReason(bright)==null && ProtectedScreenReason(catalogue)==null,"story and virtual screens remain allowed");
        using var titleMenu=Load("title-menu-protected");
        Assert(IsTitleMenuProtected(titleMenu) && ProtectedScreenReason(titleMenu)!=null,"title account-management menu is protected");
        Log($"버라 검사 {checks}개 통과");
    }
}
