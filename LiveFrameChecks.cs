internal static partial class Program
{
    static async Task VerifyLiveFrames(Action<bool,string> assert)
    {
        var frames=new LiveFrameStore(2,10);
        var first=new byte[]{1,2,3,4};
        frames.Publish(0,first);
        var version=frames.Read(0)!.Revision;
        assert(version<9_007_199_254_740_991 && (long)(double)(version+1)==version+1,"frame revisions stay exact in JavaScript so the live image refreshes on every new frame");
        for(var i=0;i<1000;i++)frames.Publish(0,new byte[]{5,6,7,8});
        assert(frames.Count==1 && frames.Bytes==4 && frames.Read(0)!.Revision>version,"repeated previews replace one frame instead of accumulating memory");
        assert(first.SequenceEqual(new byte[]{1,2,3,4}),"replacing a frame does not mutate data held by an in-flight response");
        frames.Publish(2,new byte[4]);frames.Publish(3,new byte[4]);
        assert(frames.Count==2 && frames.Bytes==8 && frames.Read(0)==null,"the frame cache evicts oldest frames when its slot limit is reached");
        frames.Publish(4,new byte[8]);
        assert(frames.Count==1 && frames.Bytes==8 && frames.Read(4)!=null,"the byte cap also bounds memory independently of instance count");
        frames.Keep([0]);assert(frames.Count==0 && frames.Bytes==0,"removed instances release their cached image data");
        frames.Publish(0,new byte[3]);frames.Clear();assert(frames.Count==0 && frames.Bytes==0,"shutdown clears all frame references");
        var saved=Selected;
        try
        {
            Selected=new(999,"memory capture test",1,true,true,6553);
            var existed=File.Exists(ShotPath);var modified=existed?File.GetLastWriteTimeUtc(ShotPath):default;
            var png=File.ReadAllBytes(Path.Combine(Root,"tests","fixtures","story-reader-menu-open.png"));
            await Snapshot(capture:()=>Task.FromResult(png));
            assert(CurrentFrames.Read(999)?.Png==png && File.Exists(ShotPath)==existed && (!existed || File.GetLastWriteTimeUtc(ShotPath)==modified),
                "runtime capture publishes the original bytes in memory without writing a PNG file");
            assert(CurrentEvidenceBytes(ShotPath)==png,"evidence compression consumes the in-memory frame without rereading a screenshot file");
        }
        finally { Selected=saved;CurrentFrames.Clear(); }
    }
}
