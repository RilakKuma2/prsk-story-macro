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

        var observations=0;var scrolls=0;
        var resumed=await ScanEpisodePages(() =>
        {
            observations++;
            return Task.FromResult(new EpisodePage(false,true,true,false));
        },() => { scrolls++;return Task.FromResult(false); });
        assert(observations==1 && scrolls==0 && resumed.HasUnread && !resumed.BottomReached,
            "the loaded red-only episode opens immediately without scrolling");

        observations=0;scrolls=0;
        var blocked=await ScanEpisodePages(() => { observations++;return Task.FromResult(new EpisodePage(false,false,false,true)); },
            () => { scrolls++;return Task.FromResult(false); });
        assert(observations==2 && scrolls==2 && blocked is {BottomReached:true,HasLocked:true},
            "a locked-only story advances at the bottom without a second scan or being counted as fully read");

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
        try { RecordVerifiedStory(progress,new(false,false,false,1));assert(false,"incomplete verification must not mark completion"); }
        catch (InvalidOperationException) { assert(true,"an unfinished scan cannot mark the whole event completed"); }
        var complete=RecordVerifiedStory(progress,new(false,false,true,2));
        assert(complete.Stage=="verified" && complete.Completed.Count==1,"fully verified unlocked stories are recorded as completed");
    }
}
