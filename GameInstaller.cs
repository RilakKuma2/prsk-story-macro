using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    const string GamePageUrl = "https://apkpure.com/kr/hatsune-miku-colorful-stage-2024-japan/com.sega.pjsekai";
    const string GameXapkUrl = "https://d.apkpure.com/b/XAPK/com.sega.pjsekai?version=latest";
    const string QooAppPackage = "com.qooapp.qoohelper";
    const string QooAppApkUrl = "https://d.qoo-apk.com/QooApp.apk";
    static string ApkCache => Path.Combine(DataRoot,"local","apk");
    static Func<string,bool>? GameDownloadWindow = null;
    static string? AuthorizedInstallCommand;
    static string ApkUrl(string key) => key=="qooapp" ? QooAppApkUrl : GameXapkUrl;
    static string LatestFile(string key) => Path.Combine(ApkCache,$"latest-{key}.txt");
    static string KeptPath(string key,string version,bool apk) => Path.Combine(ApkCache,$"{key}-{(version.Length>0?version:"latest")}{(apk?".apk":".xapk")}");
    static string? KeptVersionFile(string key,string version) => new[]{KeptPath(key,version,false),KeptPath(key,version,true)}.FirstOrDefault(File.Exists);
    static string PackageFileVersion(string file) => Regex.Match(Path.GetFileName(file),@"([0-9]+(?:\.[0-9]+)+)").Value;
    static bool IsGameDownloadSite(string url) => Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme=="https" &&
        uri.Host is "apkpure.com" or "d.apkpure.com" or "www.qoo-app.com" or "d.qoo-apk.com";

    // Same APKPure/QooApp sources, version cache and browser download flow as the invitation macro.
    static async Task<string> FetchLatestGameFile(string key,Action<string> report)
    {
        Directory.CreateDirectory(ApkCache);
        File.Delete(LatestFile(key));
        var since = DateTime.Now.AddSeconds(-2);
        if (GameDownloadWindow?.Invoke(key) != true)
        {
            if (await TryDownloadGameFile(key,report) is { } direct) return direct;
            Process.Start(new ProcessStartInfo(key=="qooapp"?QooAppApkUrl:GamePageUrl){UseShellExecute=true})?.Dispose();
        }
        report(T("받기 창에서 파일을 받으면 설치를 이어갑니다.","取得ウィンドウでダウンロードするとインストールを続けます。","Download the file in the browser to continue installation."));
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            downloads = Environment.ExpandEnvironmentVariables(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders","{374DE290-123F-4565-9164-39C4925E467B}",Path.Combine(downloads,"Downloads")) as string ?? Path.Combine(downloads,"Downloads"));
        else downloads = Path.Combine(downloads,"Downloads");
        var deadline = DateTime.UtcNow.AddHours(1);
        while (DateTime.UtcNow < deadline)
        {
            Stop.Token.ThrowIfCancellationRequested();
            if (File.Exists(LatestFile(key)) && KeptVersionFile(key,File.ReadAllText(LatestFile(key)).Trim()) is { } kept) return kept;
            if (Directory.Exists(downloads))
            {
                var file = Directory.EnumerateFiles(downloads).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".apk" or ".xapk")
                    .Where(f => File.GetLastWriteTime(f)>=since && DateTime.Now-File.GetLastWriteTime(f)>TimeSpan.FromSeconds(2))
                    .Where(f => key=="qooapp" ? Path.GetFileName(f).Contains("qoo",StringComparison.OrdinalIgnoreCase) :
                        new[]{"pjsekai","プロジェクトセカイ","hatsune","colorful"}.Any(n=>Path.GetFileName(f).Contains(n,StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (file != null)
                {
                    using var archive=ZipFile.OpenRead(file);
                    if (archive.GetEntry("manifest.json")!=null || archive.GetEntry("AndroidManifest.xml")!=null)
                    {
                        var target=KeptPath(key,PackageFileVersion(file),Path.GetExtension(file).Equals(".apk",StringComparison.OrdinalIgnoreCase));
                        File.Copy(file,target,true);return target;
                    }
                }
            }
            await Task.Delay(1000,Stop.Token);
        }
        throw new TimeoutException(T("파일을 1시간 동안 기다렸지만 받지 못했어요.","ファイルを1時間待ちましたが取得できませんでした。","No file arrived within an hour."));
    }
    static async Task<string?> TryDownloadGameFile(string key,Action<string> report)
    {
        string? partial = null;
        try
        {
            using var http=new HttpClient{Timeout=Timeout.InfiniteTimeSpan};
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiStorySkip/"+CurrentVersion);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(Stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var response=await http.GetAsync(ApkUrl(key),HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("html")==true) return null;
            deadline.CancelAfter(TimeSpan.FromHours(1));
            var name=response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ??
                Uri.UnescapeDataString(response.RequestMessage?.RequestUri?.Query??"");
            var version=PackageFileVersion(name);
            var target=KeptPath(key,version,key=="qooapp" || name.Contains(".apk",StringComparison.OrdinalIgnoreCase) && !name.Contains(".xapk",StringComparison.OrdinalIgnoreCase));
            if (version.Length>0 && File.Exists(target)) return target;
            partial=target+".part";
            await using (var input=await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output=File.Create(partial))
            {
                var buffer=new byte[1<<20];long done=0;var shown=DateTime.MinValue;
                for (int count;(count=await input.ReadAsync(buffer,deadline.Token))>0;)
                {
                    await output.WriteAsync(buffer.AsMemory(0,count),deadline.Token);done+=count;
                    if (DateTime.UtcNow-shown>TimeSpan.FromSeconds(2))
                    { shown=DateTime.UtcNow;report($"{key} · {done>>20}/{(response.Content.Headers.ContentLength??0)>>20}MB"); }
                }
            }
            using (var archive=ZipFile.OpenRead(partial))
                if (archive.GetEntry("manifest.json")==null && archive.GetEntry("AndroidManifest.xml")==null) return null;
            File.Move(partial,target,true);partial=null;return target;
        }
        catch (OperationCanceledException) when (!Stop.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { return null; }
        finally { if (partial!=null) File.Delete(partial); }
    }

    static async Task<string> InstallAdb(TimeSpan timeout,params string[] command)
    {
        await Guard();
        var args=new[]{"-s",Serial}.Concat(command).ToArray();
        AuthorizedInstallCommand=string.Join('\0',args);
        try { return Encoding.UTF8.GetString(await RunBytes(Adb,args,timeout:timeout)); }
        finally { AuthorizedInstallCommand=null; }
    }
    static Task<string> InstallRead(string command) => InstallAdb(TimeSpan.FromSeconds(45),"shell",command);
    static async Task<bool> InstalledForSetup(string package) => (await InstallRead($"pm path {package} || true")).Contains("package:");
    static async Task BootForGameInstall()
    {
        var vm=await Guard(false);
        if (!vm.Running) { Log(T("선택한 인스턴스 부팅 중","選択したインスタンスを起動中","Booting the selected instance"));await OriginalEmulators.Launch(vm.Index); }
        await WaitVmBoot();
    }
    static List<(string File,string Id)> ChooseGameSplits(JsonElement manifest,string abiList)
    {
        var splits=manifest.GetProperty("split_apks").EnumerateArray().Select(e=>(File:e.GetProperty("file").GetString()!,Id:e.GetProperty("id").GetString()??"")).ToList();
        string[] known=["x86_64","x86","arm64_v8a","armeabi_v7a","armeabi"];
        var abiSplits=splits.Where(s=>known.Any(k=>s.Id=="config."+k)).ToList();
        var chosen=abiList.Trim().Split(',').Select(a=>abiSplits.FirstOrDefault(s=>s.Id=="config."+a.Replace('-','_'))).FirstOrDefault(s=>s.File!=null);
        if (abiSplits.Count>0 && chosen.File==null) throw new InvalidDataException("이 인스턴스와 호환되는 게임 APK가 없습니다.");
        return splits.Where(s=>!abiSplits.Contains(s) || s==chosen).ToList();
    }
    static async Task InstallGameArchive(string file,string staging,Action<string> report)
    {
        using var archive=ZipFile.OpenRead(file);
        var apks=new List<string>();
        if (archive.GetEntry("manifest.json") is { } manifestEntry)
        {
            using var manifest=JsonDocument.Parse(manifestEntry.Open());
            var root=manifest.RootElement;
            if (root.GetProperty("package_name").GetString()!=Package) throw new InvalidDataException("프로젝트 세카이 설치 파일이 아닙니다.");
            foreach (var split in ChooseGameSplits(root,await InstallRead("getprop ro.product.cpu.abilist")))
            {
                var path=Path.Combine(staging,Path.GetFileName(split.File));
                (archive.GetEntry(split.File)??throw new InvalidDataException("APK 파일이 누락됐습니다.")).ExtractToFile(path,true);apks.Add(path);
            }
            if (root.TryGetProperty("expansions",out var expansions))
                foreach (var expansion in expansions.EnumerateArray())
                {
                    var remote=expansion.GetProperty("install_path").GetString()!;
                    if (!Regex.IsMatch(remote,@"^Android/obb/com\.sega\.pjsekai/[A-Za-z0-9._-]+\.obb$")) throw new InvalidDataException("추가 데이터 경로가 올바르지 않습니다.");
                    var entry=archive.GetEntry(expansion.GetProperty("file").GetString()!)??throw new InvalidDataException("추가 데이터가 누락됐습니다.");
                    var path=Path.Combine(staging,Path.GetFileName(remote));entry.ExtractToFile(path,true);
                    await InstallRead($"mkdir -p /sdcard/Android/obb/{Package}");
                    await InstallAdb(TimeSpan.FromMinutes(30),"push",path,"/sdcard/"+remote);
                }
        }
        else
        {
            if (archive.GetEntry("AndroidManifest.xml")==null) throw new InvalidDataException("APK 파일이 아닙니다.");
            var path=Path.Combine(staging,"base.apk");File.Copy(file,path,true);apks.Add(path);
        }
        if (apks.Count==0) throw new InvalidDataException("설치할 APK가 없습니다.");
        report(T("게임 설치 중","ゲームをインストール中","Installing the game"));
        var output=await InstallAdb(TimeSpan.FromMinutes(15),[apks.Count>1?"install-multiple":"install","-r","-g",..apks]);
        if (!output.Contains("Success") || !await InstalledForSetup(Package)) throw new InvalidOperationException("게임 설치 실패: "+output.Trim());
    }
    static async Task InstallGameOnly(bool update,Action<string> report)
    {
        var wasOff=!(await Guard(false)).Running;
        await BootForGameInstall();
        var temp=Path.GetTempPath();
        if (!temp.All(c=>c<128)) temp=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var staging=Path.Combine(temp,"SekaiStorySkip-adb",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var installed=await InstalledForSetup(Package);
            if (update && !installed) throw new InvalidOperationException(T("게임을 먼저 설치하세요.","先にゲームをインストールしてください。","Install the game first."));
            if (update || !installed)
            {
                var file=await FetchLatestGameFile("pjsekai",report);
                await InstallGameArchive(file,staging,report);
            }
            else report(T("게임이 이미 설치돼 있어요","ゲームはインストール済みです","The game is already installed"));
            if (!update && !await InstalledForSetup(QooAppPackage))
            {
                try
                {
                    var file=await FetchLatestGameFile("qooapp",report);
                    var apk=Path.Combine(staging,"QooApp.apk");File.Copy(file,apk,true);
                    report(T("QooApp 설치 중","QooAppをインストール中","Installing QooApp"));
                    var output=await InstallAdb(TimeSpan.FromMinutes(5),"install","-r",apk);
                    if (!output.Contains("Success") || !await InstalledForSetup(QooAppPackage)) throw new InvalidOperationException(output.Trim());
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { report("QooApp 설치 실패: "+ex.Message); }
            }
            report(T(update?"게임 업데이트 완료":"게임 설치 완료",update?"ゲーム更新完了":"ゲームインストール完了",update?"Game update complete":"Game installation complete"));
        }
        finally
        {
            try { Directory.Delete(staging,true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report("설치 임시 파일 정리 지연 · "+ex.Message); }
            if (update && wasOff) await OriginalEmulators.Shutdown(Selected!.Index);
        }
        // Installation ends here, exactly before the invitation macro's onboarding/start steps.
    }
}
