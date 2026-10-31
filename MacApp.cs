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
    static void VerifySafety() => VerifyEmulatorChanges();
}
