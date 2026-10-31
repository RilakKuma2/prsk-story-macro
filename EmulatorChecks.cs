using System.Drawing;
internal static partial class Program
{
    static void VerifyEmulatorChanges()
    {
        VerifyVirtualLive();
        var checks=0;
        void Assert(bool condition,string name) { if(!condition) throw new Exception("Emulator check failed: "+name); checks++; }
        void Reject(Action action,string name) { try { action(); } catch(InvalidOperationException) { checks++; return; } throw new Exception("Unsafe command accepted: "+name); }
        string Device(string name,int x,int y,bool tracking,bool pressure,int number=4) =>
            $"add device 1: /dev/input/event{number}\n  name: \"{name}\"\n ABS_MT_POSITION_X : value 0, min 0, max {x}\n ABS_MT_POSITION_Y : value 0, min 0, max {y}\n"+
            (tracking?" ABS_MT_TRACKING_ID : value 0, min 0, max 65535\n":"")+(pressure?" ABS_MT_PRESSURE : value 0, min 0, max 255\n":"");
        var ld=SelectStoryTouch("ldplayer",false,"x86_64",Device("Touch",959,539,true,true,2));
        var mu=SelectStoryTouch("mumu",false,"arm64-v8a",Device("Touchscreen",540,960,true,false));
        var bs=SelectStoryTouch("bluestacks",false,"x86_64",Device("BlueStacks Virtual Touch",32767,32767,true,false));
        var air=SelectStoryTouch("bluestacks",true,"arm64-v8a",Device("BlueStacks Virtual Touch",32767,32767,true,false));
        var me=SelectStoryTouch("memu",false,"x86",Device("Touch",959,539,true,true));
        var nox=SelectStoryTouch("nox",false,"x86",Device("Touch",959,539,false,true));
        Assert(!ld.Files && ld.Pressure && ld.Wide,"LD printf with pressure and 64-bit packets");
        Assert(mu.Files && mu.MaxX==540 && mu.MaxY==960 && mu.Tracking,"MuMu rotated device");
        Assert(bs.MaxX==32767 && air.MaxX==32767 && air.Files && !air.Pressure,"BlueStacks 5 and Air touch ranges");
        Assert(!me.Wide && !nox.Wide && !nox.Tracking,"MEmu/Nox 32-bit and Nox type A");
        Reject(()=>SelectStoryTouch("bluestacks",true,"arm64-v8a",Device("Mouse",32767,32767,true,false)),"Air mouse");
        Reject(()=>SelectStoryTouch("nox",false,"x86",Device("Mouse",959,539,true,false)),"other mouse");
        static List<(short Type,short Code,int Value)> Decode(byte[] bytes,bool wide)
        {
            var result=new List<(short,short,int)>(); using var reader=new BinaryReader(new MemoryStream(bytes));
            while(reader.BaseStream.Position<bytes.Length) { reader.ReadBytes(wide?16:8); result.Add((reader.ReadInt16(),reader.ReadInt16(),reader.ReadInt32())); }
            return result;
        }
        var noxPackets=StoryTouchPackets(nox); var down=Decode(noxPackets.Down,false); var up=Decode(noxPackets.Up,false);
        Assert(down.Contains((0,2,0)) && up.Contains((0,2,0)) && !down.Any(e=>e.Code==57),"Nox SYN_MT_REPORT, no tracking slots");
        Assert(down.Contains((3,58,1)) && up.Contains((1,330,0)),"Nox pressure and release");
        var airDown=Decode(StoryTouchPackets(air).Down,true);
        Assert(airDown.Contains((3,53,16384)) && airDown.Contains((3,54,16384)) && !airDown.Any(e=>e.Code==58),"Air center and no pressure");
        Assert(Decode(StoryTouchPackets(ld).Up,true).Contains((3,58,0)),"LD pressure release");
        var off=new Vm(0,"off",1,false,false,0); var first=new Vm(1000,"one",2,true,true,16384,"127.0.0.1:16384"); var second=new Vm(3000,"two",3,true,true,21503);
        Assert(ChooseInstance([off,first],off.Index)==first.Index,"manual VM sole running instance wins");
        Assert(ChooseInstance([first,second],-1)==-1,"multiple running instances require selection");
        Assert(ChooseInstance([first,second],second.Index)==second.Index,"chosen running VM retained");
        Assert(ChooseInstance([],0)==-1 && ChooseInstance([off],-1)==0,"empty and single offline selection");
        if (OperatingSystem.IsMacOS())
        {
            foreach (var (total, target, maximum) in new[] { (8,3,4), (16,6,8), (24,8,12), (32,8,12), (64,8,12) })
            {
                var host = new HostMemory(total, .5);
                Assert(TargetMemoryGb(4,true,host,0)==target && NewVmMemoryGb(host)==target,$"Mac {total}GB defaults use total RAM despite low available RAM");
                Assert(MaximumMemoryGb(4,true,host)==maximum && MaximumMemoryGb(0,false,host)==maximum,$"Mac {total}GB cap is independent of VM running state");
                Assert(TargetMemoryGb(4,true,host,12)==maximum,$"Mac {total}GB clamps saved choices to its cap");
                Assert(CanSetMemory(4,target,false,host) && !CanSetMemory(4,maximum+1,true,host),$"Mac {total}GB applies its tier and rejects excess RAM");
            }
            Assert(TargetMemoryGb(4,true,new(16,.5),5)==5,"Mac preserves a valid explicit RAM choice");
        }
        else Assert(NewVmMemoryGb(new(32,20))==8 && NewVmMemoryGb(new(32,12))==5 && NewVmMemoryGb(new(32,8))==3,"new VM auto RAM stays within 3 to 8GB");
        Assert(OriginalEmulators.NewVmDiskBytes==(12L<<30) && !OriginalEmulators.HasCreationSpace((14L<<30)-1) && OriginalEmulators.HasCreationSpace(14L<<30),"12GB creation estimate plus original 2GB margin");
        Assert(IsEmulatorDownload(MuMuDownloadUrl) && !IsEmulatorDownload("file:///C:/Windows/System32/cmd.exe"),"official download link only");
        foreach(var command in new[]{"read-current","read-one","restart-read"}) Assert(IsPanelCommand(command),"start "+command);
        foreach(var command in new[]{"home","story","reset","delete-account","from-start"}) Assert(!IsPanelCommand(command),"reject "+command);
        foreach(var (exe,verb) in new[]{("ldconsole.exe","remove"),("mumumanager.exe","delete"),("mumumanager.exe","clone"),("memuc.exe","remove"),("noxconsole.exe","remove")})
            Reject(()=>OriginalEmulators.ValidateNativeCommand(exe,[verb]),exe+" "+verb);
        Reject(()=>OriginalEmulators.ValidateNativeCommand("adb",["-s","emulator-5554","shell","pm clear "+Package]),"account data clear");
        Reject(()=>OriginalEmulators.ValidateNativeCommand("adb",["-s","emulator-5554","shell","am force-stop "+Package]),"provider cannot bypass game restart gate");
        using var catalogue=new Bitmap(Path.Combine(Root,"tests","fixtures","catalogue-after-completion.png"));
        using var episodes=new Bitmap(Path.Combine(Root,"tests","fixtures","top-story-episodes.png"));
        Assert(IsMainEventList(catalogue) && !IsEpisodeList(catalogue),"all-events example");
        Assert(IsEpisodeList(episodes) && EpisodeBadgeRows(episodes,true).Count>0,"single-event example with unread SKIP");
        using var worldLink=new Bitmap(Path.Combine(Root,"tests","fixtures","world-link-opening.png"));
        Assert(IsEpisodeList(worldLink),"World Link opening recognized without episode 1 or fixed icon x");
        Assert(EpisodeBadgeRows(worldLink,false).Count==1 && FindEpisodeToRead(worldLink,true)!=null,"World Link unread opening can be started");
        var saved=Selected; var auto=AutoResumeEnabled;
        try
        {
            Selected=first; AutoResumeEnabled=false; ManualRestartAuthorized=true;
            ValidateProcessArgs(["-s",Serial,"shell","am force-stop "+Package],false);
            Assert(!ManualRestartAuthorized,"explicit reboot works with auto resume disabled and consumes permission");
            Reject(()=>ValidateProcessArgs(["-s",Serial,"shell","am force-stop "+Package],false),"manual restart cannot repeat without authorization");
        }
        finally { Selected=saved; AutoResumeEnabled=auto; ManualRestartAuthorized=false; }
        using (var memories = new Bitmap(Path.Combine(Root,"tests","fixtures","single-event-memories-partly-locked.png")))
        {
            Assert(IsEpisodeList(memories),"single event accepts the current Memories list with a locked next episode");
            Assert(EpisodeBadgeRows(memories,true).Count==1 && EpisodeBadgeRows(memories,false).Count==0,"current Memories list has one readable SKIP episode");
            Assert(FindEpisodeToRead(memories,true) is { X:640,Y:>=190 and <=230 },"single event selects the SKIP first episode instead of the locked second episode");
        }
        VerifyUnreadResume(Assert).GetAwaiter().GetResult();
        VerifySingleEventRecovery(Assert).GetAwaiter().GetResult();
        VerifyFilterRecovery(Assert).GetAwaiter().GetResult();
        VerifyStoryMenuClose(Assert);
        VerifyUpdateInfo(Assert);
        Log($"VM/시작 모드 검사 {checks}개 통과");
    }
}
