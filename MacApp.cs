using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Rectangle=System.Drawing.Rectangle;

internal static partial class Program
{
    // Original MacApp.swift/WKWebView host, on this independent project's own local port.
    static void ShowGui() => RunMacPanel().GetAwaiter().GetResult();
    static async Task RunMacPanel()
    {
        const string url="http://127.0.0.1:8796/";
        bool openBrowser=!Environment.GetCommandLineArgs().Contains("--window");
        var listener=new TcpListener(IPAddress.Loopback,8796);
        try { listener.Start(); listener.Stop(); }
        catch(SocketException) { if(openBrowser) Process.Start("/usr/bin/open",url)?.Dispose(); return; }
        using var panel=new StoryPanel();
        var server=panel.BuildServer(); server.Urls.Clear(); server.Urls.Add(url);
        await server.StartAsync(); await panel.RestoreListen(); panel.StartMonitor();
        await File.WriteAllTextAsync(Path.Combine(Artifacts,"panel-info.json"),System.Text.Json.JsonSerializer.Serialize(new { Url=url,Token=panel.Token }));
        if(openBrowser) Process.Start("/usr/bin/open",url)?.Dispose();
        await server.WaitForShutdownAsync();
        await panel.Shutdown(); await server.DisposeAsync();
    }
    static readonly SemaphoreSlim OcrBuild=new(1,1);
    static async Task<string> EnsureOcrTool()
    {
        var shipped=Path.Combine(Root,"sekai-ocr"); if(File.Exists(shipped)) return shipped;
        var tool=Path.Combine(DataRoot,"local","bin","sekai-ocr");
        await OcrBuild.WaitAsync();
        try
        {
            if(File.Exists(tool)) return tool;
            Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
            await RunMacTool("/usr/bin/xcrun","swiftc","-O","-o",tool,Path.Combine(Root,"mac","ocr.swift"));
            return tool;
        }
        finally { OcrBuild.Release(); }
    }
    static async Task<string> RunMacTool(string file,params string[] args)
    {
        using var process=new Process { StartInfo=new(file) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true } };
        foreach(var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(deadline.Token); } catch { process.Kill(true); throw; }
        if(process.ExitCode!=0) throw new InvalidOperationException(await error);
        return (await output).Trim();
    }
    static async Task<string> OcrRegionText(Bitmap screen,Rectangle region)
    {
        using var crop=screen.Clone(region,PixelFormat.Format24bppRgb);
        var path=Path.Combine(Artifacts,"percent-ocr.png"); crop.Save(path,ImageFormat.Png);
        return await RunMacTool(await EnsureOcrTool(),path,"3");
    }
    static async Task ApplyMacSelfUpdate(string bundle)
    {
        var info = await ReadUpdateInfo();
        if (info.Status != "available" || info.DownloadUrl is not { } url || !url.EndsWith("/SekaiStorySkip-mac-arm64.dmg", StringComparison.Ordinal))
            throw new InvalidOperationException(T("설치할 새 버전이 없습니다.", "インストールできる新しいバージョンがありません。", "No new version is available to install."));
        var staging = Path.Combine(DataRoot, "local", "update");
        var mnt = Path.Combine(staging, "mnt");
        try { if (Directory.Exists(mnt)) await RunMacTool("/usr/bin/hdiutil", "detach", mnt, "-force"); } catch { }
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(mnt);
        var dmg = Path.Combine(staging, "SekaiStorySkip-mac-arm64.dmg");
        Log($"새 버전 v{info.Latest} 다운로드 중…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiStorySkip/" + CurrentVersion);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(dmg);
            await input.CopyToAsync(output);
        }
        await RunMacTool("/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-mountpoint", mnt, dmg);
        var sourceApp = Path.Combine(mnt, "SekaiStorySkip.app");
        if (!File.Exists(Path.Combine(sourceApp, "Contents", "MacOS", "SekaiStorySkip")))
        {
            try { await RunMacTool("/usr/bin/hdiutil", "detach", mnt, "-force"); } catch { }
            throw new InvalidDataException("DMG 안에서 SekaiStorySkip.app을 확인하지 못했습니다.");
        }
        var script = Path.Combine(staging, "apply-mac.sh");
        await File.WriteAllTextAsync(script, """
            #!/bin/sh
            BUNDLE="$1"
            MNT="$2"
            STAGING="$3"
            PANEL_PID="$4"
            for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
              kill -0 "$PANEL_PID" 2>/dev/null || break
              sleep 0.25
            done
            for i in 1 2 3 4 5 6 7 8 9 10 11 12; do
              pgrep -f "$BUNDLE/Contents/MacOS/SekaiStorySkip" >/dev/null 2>&1 || break
              sleep 0.25
            done
            rm -rf "$BUNDLE"
            cp -R "$MNT/SekaiStorySkip.app" "$BUNDLE"
            xattr -cr "$BUNDLE" 2>/dev/null || true
            /usr/bin/hdiutil detach "$MNT" -force >/dev/null 2>&1 || true
            rm -rf "$STAGING" 2>/dev/null || true
            /usr/bin/open "$BUNDLE"
            """);
        var updater = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { script, bundle, mnt, staging, Environment.ProcessId.ToString() }) updater.ArgumentList.Add(arg);
        Process.Start(updater)?.Dispose();
        _ = Task.Run(async () => { await Task.Delay(350); Environment.Exit(0); });
    }
    static void VerifySafety() => VerifyEmulatorChanges();
}
