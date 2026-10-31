using System.Drawing;

internal static partial class Program
{
    static async Task VerifyUnreadResume(Action<bool,string> assert)
    {
        using var redOnly = new Bitmap(Path.Combine(Root,"tests","fixtures","latest-episode-unread-only.png"));
        using var mixed = new Bitmap(Path.Combine(Root,"tests","fixtures","episode-lock-with-skip.png"));
        using var lockedOnly = new Bitmap(Path.Combine(Root,"tests","fixtures","episode-only-locked-remains.png"));
        assert(EpisodeBadgeRows(redOnly,true).Count==0 && EpisodeBadgeRows(redOnly,false).Count==1 &&
            FindEpisodeToRead(redOnly,true) is {Y:>=100 and <150},"red exclamation alone selects the unlocked latest episode without SKIP");
        assert(EpisodeLockedRows(redOnly).Count==0,"unlocked red-badge row is not classified as locked");
        assert(EpisodeLockedRows(mixed).Count==1 && FindEpisodeToRead(mixed,true) is {Y:>=230 and <270},
            "locked second episode is excluded while the lower SKIP first episode remains readable");
        assert(EpisodeLockedRows(lockedOnly).Count==1 && FindEpisodeToRead(lockedOnly,true)==null,
            "already-read first episode plus locked second episode has no eligible read target");

        var atTop=false;var rewinds=0;var opened=0;
        var resumed=await ScanEpisodePages(() =>
        {
            if (!atTop) return Task.FromResult(new EpisodePage(false,false,false,false));
            opened++;
            return Task.FromResult(new EpisodePage(false,true,true,false));
        },() => Task.FromResult(false),() => { atTop=true;rewinds++;return Task.CompletedTask; });
        assert(rewinds==1 && opened==1 && resumed.HasUnread && !resumed.BottomReached,
            "recovery at an empty lower page rewinds and opens the upper red-only last episode before completing");
        assert(resumed.TopChecked,"resumed red-only scan records that it checked the top");

        rewinds=0;var observations=0;
        var blocked=await ScanEpisodePages(() => { observations++;return Task.FromResult(new EpisodePage(false,false,false,true)); },
            () => Task.FromResult(false),() => { rewinds++;return Task.CompletedTask; });
        assert(rewinds==1 && observations==4 && blocked is {TopChecked:true,BottomReached:true,HasLocked:true},
            "a locked-only story finishes scanning from the top and can advance without being counted as fully read");

        using var catalogue=new Bitmap(Path.Combine(Root,"tests","fixtures","catalogue-after-completion.png"));
        var key=CatalogueSignature(catalogue);
        var progress=new QueueProgress(0,1,5555,"test",[key],key,"reading",DateTimeOffset.Now);
        var pass=new CatalogueReadPass();
        assert(!pass.AlreadyChecked(key),"a restored historical completed record cannot skip a newly unlocked event");
        var saved=RecordVerifiedStory(progress,blocked);
        assert(saved.Completed.Count==0 && saved.Stage=="locked" && saved.Current==null,
            "a locked event removes stale completion and remains eligible after it unlocks");
        pass.Confirm(key,blocked);
        assert(pass.AlreadyChecked(key) && !new CatalogueReadPass().AlreadyChecked(key),
            "locked events advance only within this scan and are rechecked after a restart");
        try { pass.Confirm(key,resumed);assert(false,"an unread event must not become skippable"); }
        catch (InvalidOperationException) { assert(true,"red-only unread evidence blocks catalogue advancement"); }
        try { RecordVerifiedStory(progress,new(false,false,true,1));assert(false,"incomplete verification must not mark completion"); }
        catch (InvalidOperationException) { assert(true,"a lower-page-only scan cannot mark the whole event completed"); }
        var complete=RecordVerifiedStory(progress,new(false,false,true,4,true,false));
        assert(complete.Stage=="verified" && complete.Completed.Count==1,"fully verified unlocked stories are recorded as completed");
    }
}
