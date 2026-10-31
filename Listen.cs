using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;

internal static partial class Program
{
    // Original WebServer.cs LAN address selection: routed adapters first, then other active IPv4 adapters.
    static IEnumerable<string> LanAddresses()
    {
        var up=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).ToList();
        var routed=up.Where(n=>n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))).ToList();
        return (routed.Count>0?routed:up).SelectMany(n=>n.GetIPProperties().UnicastAddresses)
            .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
            .Select(a=>a.Address.ToString()).Distinct();
    }
    sealed record ListenRequest(bool Enabled,int Port=8788);
    static bool ValidListenPort(int port)=>port is >=1024 and <=65535;
    sealed partial class StoryPanel
    {
        readonly SemaphoreSlim listenGate=new(1,1);
        WebApplication? lanServer;
        int listenPort=8788;
        string ListenSetting=>Path.Combine(DataRoot,"local","lan-port.txt");
        object ListenState()=>new { enabled=lanServer!=null,port=listenPort,urls=lanServer==null?Array.Empty<string>():LanAddresses().Select(ip=>$"http://{ip}:{listenPort}/").ToArray() };
        public async Task RestoreListen()
        {
            try { if(File.Exists(ListenSetting) && int.TryParse(await File.ReadAllTextAsync(ListenSetting),out var port) && ValidListenPort(port)) await SetListen(true,port); }
            catch(Exception ex){Log("원격 접속 시작 실패 · "+ex.Message);}
        }
        async Task SetListen(bool enabled,int port)
        {
            if(!ValidListenPort(port)) throw new ArgumentException("포트는 1024~65535로 입력하세요.");
            await listenGate.WaitAsync();
            try
            {
                if(lanServer!=null)
                {
                    if(enabled && listenPort==port) return;
                    await lanServer.StopAsync(); await lanServer.DisposeAsync(); lanServer=null;
                }
                if(enabled)
                {
                    var server=BuildServer("0.0.0.0",port);
                    try { await server.StartAsync(); }
                    catch { await server.DisposeAsync(); throw; }
                    listenPort=port;lanServer=server;
                    Directory.CreateDirectory(Path.GetDirectoryName(ListenSetting)!);
                    await File.WriteAllTextAsync(ListenSetting,port.ToString());
                    Log($"원격 접속 켜짐 · 0.0.0.0:{port}");
                }
                else { listenPort=port;if(File.Exists(ListenSetting)) File.Delete(ListenSetting);Log("원격 접속 꺼짐"); }
            }
            finally {listenGate.Release();}
        }
        async Task CloseListen()
        {
            if(lanServer==null) return;
            await lanServer.StopAsync(); await lanServer.DisposeAsync(); lanServer=null;
        }
    }
}
