using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Small adapter around the original invitation macro's emulator implementations.
// Only discovery, creation, configuration and selected-VM start/stop are exposed.
internal static partial class OriginalEmulators
{
    static string Workspace => Program.DataRoot;
    static string T(string ko, string ja, string en) => Program.PickLanguage(ko,ja,en);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static Func<CancellationToken> Cancellation = () => CancellationToken.None;
    internal sealed record Instance(int Index, string Name, bool Running, string Serial, long Created);
    internal sealed record Memory(double Gb, int Cpus);
    internal static string Kind(int index) => KindOf(index);
    internal static int Local(int index) => LocalIndex(index);
    internal static string Label(int index) => EmulatorLabel(KindOf(index));
    internal static string AdbPath(int index) => AdbOf(index);
    internal static string Serial(int index) => SerialOf(index);
    internal static string MuMuManagerPath => MuMuManager;
    internal sealed record KindInfo(string Key,string Name,bool Installed,bool CanCreate);
    internal static KindInfo[] Kinds() => Emulators.OrderBy(e=>e.Key=="mumu"?0:1).Select(e=>new KindInfo(e.Key,EmulatorLabel(e.Key),FolderOf(e.Key)!=null,e.Key!="bluestacks")).ToArray();
    internal static object[] Installed() => InstalledEmulators.Select(e => (object)new { key=e.Key, name=EmulatorLabel(e.Key), canCreate=e.Key!="bluestacks" }).ToArray();
    internal static async Task<List<Instance>> List(int? only = null)
    {
        var csv = await Task.Run(() => only is int index ? VmListOf([EmulatorOf(index)]) : VmListNow());
        return csv.Split('\n').Select(line => line.Split(',')).Where(row => row.Length >= 7).Select(row =>
        {
            var index = int.Parse(row[0]);
            long created = 0;
            try { if (VmDir(index) is { } dir && Directory.Exists(dir)) created = Directory.GetCreationTimeUtc(dir).Ticks; } catch { }
            return new Instance(index, row[1], row[4]=="1", SerialOf(index), created);
        }).OrderBy(vm => vm.Index).ToList();
    }
    internal static Task Launch(int index) => VmLaunch(index);
    internal static Task Connect(int index) => ConnectVm(index);
    internal static async Task<byte[]> Preview(int index)
    {
        await ConnectVm(index);
        return await RunBytesWithin(TimeSpan.FromSeconds(12),AdbOf(index),["-s",SerialOf(index),"exec-out","screencap","-p"]);
    }
    internal static async Task Shutdown(int index)
    {
        await VmQuitRequest(index);
        var until = DateTime.UtcNow.AddMinutes(2);
        while ((await VmRow(index)) is { } row && row[4]=="1")
        {
            Cancellation().ThrowIfCancellationRequested();
            if (DateTime.UtcNow > until) throw new TimeoutException("선택한 VM 종료 대기 시간이 초과되었습니다.");
            await Task.Delay(1000, Cancellation());
        }
    }
    internal static bool CanFixResolution(int index) => FixesSettings(index);
    internal static bool ResolutionOk(string output) => ResolutionMatches(output);
    internal static async Task Prepare(int index, bool running, int memoryMb = 0, int cpus = 0)
    {
        if (memoryMb == 0 || cpus == 0)
        {
            var memory = await ReadMemory(index);
            memoryMb = (int)(memory.Gb * 1024); cpus = memory.Cpus;
        }
        ProfileMemory.Value=memoryMb; ProfileCpus.Value=cpus;
        try { await PrepareVm(index, running); }
        finally { ProfileMemory.Value=0; ProfileCpus.Value=0; }
    }
    internal static bool MemoryCanChange(int index) => KindOf(index)!="bluestacks" ||
        BlueStacksProcesses().Keys.All(id => id==BlueStacksInstanceOf(LocalIndex(index)).Id);
    internal static async Task<Memory> ReadMemory(int index)
    {
        var local=LocalIndex(index);
        switch (KindOf(index))
        {
            case "mumu":
                var keys = new[] { "low", "middle", "high", "custom" }.SelectMany(mode => new[] { "-k", "performance_mem."+mode, "-k", "performance_cpu."+mode });
                var value = await MuMu(TimeSpan.FromSeconds(30), ["setting","-v",local.ToString(),"-k","performance_mode",..keys]);
                RequireMuMuOk(value);
                var mode = value.GetProperty("performance_mode").GetString();
                return new(double.Parse(value.GetProperty("performance_mem."+mode).GetString()!, CultureInfo.InvariantCulture),
                    int.Parse(value.GetProperty("performance_cpu."+mode).GetString()!, CultureInfo.InvariantCulture));
            case "ldplayer":
                var config = JsonNode.Parse(await File.ReadAllTextAsync(LdConfig(local)))!;
                return new(config["advancedSettings.memorySize"]!.GetValue<int>()/1024d, config["advancedSettings.cpuCount"]!.GetValue<int>());
            case "memu":
                int Get(string key) => int.Parse(Regex.Match(MEmuText("getconfigex","-i",local.ToString(),key), @"Value:\s*(\d+)").Groups[1].Value);
                return new(Get("memory")/1024d, Get("cpus"));
            case "nox":
                var vm=NoxVmOf(local);
                var ini=await File.ReadAllTextAsync(Path.Combine(NoxConfigDir,vm.Name=="nox"?"conf.ini":$"clone_{vm.Name}_conf.ini"));
                int Ini(string key) => int.Parse(Regex.Match(ini,$@"(?m)^{key}=(\d+)\s*$").Groups[1].Value);
                return new(Ini("mem")/1024d, Ini("cpu"));
            default:
                var prefix=$"bst.instance.{BlueStacksInstanceOf(local).Id}.";
                var values=ReadBlueStacksConf();
                return new(int.Parse(values[prefix+"ram"])/1024d, int.Parse(values[prefix+"cpus"]));
        }
    }
    internal static async Task WriteMemory(int index, int gb, int cpus)
    {
        if (gb is <3 or >12 || cpus is <1 or >64) throw new ArgumentOutOfRangeException(nameof(gb));
        if ((await VmRow(index)) is not { } row || row[4]=="1") throw new InvalidOperationException("종료된 선택 VM만 RAM을 변경합니다.");
        var local=LocalIndex(index);
        // Use the same setting keys/units as the original Prepare* implementations.
        if (KindOf(index)=="bluestacks")
        {
            if (BlueStacksProcesses().Count>0) throw new InvalidOperationException("다른 블루스택이 켜져 있어 RAM 변경을 생략합니다.");
            var key=$"bst.instance.{BlueStacksInstanceOf(local).Id}.ram";
            var lines=(await File.ReadAllLinesAsync(BlueStacksConf)).ToList();
            var at=lines.FindIndex(line=>line.StartsWith(key+"=",StringComparison.Ordinal));
            if(at<0) throw new InvalidOperationException("블루스택 RAM 설정을 찾지 못했습니다.");
            lines[at]=$"{key}=\"{gb*1024}\"";
            await File.WriteAllLinesAsync(BlueStacksConf,lines);
        }
        else if (KindOf(index)=="mumu")
            RequireMuMuOk(await MuMu(TimeSpan.FromSeconds(30),"setting","-v",local.ToString(),"-k","performance_mode","-val","custom","-k","performance_cpu.custom","-val",cpus.ToString(),"-k","performance_mem.custom","-val",gb.ToString()));
        else if (KindOf(index)=="ldplayer")
        {
            var path=LdConfig(local); var config=JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            config["advancedSettings.memorySize"]=gb*1024;
            await File.WriteAllTextAsync(path,config.ToJsonString(JsonOptions));
        }
        else if (KindOf(index)=="memu") await MEmuRun(TimeSpan.FromSeconds(30),"setconfigex","-i",local.ToString(),"memory",(gb*1024).ToString());
        else await RunBytesWithin(TimeSpan.FromSeconds(30),NoxConsole,["modify","-index:"+local,"-memory:"+(gb*1024)],false);
    }
    internal const long NewVmDiskBytes = 12L << 30;
    internal const long DiskMargin = 2L << 30;
    internal sealed record CreationDisk(string Drive, long Free, long Required, long Margin);
    // Original Vms.cs VmsDrive/RequireDisk, with the requested 9GB + 3GB estimate.
    internal static async Task<CreationDisk> DiskForCreation(string key)
    {
        var emulator = InstalledEmulators.SingleOrDefault(e => e.Key == key) ?? throw new InvalidOperationException("에뮬레이터를 먼저 설치하세요.");
        string? dir=null;
        var rows=await Task.Run(()=>VmListOf([emulator]));
        foreach(var row in rows.Split('\n').Select(line=>line.Split(',')).Where(row=>row.Length>=7))
            try { if(VmDir(int.Parse(row[0])) is { } candidate && Directory.Exists(candidate)) { dir=candidate; break; } } catch { }
        var drive=Path.GetPathRoot(Path.GetFullPath(dir??RequireFolder(key)))!;
        return new(drive,new DriveInfo(drive).AvailableFreeSpace,NewVmDiskBytes,DiskMargin);
    }
    internal static bool HasCreationSpace(long free) => free >= NewVmDiskBytes + DiskMargin;
    internal static async Task<int> Create(string key, string name, int memoryGb)
    {
        if (!InstalledEmulators.Any(e=>e.Key==key) || key=="bluestacks") throw new InvalidOperationException("이 VM은 해당 에뮬레이터의 멀티 매니저에서 생성하세요.");
        if (string.IsNullOrWhiteSpace(name) || name.Length>50 || name.Any(c=>char.IsControl(c) || c==',')) throw new ArgumentException("VM 이름을 확인하세요.");
        if (memoryGb is <3 or >8) throw new ArgumentOutOfRangeException(nameof(memoryGb));
        var disk=await DiskForCreation(key);
        if (!HasCreationSpace(disk.Free)) throw new InvalidOperationException($"디스크 공간 부족 · {disk.Drive}에 14GB가 필요합니다. 남은 공간 {disk.Free/(double)(1L<<30):0.#}GB");
        var index=await AddVm(key,name.Trim());
        await Prepare(index,false,memoryGb*1024,2);
        return index;
    }
    static async Task<Dictionary<int,string[]>> AllVmRows() => (await VmList()).Split('\n').Select(line=>line.Trim().Split(','))
        .Where(row=>row.Length>=7).ToDictionary(row=>int.Parse(row[0]));
    static async Task<int> NewVmIndex(HashSet<int> before,string name)
    {
        var created=(await AllVmRows()).Where(row=>!before.Contains(row.Key) && row.Value[1]==name).Select(row=>row.Key).ToList();
        if(created.Count!=1) throw new InvalidOperationException("새 VM을 찾지 못했습니다. 멀티 인스턴스 매니저를 확인하세요.");
        return created[0];
    }
    static Task RunLdManage(params string[] args) => RunBytesWithin(TimeSpan.FromMinutes(10),Ld,args,false);
    static async Task<string> Run(string file,params string[] args) => Encoding.UTF8.GetString(await RunBytesWithin(TimeSpan.FromSeconds(45),file,args));
    static async Task<byte[]> RunBytesWithin(TimeSpan timeout,string file,string[] args,bool checkExit=true)
    {
        Cancellation().ThrowIfCancellationRequested();
        ValidateNativeCommand(file,args);
        using var process=new Process { StartInfo=new(file) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true } };
        foreach(var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var bytes=new MemoryStream();
        var output=process.StandardOutput.BaseStream.CopyToAsync(bytes); var error=process.StandardError.ReadToEndAsync();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(Cancellation()); deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if(!process.HasExited) process.Kill(true); throw; }
        await output;
        var stderr=await error;
        if(checkExit && process.ExitCode!=0) throw new InvalidOperationException(Path.GetFileName(file)+": "+stderr);
        return bytes.ToArray();
    }
    // Provider methods cannot invoke deletion, cloning, account/data reset, or arbitrary Android shell.
    internal static void ValidateNativeCommand(string file,string[] args)
    {
        var exe=Path.GetFileName(file).ToLowerInvariant();
        if (args.Length==0) throw new InvalidOperationException("빈 VM 명령");
        var verb=args[0];
        bool allowed=exe switch
        {
            "ldconsole.exe" => verb is "launch" or "quit" or "add",
            "mumumanager.exe" => verb is "info" or "version" or "setting" or "create" or "rename" || verb=="control" && args.Length==4 && args[3] is "launch" or "shutdown",
            "memuc.exe" => verb is "start" or "stop" or "getconfigex" or "setconfigex" or "create" or "rename",
            "noxconsole.exe" => verb is "launch" or "quit" or "modify" or "globalsetting" or "add" or "rename",
            "open" => verb=="-n",
            "kill" => args.Length==2 && verb=="-TERM" && int.TryParse(args[1],out var pid) && pid>0,
            _ => args.Length>=3 && verb=="-s" && (args.Skip(2).SequenceEqual(new[]{"get-state"}) || args.Skip(2).SequenceEqual(new[]{"shell","reboot -p"}) || args.Skip(2).SequenceEqual(new[]{"exec-out","screencap","-p"}))
        };
        if(!allowed) throw new InvalidOperationException("허용하지 않은 VM 명령을 차단했습니다.");
    }
}
