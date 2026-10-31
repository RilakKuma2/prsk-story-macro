// Reused original provider dispatch, discovery and settings.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

internal static partial class OriginalEmulators
{
    record EmulatorKind(string Key,int Base);
    const int EmulatorBlock=1000;
    static readonly EmulatorKind[] Emulators=OperatingSystem.IsMacOS()?[new("bluestacks",0)]
        :[new("ldplayer",0),new("mumu",1000),new("bluestacks",2000),new("memu",3000),new("nox",4000)];
    // BlueStacks Air on macOS; BlueStacks 5 on Windows is one of the emulators above.
    static bool IsBlueStacks=>OperatingSystem.IsMacOS();
    static EmulatorKind EmulatorOf(int instance)=>Emulators.FirstOrDefault(e=>instance>=e.Base && instance<e.Base+EmulatorBlock)
        ??throw new ArgumentException(T($"{instance}번은 어느 에뮬레이터의 VM도 아니에요.",$"{instance}番はどのエミュレーターのVMでもありません。",$"#{instance} is not a VM of any emulator."));
    static string KindOf(int instance)=>EmulatorOf(instance).Key;
    static int LocalIndex(int instance)=>instance-EmulatorOf(instance).Base;

    static string EmulatorLabel(string key)=>key switch
    {
        "ldplayer"=>T("LD플레이어","LDPlayer","LDPlayer"),
        "mumu"=>T("뮤뮤 플레이어","MuMuプレイヤー","MuMu Player"),
        "bluestacks"=>IsBlueStacks?"BlueStacks Air":T("블루스택","BlueStacks","BlueStacks"),
        "memu"=>T("미뮤","MEmu","MEmu"),
        "nox"=>T("녹스","NoxPlayer","NoxPlayer"),
        _=>key
    };
    // Shown in the UI and checked before VM setup; null when the emulator is not installed.
    static string? FolderOf(string key)=>key switch
    {
        "ldplayer"=>LdFolder,"mumu"=>MuMuFolder,"bluestacks"=>BlueStacksApp,"memu"=>MEmuFolder,"nox"=>NoxFolder,_=>null
    };
    static IEnumerable<EmulatorKind> InstalledEmulators=>Emulators.Where(e=>FolderOf(e.Key)!=null);
    static string? EmulatorFolder=>InstalledEmulators.Select(e=>FolderOf(e.Key)).FirstOrDefault();
    static string RequireFolder(string key)=>FolderOf(key)??throw new InvalidOperationException(key=="ldplayer"?LdMissing
        :T($"{EmulatorLabel(key)} 설치 폴더를 찾지 못했어요. 설치한 뒤 다시 시도하세요.",$"{EmulatorLabel(key)}のインストール先が見つかりません。インストールしてからやり直してください。",$"{EmulatorLabel(key)} was not found. Install it and try again."));
    static string AdbOf(int instance)=>KindOf(instance) switch
    {
        "ldplayer"=>Path.Combine(LdRequired,"adb.exe"),
        "mumu"=>MuMuAdb,
        "bluestacks"=>BlueStacksAdb,
        "memu"=>Path.Combine(RequireFolder("memu"),"adb.exe"),
        "nox"=>NoxAdb,
        _=>throw new ArgumentException(instance.ToString())
    };
    // BlueStacks 5 and NoxPlayer ship adb 1.0.36, whose server stops a newer adb's (and the other way round). When an
    // emulator with a newer adb (LDPlayer, MuMu, MEmu) is installed too, that adb serves them as well, so VMs of
    // different emulators can run together; their own old adb then only cuts in when the emulator itself uses it.
    static string? NewerAdb=>new[]{LdFolder is { } ld?Path.Combine(ld,"adb.exe"):null,MuMuToolDir(MuMuFolder) is { } mu?Path.Combine(mu,"adb.exe"):null,
        MEmuFolder is { } me?Path.Combine(me,"adb.exe"):null}.FirstOrDefault(File.Exists);

    // Network serials are read with the VM list (a running VM's port is known there) and kept for later calls.
    static readonly ConcurrentDictionary<int,string> KnownSerials=new();
    static string SerialOf(int instance)
    {
        var local=LocalIndex(instance);
        return KindOf(instance) switch
        {
            "ldplayer"=>"emulator-"+(5554+local*2),
            "bluestacks"=>BlueStacksSerial(local),
            "memu"=>MEmuSerial(local),
            "nox"=>NoxSerial(local),
            _=>KnownSerials.TryGetValue(instance,out var serial)?serial:MuMuSerial(local)
        };
    }

    // Rows of every installed emulator, renumbered into the emulator's block.
    static string ListOf(string key)=>key switch
    {
        "ldplayer"=>LdList(),"mumu"=>MuMuList(),"bluestacks"=>BlueStacksList(),"memu"=>MEmuList(),"nox"=>NoxList(),_=>""
    };
    // Network serials connect on their first adb call (RunBytesWithin), so listing touches no adb: an adb of another
    // emulator would restart the adb server under the VMs that are running.
    static string VmListNow()=>VmListOf(InstalledEmulators);
    static string VmListOf(IEnumerable<EmulatorKind> emulators)
    {
        var parts=emulators.AsParallel().AsOrdered().Select(e=>
        {
            string text;
            try { text=ListOf(e.Key); }
            catch(Exception ex) { Console.Error.WriteLine($"{e.Key} list failed: {ex.Message}"); return []; }
            var rows=new List<string>();
            foreach(var line in text.Split('\n'))
            {
                var row=line.Trim().Split(',');
                if(row.Length<7 || !int.TryParse(row[0],out var local) || local is <0 or >=EmulatorBlock) continue;
                row[0]=(e.Base+local).ToString();
                rows.Add(string.Join(',',row));
            }
            return rows;
        }).ToList();
        return string.Join("\n",parts.SelectMany(p=>p));
    }
    static async Task<string> VmList()=>await Task.Run(VmListNow);
    // One VM's row reads only its own emulator's list.
    static async Task<string[]?> VmRow(int instance)=>(await Task.Run(()=>VmListOf([EmulatorOf(instance)]))).Split('\n').Select(x=>x.Trim().Split(',')).SingleOrDefault(x=>x.Length>=7 && x[0]==instance.ToString());

    static async Task VmLaunch(int instance)
    {
        var local=LocalIndex(instance);
        switch(KindOf(instance))
        {
            case "ldplayer": await Run(Ld,"launch","--index",local.ToString()); break;
            case "mumu": await MuMuLaunch(local); break;
            case "bluestacks": await BlueStacksLaunch(local); break;
            case "memu": await MEmuLaunch(local); break;
            case "nox": await NoxLaunch(local); break;
        }
    }
    // Asks the VM to close and returns; QuitVm also waits until it is off.
    static async Task VmQuitRequest(int instance)
    {
        var local=LocalIndex(instance);
        switch(KindOf(instance))
        {
            case "ldplayer": await Run(Ld,"quit","--index",local.ToString()); break;
            case "mumu": await MuMuQuit(local); break;
            case "bluestacks": await BlueStacksQuit(local); break;
            case "memu": await MEmuQuit(local); break;
            case "nox": await NoxQuit(local); break;
        }
    }
    // Network VMs are reached over adb connect, which has to be repeated after each boot.
    static async Task ConnectVm(int instance)
    {
        if(KindOf(instance)=="ldplayer") return;
        await Task.Run(()=>ConnectSerials(AdbOf(instance),[SerialOf(instance)]));
    }
    static Dictionary<string,string> AdbDevices(string adb)=>RunText(adb,"devices").Split('\n').Skip(1)
        .Select(l=>l.Trim().Split('\t')).Where(p=>p.Length==2).GroupBy(p=>p[0]).ToDictionary(g=>g.Key,g=>g.First()[1]);
    static void ConnectSerials(string adb,IEnumerable<string> serials)
    {
        Dictionary<string,string>? devices=null;
        foreach(var serial in serials.Where(s=>s.StartsWith("127.0.0.1:") && !s.EndsWith(":0")))
        {
            devices??=AdbDevices(adb);
            var state=devices.GetValueOrDefault(serial);
            if(state=="device") continue;
            // A restarted VM leaves an offline entry that adb does not reconnect by itself.
            if(state!=null) RunText(adb,"disconnect",serial);
            RunText(adb,"connect",serial);
        }
    }

    // Settings the macro needs on every VM: 960x540 at 160 DPI with adb reachable. A VM that is off is changed directly;
    // one that is on is only checked, and EnsureResolution restarts it when it differs.
    // Every emulator also gets the same light profile while its VM is off: 2 CPUs, 3 GB, 30 FPS, no vertical sync and
    // (where it is set per VM) no sound. The game runs the same at 30 FPS and the VMs take far less of the PC.
    const int LightFps=30;
    static readonly AsyncLocal<int> ProfileMemory=new(), ProfileCpus=new();
    static int LightMemoryMb=>ProfileMemory.Value>0?ProfileMemory.Value:3072;
    static int LightCpus=>ProfileCpus.Value>0?ProfileCpus.Value:2;
    static async Task<string> PrepareVm(int instance,bool running)
    {
        var local=LocalIndex(instance);
        return KindOf(instance) switch
        {
            "ldplayer"=>await PrepareLd(local,running),
            "mumu"=>await PrepareMuMu(local,running),
            "bluestacks"=>PrepareBlueStacks(local),
            "memu"=>await PrepareMEmu(local,running),
            "nox"=>await PrepareNox(local,running),
            _=>throw new ArgumentException(instance.ToString())
        };
    }
    // BlueStacks keeps every instance's settings in one file that a running BlueStacks rewrites, so a wrong VM is
    // reported instead of being restarted with fixed settings.
    static bool FixesSettings(int instance)=>KindOf(instance)!="bluestacks";
    // MuMu reports its display in portrait (540x960, rotated to landscape); the others report 960x540.
    static bool ResolutionMatches(string wmOutput)=>(wmOutput.Contains("Physical size: 960x540") || wmOutput.Contains("Physical size: 540x960"))
        && wmOutput.Contains("Physical density: 160") && !wmOutput.Contains("Override");

    static async Task<int> AddVm(string key,string name)=>key switch
    {
        "ldplayer"=>await AddLd(name),
        "mumu"=>await AddMuMu(name),
        "memu"=>await AddMEmu(name),
        "nox"=>await AddNox(name),
        _=>throw new InvalidOperationException("블루스택 인스턴스는 멀티 인스턴스 매니저에서 생성하세요.")
    };
    // Folder holding one VM's disks, for copy sizes; null when unknown.
    static string? VmDir(int instance)
    {
        var local=LocalIndex(instance);
        return KindOf(instance) switch
        {
            "ldplayer"=>Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(LdConfig(0))!)!,$"leidian{local}"),
            "bluestacks"=>BlueStacksVmDir(local),
            "mumu"=>MuMuVmDir(local),
            "memu"=>MEmuVmDir(local),
            "nox"=>NoxVmDir(local),
            _=>null
        };
    }

    // ---- Shared helpers

    // Install folders are looked up again every 30 seconds while missing, so installing an emulator needs no restart.
    static readonly ConcurrentDictionary<string,(DateTime At,string? Folder)> FolderCache=new();
    static string? CachedFolder(string key,Func<string?> find)
    {
        var cached=FolderCache.GetValueOrDefault(key);
        if(cached.Folder!=null && Directory.Exists(cached.Folder)) return cached.Folder;
        if(DateTime.UtcNow-cached.At<TimeSpan.FromSeconds(30)) return null;
        string? found=null;
        try { found=find(); } catch { }
        FolderCache[key]=(DateTime.UtcNow,found);
        return found;
    }
    // Folders from the installer's uninstall entry: its install location, or where its icon or uninstaller sits.
    static IEnumerable<string?> UninstallFolders(params string[] names)
    {
        foreach(var root in new[]{@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\"})
            foreach(var name in names)
            {
                yield return Reg(root+name,"InstallLocation");
                foreach(var value in new[]{"DisplayIcon","UninstallString"})
                    if(Reg(root+name,value) is { Length: >0 } path) yield return Path.GetDirectoryName(path.Trim().TrimStart('"').Split('"')[0].Split(',')[0]);
            }
    }
    static IEnumerable<string?> ProcessFolders(params string[] names)
    {
        foreach(var name in names)
            foreach(var process in Process.GetProcessesByName(name))
            {
                string? folder=null;
                try { folder=Path.GetDirectoryName(process.MainModule?.FileName); } catch { }
                process.Dispose();
                yield return folder;
            }
    }
    static IEnumerable<string> DriveFolders(params string[] subs)=>DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed && d.IsReady)
        .SelectMany(d=>subs.Select(s=>Path.Combine(d.RootDirectory.FullName,s)));

    // Console tools of Chinese-made emulators print in the system's ANSI code page (CP949 on Korean Windows).
    static Encoding AnsiEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
            catch { return Encoding.UTF8; }
        }
    }
    static string RunText(string file,params string[] args)=>RunTextIn(Encoding.UTF8,file,args);
    static string RunTextIn(Encoding encoding,string file,params string[] args)
    {
        var info=new ProcessStartInfo(file) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=encoding };
        foreach(var arg in args) info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;
        var output=process.StandardOutput.ReadToEndAsync(); process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException($"{Path.GetFileName(file)} did not finish."); }
        return output.Result;
    }
}
