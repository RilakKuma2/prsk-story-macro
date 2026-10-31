using System.Diagnostics;
using System.Text.RegularExpressions;
internal static partial class Program
{
    static HostMemory ReadMacMemory()
    {
        static string Read(string file, params string[] args)
        {
            using var process = new Process { StartInfo = new(file) { UseShellExecute=false, RedirectStandardOutput=true, CreateNoWindow=true } };
            foreach(var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start(); var output=process.StandardOutput.ReadToEndAsync();
            if(!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("RAM 조회 시간 초과"); }
            return output.GetAwaiter().GetResult();
        }
        var total=long.Parse(Read("/usr/sbin/sysctl","-n","hw.memsize").Trim());
        var stats=Read("/usr/bin/vm_stat");
        var page=long.Parse(Regex.Match(stats,@"page size of (\d+) bytes").Groups[1].Value);
        long Pages(string label) => long.Parse(Regex.Match(stats,Regex.Escape(label)+@":\s*(\d+)").Groups[1].Value);
        // Free and inactive pages can be reused; do not count compressed or wired memory.
        return new(total/(1024d*1024*1024),(Pages("Pages free")+Pages("Pages inactive"))*page/(1024d*1024*1024));
    }
}
