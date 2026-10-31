using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    sealed record HostMemory(double TotalGb, double AvailableGb);
    sealed record VmMemory(string Mode, double Gb, int Cpus);
    sealed record MemoryReport(double VmGb, double PcAvailableGb, double PcTotalGb, string Status, DateTimeOffset CheckedAt);
    sealed record MemorySelection(double VmGb, double PcAvailableGb, int MaxGb, int TargetGb);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, MemoryReport> MemoryReports = new();
    static bool MemoryShutdownAuthorized;
    static string? AuthorizedMemoryCommand;
    static HostMemory ReadHostMemory()
    {
        if (OperatingSystem.IsMacOS()) return ReadMacMemory();
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        const double gib = 1024d * 1024 * 1024;
        return new(status.TotalPhysical / gib, status.AvailablePhysical / gib);
    }
    static int MaximumMemoryGb(double currentGb, bool running, HostMemory host) =>
        Math.Clamp((int)Math.Floor(host.AvailableGb - Math.Max(4, host.TotalGb * .2) + (running ? currentGb : 0)), 3, 12);
    static int TargetMemoryGb(double currentGb, bool running, HostMemory host, int requested) =>
        Math.Min(requested is >= 3 and <= 12 ? requested : 8, MaximumMemoryGb(currentGb, running, host));
    static int NewVmMemoryGb(HostMemory host) => Math.Clamp(TargetMemoryGb(0, false, host, 0), 3, 8);
    static bool CanSetMemory(double currentGb, int targetGb, bool running, HostMemory host) => targetGb is >= 3 and <= 12 && currentGb > 0 &&
        (targetGb < currentGb || host.AvailableGb >= (running ? targetGb - currentGb : targetGb) + Math.Max(4, host.TotalGb * .2));
    static bool CanIncreaseMemory(double currentGb, bool running, HostMemory host) => currentGb < 8 && CanSetMemory(currentGb, 8, running, host);
    static MemorySelection? MemoryForUi(int index, bool running)
    {
        if (!MemoryReports.TryGetValue(index, out var report)) return null;
        HostMemory host;
        try { host = ReadHostMemory(); } catch { host = new(report.PcTotalGb, report.PcAvailableGb); }
        return new(report.VmGb, host.AvailableGb, MaximumMemoryGb(report.VmGb, running, host), TargetMemoryGb(report.VmGb, running, host, RequestedMemoryGb));
    }
    static string[] MemoryReadArguments(int index) => ["setting", "-v", index.ToString(CultureInfo.InvariantCulture), "-k", "performance_mode",
        ..new[] { "low", "middle", "high", "custom" }.SelectMany(mode => new[] { "-k", "performance_mem." + mode, "-k", "performance_cpu." + mode })];
    static string[] MemoryWriteArguments(int index, int cpus, int gigabytes = 8)
    {
        if (index is < 0 or >= 1000 || cpus is < 1 or > 64 || gigabytes is < 3 or > 12) throw new InvalidOperationException("잘못된 메모리 설정 대상입니다.");
        return ["setting", "-v", index.ToString(CultureInfo.InvariantCulture), "-k", "performance_mode", "-val", "custom",
            "-k", "performance_cpu.custom", "-val", cpus.ToString(CultureInfo.InvariantCulture), "-k", "performance_mem.custom", "-val", gigabytes.ToString(CultureInfo.InvariantCulture)];
    }
    static void RequireManagerOk(JsonElement result)
    {
        if (result.TryGetProperty("errcode", out var error) && error.GetInt32() != 0)
            throw new InvalidOperationException("MuMu 메모리 설정: " + result);
    }
    static async Task<VmMemory> ReadVmMemory()
    {
        var memory = await OriginalEmulators.ReadMemory(Selected!.Index);
        return new("custom", memory.Gb, memory.Cpus);
    }
    static void RecordMemory(VmMemory vm, HostMemory host, string status) => MemoryReports[Selected!.Index] =
        new(vm.Gb, host.AvailableGb, host.TotalGb, status, DateTimeOffset.Now);
    static async Task RefreshMemoryInfo(bool force = false)
    {
        if (!force && MemoryReports.TryGetValue(Selected!.Index, out var prior) && DateTimeOffset.Now - prior.CheckedAt < TimeSpan.FromSeconds(15)) return;
        try
        {
            var vm = await ReadVmMemory(); var host = ReadHostMemory();
            RecordMemory(vm, host, vm.Gb >= 8 ? "현재 RAM 유지" : CanIncreaseMemory(vm.Gb, Selected!.Running, host) ? "매크로 시작 시 8GB로 증가 가능" : "PC 여유 RAM이 부족하여 현재 설정 유지");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log("RAM 조회 생략 · " + ex.Message); }
    }
    // The source project's shutdown -> settings -> launch sequence, restricted to RAM.
    // The current CPU count is preserved when converting a preset to custom mode.
    static async Task<bool> PrepareMemoryForRun()
    {
        var restartNeeded = false;
        try
        {
            var vm = await ReadVmMemory(); var host = ReadHostMemory();
            var target = TargetMemoryGb(vm.Gb, Selected!.Running, host, RequestedMemoryGb);
            if (!OriginalEmulators.MemoryCanChange(Selected.Index)) { RecordMemory(vm, host, "다른 블루스택이 실행 중이므로 RAM 유지"); return false; }
            if (target == vm.Gb || !CanSetMemory(vm.Gb, target, Selected.Running, host))
            {
                RecordMemory(vm, host, vm.Gb >= 8 ? "현재 RAM 유지" : "PC 여유 RAM이 부족하여 현재 설정 유지");
                return false;
            }
            Log($"VM RAM {vm.Gb:0.#}GB / PC 여유 {host.AvailableGb:0.0}GB · {target}GB로 조정합니다.");
            await Guard(false);
            if (Selected.Running)
            {
                try { await SetReaderLease(false); } catch { }
                Log($"RAM 적용을 위해 선택한 {Selected.Label}만 재시작합니다.");
                await OriginalEmulators.Shutdown(Selected.Index);
                restartNeeded = true;
                var until = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    await Guard(false);
                    var current = (await ListVms()).Single(item => item.Index == Selected.Index);
                    if (!current.Running) { Selected = current; break; }
                    if (DateTime.UtcNow >= until) throw new InvalidOperationException("VM 종료 대기 시간이 초과되어 RAM 변경을 생략합니다.");
                    await Task.Delay(1000, Stop.Token);
                }
            }
            host = ReadHostMemory();
            if (!CanSetMemory(vm.Gb, target, false, host))
            {
                RecordMemory(vm, host, "PC 여유 RAM이 줄어 기존 설정으로 재개");
                return restartNeeded;
            }
            await Guard(false);
            await OriginalEmulators.WriteMemory(Selected.Index, target, vm.Cpus);
            var applied = await ReadVmMemory();
            RecordMemory(applied, ReadHostMemory(), applied.Gb == target ? $"{target}GB 적용 · VM 시작 후 반영" : "설정 확인값으로 계속 진행");
            Log($"{Selected.Label} RAM 설정 {applied.Gb:0.#}GB · CPU {applied.Cpus}개 유지");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("RAM 자동 조정 생략 · 기존 설정으로 계속합니다: " + ex.Message);
            return restartNeeded;
        }
        finally { MemoryShutdownAuthorized = false; AuthorizedMemoryCommand = null; }
    }
    static async Task EnterStoryAfterMemoryChange(bool virtualOnly = false)
    {
        await EnsureRunning();
        await RefreshMemoryInfo(force: true);
        await ReturnHome();
        await EnterMainEventStories();
        await ApplyUnreadFilter(virtualOnly);
    }
}
