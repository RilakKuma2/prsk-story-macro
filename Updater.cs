using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Forms;
using System.Reflection;

// Self-update from GitHub releases: on every GUI start, compare the latest release tag with this build's version,
// and on the user's yes download the zip, close, copy it over the install and start again. local/ is never touched.
internal static partial class Program
{
    const string ReleaseRepo="RilakKuma2/prsk-story-macro";
    const string ReleaseAsset="SekaiStorySkip-win-x64.zip";

    static Version CurrentVersion=>Normalize(typeof(Program).Assembly.GetName().Version??new Version(0,0,0));
    static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(v.Build,0));

    // Only release packages replace themselves; source builds remain untouched.
    static bool IsReleaseInstall=>typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a=>a.Key=="ReleasePackage" && a.Value=="true");
    static int UpdateCheckRunning;
    static volatile bool UpdateDownloading,UpdateApplying;

    static void AddUpdateMenu(Form window,StoryPanel panel)
    {
        var menu=new MenuStrip();
        var check=new ToolStripMenuItem(T("업데이트 확인…","アップデートを確認…","Check for Updates…"));
        check.Click+=async (_,_)=>await CheckForUpdate(window,panel,true);
        menu.Items.Add(check);
        menu.Items.Add(new ToolStripMenuItem("v"+CurrentVersion) { Enabled=false });
        window.Controls.Add(menu);
        window.MainMenuStrip=menu;
        window.FormClosing+=(_,e)=>{ if(UpdateDownloading && !UpdateApplying) e.Cancel=true; };
    }

    static async Task CheckForUpdate(Form window,StoryPanel panel,bool manual=false)
    {
        if(Interlocked.Exchange(ref UpdateCheckRunning,1)!=0) return;
        var updating=false;
        void Notice(string message) { if(!window.IsDisposed) window.Invoke(()=>MessageBox.Show(window,message,AppTitle,MessageBoxButtons.OK,MessageBoxIcon.Information)); }
        try
        {
            if(!IsReleaseInstall)
            {
                if(manual) Notice(T("개발용 빌드입니다. 배포용 앱에서 업데이트할 수 있어요.","開発用ビルドです。配布版アプリで更新できます。","This is a development build. Use a release package to update."));
                return;
            }
            using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiStorySkip/"+CurrentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases/latest");
            if(response.StatusCode==System.Net.HttpStatusCode.NotFound)
            {
                if(manual) Notice(T("아직 공개된 릴리스가 없어요.","公開済みリリースがありません。","No public release is available yet."));
                return;
            }
            response.EnsureSuccessStatusCode();
            using var release=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if(window.IsDisposed || window.Disposing) return;
            var root=release.RootElement;
            var tag=root.GetProperty("tag_name").GetString()??"";
            if(!Version.TryParse(tag.TrimStart('v','V'),out var latest)) throw new InvalidDataException("Invalid release version: "+tag);
            if(Normalize(latest)<=CurrentVersion)
            {
                if(manual) Notice(T($"최신 버전입니다 (v{CurrentVersion}).",$"最新バージョンです（v{CurrentVersion}）。",$"You are up to date (v{CurrentVersion})."));
                return;
            }
            var asset=root.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>a.GetProperty("name").GetString()==ReleaseAsset);
            if(asset.ValueKind!=JsonValueKind.Object)
            {
                if(manual) Notice(T("새 버전의 Windows 설치 파일이 아직 없습니다.","新バージョンのWindows用ファイルがまだありません。","The Windows package is not available yet."));
                return;
            }
            var notes=root.TryGetProperty("body",out var body)?body.GetString()?.Trim():null;
            if(notes is { Length: >600 }) notes=notes[..600]+"…";

            if(panel.UpdateBusy || OtherMacroProcesses())
            {
                window.Invoke(()=>MessageBox.Show(window,T($"새 버전 {tag}이(가) 나왔어요 (지금 v{CurrentVersion}).\n매크로가 실행 중이라 지금은 업데이트하지 않아요. 모두 멈춘 뒤 다시 열면 업데이트할 수 있어요.",
                    $"新しいバージョン {tag} があります（現在 v{CurrentVersion}）。\nマクロ実行中のため今は更新しません。すべて止めてから開き直すと更新できます。",
                    $"Version {tag} is available (this is v{CurrentVersion}).\nA macro is running, so it is not updated now. Stop them all and reopen to update."),AppTitle,MessageBoxButtons.OK,MessageBoxIcon.Information));
                return;
            }
            var answer=(DialogResult)window.Invoke(()=>MessageBox.Show(window,T($"새 버전 {tag}이(가) 나왔어요 (지금 v{CurrentVersion}).\n지금 업데이트할까요? 받은 뒤 자동으로 다시 열려요.",
                $"新しいバージョン {tag} があります（現在 v{CurrentVersion}）。\n今すぐ更新しますか？取得後に自動で開き直します。",
                $"Version {tag} is available (this is v{CurrentVersion}).\nUpdate now? The app reopens by itself afterwards.")+(notes is { Length: >0 }?"\n\n"+notes:""),AppTitle,MessageBoxButtons.YesNo,MessageBoxIcon.Information));
            if(answer!=DialogResult.Yes) return;
            // Story workers run in this panel's process; hold its device gate while replacing the app.
            if(!panel.TryBeginUpdate())
            {
                Notice(T("작업 중입니다. 모두 멈춘 뒤 다시 확인해 주세요.","処理中です。すべて停止してから再度確認してください。","An operation is running. Stop it and check again."));
                return;
            }
            updating=true;
            UpdateDownloading=true;

            var staging=Path.Combine(DataRoot,"local","update");
            if(Directory.Exists(staging)) Directory.Delete(staging,true);
            Directory.CreateDirectory(staging);
            var zip=Path.Combine(staging,ReleaseAsset);
            await DownloadUpdate(asset.GetProperty("browser_download_url").GetString()!,zip,window);
            var extracted=Path.Combine(staging,"files");
            ZipFile.ExtractToDirectory(zip,extracted);
            var source=Path.GetDirectoryName(Directory.GetFiles(extracted,"SekaiStorySkip.exe",SearchOption.AllDirectories).FirstOrDefault()
                ??throw new InvalidDataException("SekaiStorySkip.exe is missing from the release."))!;
            if(!Directory.Exists(Path.Combine(source,"templates"))) throw new InvalidDataException("templates is missing from the release.");
            if(panel.UpdateBusy || OtherMacroProcesses())
            {
                Notice(T("매크로가 실행 중입니다. 모두 멈춘 뒤 다시 확인해 주세요.","マクロ実行中です。すべて停止してから再度確認してください。","A macro is running. Stop it and check again."));
                return;
            }

            var script=Path.Combine(staging,"apply.ps1");
            File.WriteAllText(script,ApplyScript);
            var info=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true };
            foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-ProcessId",Environment.ProcessId.ToString(),"-Source",source,"-Target",DataRoot,"-Staging",staging})
                info.ArgumentList.Add(arg);
            using var helper=Process.Start(info)??throw new IOException("Could not start the update helper.");
            UpdateApplying=true;
            window.Invoke(window.Close);
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // Automatic checks stay quiet offline; a manual check always reports the result.
            if(manual || updating) Notice(T("업데이트를 확인하지 못했어요. 인터넷 연결을 확인해 주세요.\n\n","更新を確認できませんでした。接続を確認してください。\n\n","Could not check for updates. Check your connection.\n\n")+ex.Message);
        }
        catch(Exception ex)
        {
            if(!window.IsDisposed) Notice(T("업데이트하지 못했어요.\n\n","更新できませんでした。\n\n","The update failed.\n\n")+ex.Message);
        }
        finally
        {
            if(updating) panel.EndUpdate();
            UpdateDownloading=false;
            Interlocked.Exchange(ref UpdateCheckRunning,0);
            if(!window.IsDisposed) window.BeginInvoke(()=>window.Text=AppTitle);
        }
    }

    static async Task DownloadUpdate(string url,string path,Form window)
    {
        using var http=new HttpClient { Timeout=TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiStorySkip/"+CurrentVersion);
        using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total=response.Content.Headers.ContentLength;
        await using var input=await response.Content.ReadAsStreamAsync();
        await using var output=File.Create(path);
        var buffer=new byte[1<<16]; long done=0; int read,shown=-2;
        while((read=await input.ReadAsync(buffer))>0)
        {
            await output.WriteAsync(buffer.AsMemory(0,read)); done+=read;
            var percent=total>0?(int)(done*100/total.Value):-1;
            if(percent==shown) continue;
            shown=percent;
            window.BeginInvoke(()=>window.Text=$"{AppTitle} · {T("업데이트 받는 중","アップデートを取得中","Downloading the update")} "+(percent<0?$"{done>>20} MB":$"{percent}%"));
        }
    }

    // Workers run the same exe; replacing it under them would fail.
    static bool OtherMacroProcesses()
    {
        var exe=Path.Combine(Root,"SekaiStorySkip.exe");
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Any(p=>
        {
            try { return p.Id!=Environment.ProcessId && string.Equals(p.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
            finally { p.Dispose(); }
        });
    }

    // ASCII only: paths arrive as arguments, so the Korean install folder never passes through the script's encoding.
    const string ApplyScript="""
        param([int]$ProcessId,[string]$Source,[string]$Target,[string]$Staging)
        # Wait for the app to exit, copy the new release over it (local\ is untouched), then start it again.
        Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
        if(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue){ exit 1 }
        $ok=$false
        for($i=0;$i -lt 30 -and -not $ok;$i++){
            robocopy $Source $Target /E /XD (Join-Path $Source 'templates') /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            $files=$LASTEXITCODE
            robocopy (Join-Path $Source 'templates') (Join-Path $Target 'templates') /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if($files -lt 8 -and $LASTEXITCODE -lt 8){ $ok=$true } else { Start-Sleep -Seconds 2 }
        }
        if(-not $ok){
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show('Update failed. Please extract the release ZIP manually.',$Target) | Out-Null
        }
        Start-Process -FilePath (Join-Path $Target 'SekaiStorySkip.exe') -WorkingDirectory $Target
        if($ok){ Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue }
        """;
}
