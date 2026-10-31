using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static partial class Program
{
    sealed record Vm(int Index, string Name, long Created, bool Running, bool Booted, int Port, string? Address = null)
    {
        public string Kind => OriginalEmulators.Kind(Index);
        public int LocalIndex => OriginalEmulators.Local(Index);
        public string Label => OriginalEmulators.Label(Index);
    }
    static Vm? Selected;
    static string Manager => OriginalEmulators.MuMuManagerPath;
    static string Adb => OriginalEmulators.AdbPath(Selected!.Index);
    static string Serial => Selected!.Address ?? (Selected.Kind == "ldplayer" ? "emulator-" + (5554 + Selected.LocalIndex * 2) : $"127.0.0.1:{Selected.Port}");
    static bool ManualRestartAuthorized;

    static async Task<List<Vm>> ListVms(int? only = null) => (await OriginalEmulators.List(only)).Select(vm =>
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(vm.Serial, @"^(127\.0\.0\.1:\d+|emulator-\d+)$"))
            throw new InvalidOperationException("로컬 VM 연결만 허용합니다.");
        var port = vm.Serial.StartsWith("127.0.0.1:") ? int.Parse(vm.Serial.Split(':')[1]) : 0;
        return new Vm(vm.Index, vm.Name, vm.Created, vm.Running, vm.Running, port, vm.Serial);
    }).ToList();

    static async Task<IDisposable> BeginSession(int index)
    {
        Stop.Token.ThrowIfCancellationRequested();
        Selected = (await ListVms(index)).Single(vm => vm.Index == index);
        ReaderTouch = null;
        var locks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SekaiStorySkip", "locks");
        Directory.CreateDirectory(locks);
        var session = new FileStream(Path.Combine(locks, $"{Selected.Kind}-{Selected.LocalIndex}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { await MigrateMuMuRecords(); } catch (Exception ex) { Log("이전 진행 기록 보존: " + ex.Message); }
        Log($"{Selected.Label} · {Selected.Name} 선택");
        return session;
    }
    static async Task<Vm> Guard(bool mustRun = true)
    {
        Stop.Token.ThrowIfCancellationRequested();
        CheckPendingProtection();
        var selected = Selected ?? throw new InvalidOperationException("선택된 인스턴스가 없습니다.");
        var current = (await ListVms(selected.Index)).SingleOrDefault(vm => vm.Index == selected.Index);
        // Native VM-list commands can briefly return no row. Do not mistake that
        // observation failure for replacing the selected VM; keep input paused.
        if (current == null)
        {
            Log($"인스턴스 조회 누락 · {selected.Kind}/{selected.LocalIndex}, 입력 없이 재확인");
            await Task.Delay(500, Stop.Token);
            current = (await ListVms(selected.Index)).SingleOrDefault(vm => vm.Index == selected.Index);
        }
        if (current == null || current.Name != selected.Name || current.Created != selected.Created)
            throw new InvalidOperationException($"인스턴스 식별 정보 확인 실패: {selected.Kind}/{selected.LocalIndex}, 예상 name={selected.Name}, created={selected.Created}; 실제 name={current?.Name ?? "조회 누락"}, created={current?.Created.ToString() ?? "없음"}");
        if (mustRun && !current.Running) throw new InvalidOperationException("인스턴스가 실행 중이 아닙니다.");
        Selected = current;
        return current;
    }
    static async Task ConnectVm()
    {
        await Guard();
        await OriginalEmulators.Connect(Selected!.Index);
        if ((await Shell("getprop sys.boot_completed")).Trim() != "1") throw new InvalidOperationException("안드로이드 부팅 대기 중입니다.");
    }
    // Original Resume.cs + EnsureResolution flow, using its provider-specific preparation and ADB.
    static async Task EnsureRunning(bool launchGame = true)
    {
        Selected = await Guard(false);
        if (!Selected.Running)
        {
            await OriginalEmulators.Prepare(Selected.Index, false);
            Log($"{Selected.Label} · 선택한 인스턴스 부팅");
            await OriginalEmulators.Launch(Selected.Index);
        }
        await WaitVmBoot();
        var resolution = await Shell("wm size; wm density");
        if (resolution.Contains("Physical size") && !OriginalEmulators.ResolutionOk(resolution))
        {
            if (!OriginalEmulators.CanFixResolution(Selected!.Index)) throw new InvalidOperationException("블루스택 해상도를 960×540, DPI 160으로 설정하세요.");
            Log("원본 VM 설정 적용 · 960×540 / DPI 160으로 재시작");
            await OriginalEmulators.Shutdown(Selected!.Index);
            await Guard(false);
            await OriginalEmulators.Prepare(Selected.Index, false);
            await OriginalEmulators.Launch(Selected.Index);
            await WaitVmBoot();
            if (!OriginalEmulators.ResolutionOk(await Shell("wm size; wm density"))) throw new InvalidOperationException("VM 해상도 설정이 반영되지 않았습니다.");
        }
        if (!launchGame) return;
        var current = await Foreground();
        if(IsGoogleSignIn(current))current=await LeaveGoogleSignIn(current);
        // Original Google Play sign-in handling is completed by ReturnHome's Android BACK.
        if (!current.Contains(Package) && !current.Contains("com.google.android.gms"))
        {
            Log("기존 계정으로 셐 실행");
            await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
        }
    }
    static async Task WaitVmBoot()
    {
        var until = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            Stop.Token.ThrowIfCancellationRequested();
            try { await ConnectVm(); return; }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not RecoveryNeeded && DateTime.UtcNow < until) { await Task.Delay(2000, Stop.Token); }
        }
    }
    static async Task RestartGameAndRead()
    {
        await EnsureRunning(launchGame: false);
        try { await SetReaderLease(false); } catch { }
        ManualRestartAuthorized = true;
        try { await Shell($"am force-stop {Package}"); }
        finally { ManualRestartAuthorized = false; }
        ReaderTouch = null; LastAliveCheck = DateTime.MinValue;
        await EnsureRunning(); await ReturnHome(); await EnterMainEventStories(); await ApplyUnreadFilter();
        await ReadStoryQueue();
    }

    static bool IsGoogleSignIn(string activity) => activity.Contains("com.google.android.gms/") && (activity.Contains("MinuteMaid") || activity.Contains("SignIn"));
    static async Task<string> Foreground() => string.Join('\n', (await Shell("dumpsys activity activities")).Split('\n').Where(l => l.Contains("topResumedActivity=") || l.Contains("mResumedActivity:")));
    static async Task<string> Shell(string command) => Encoding.UTF8.GetString(await RunBytes(Adb, ["-s", Serial, "shell", command]));

    // Defense in depth at the only process-start boundary. No arbitrary shell, broadcast device or manager verbs.
    static void ValidateProcessArgs(string[] args, bool manager)
    {
        if (manager)
        {
            if (args.SequenceEqual(new[] { "info", "-v", "all" })) return;
            if (Selected != null && args.SequenceEqual(new[] { "control", "-v", Selected.LocalIndex.ToString(CultureInfo.InvariantCulture), "launch" })) return;
            if (Selected != null && args.SequenceEqual(MemoryReadArguments(Selected.LocalIndex))) return;
            if (Selected != null && MemoryShutdownAuthorized && args.SequenceEqual(new[] { "control", "-v", Selected.LocalIndex.ToString(CultureInfo.InvariantCulture), "shutdown" })) { MemoryShutdownAuthorized = false; return; }
            if (Selected != null && AuthorizedMemoryCommand != null && args.Length >= 3 && args[0] == "setting" && args[1] == "-v" && args[2] == Selected.LocalIndex.ToString(CultureInfo.InvariantCulture) && string.Join('\0', args) == AuthorizedMemoryCommand)
            { AuthorizedMemoryCommand = null; return; }
        }
        else
        {
            if (Selected != null && args.Length >= 3 && args[0] == "-s" && args[1] == Serial && AuthorizedInstallCommand != null && string.Join('\0',args) == AuthorizedInstallCommand) { AuthorizedInstallCommand = null; return; }
            if (Selected != null && args.Length >= 3 && args[0] == "-s" && args[1] == Serial && AuthorizedAuxiliary != null && string.Join('\0', args) == AuthorizedAuxiliary) { AuthorizedAuxiliary = null; return; }
            if (Selected != null && args.SequenceEqual(new[] { "connect", Serial })) return;
            if (Selected != null && args.Length >= 4 && args[0] == "-s" && args[1] == Serial)
            {
                if (args.SequenceEqual(new[] { "-s", Serial, "exec-out", "screencap", "-p" })) return;
                if (args.Length == 4 && args[2] == "shell")
                {
                    if (ManualRestartAuthorized && args[3] == $"am force-stop {Package}") { ManualRestartAuthorized = false; return; }
                    if (AutoResumeEnabled && RestartAuthorized && args[3] == $"am force-stop {Package}") { RestartAuthorized = false; return; }
                    if (args[3] is "getprop sys.boot_completed" or "getprop ro.product.cpu.abi" or "getevent -lp" or "getevent -lp /dev/input/event2" or "wm size; wm density" or "dumpsys activity activities" || args[3] == $"pidof {Package} || true" || args[3] == $"monkey -p {Package} -c android.intent.category.LAUNCHER 1") return;
                    if (AuthorizedInput != null && args[3] == AuthorizedInput) { AuthorizedInput = null; return; }
                }
            }
        }
        throw new InvalidOperationException("안전장치: 허용하지 않은 장치 명령을 차단했습니다.");
    }
    static string? AuthorizedInput;
    static bool IsRepeatableAdbRead(string[] args) => args.Length >= 4 && args[0] == "-s" &&
        (args.Skip(2).SequenceEqual(new[] { "exec-out", "screencap", "-p" }) ||
         args.Length == 4 && args[2] == "shell" &&
         (args[3] is "getprop sys.boot_completed" or "getprop ro.product.cpu.abi" or "getevent -lp" or "getevent -lp /dev/input/event2" or "wm size; wm density" or "dumpsys activity activities" ||
          args[3] == $"pidof {Package} || true"));
    static bool IsSelectedGameStop(string[] args) => Selected != null &&
        args.SequenceEqual(new[] { "-s", Serial, "shell", $"am force-stop {Package}" });
    static bool IsAdbConnectionFailure(Exception error) => error is not OperationCanceledException &&
        (error.Message.TrimEnd().EndsWith("):") || new[] { "not found", "offline", "doesn't match", "protocol fault", "connection reset", "daemon", "closed" }
            .Any(text => error.Message.Contains(text, StringComparison.OrdinalIgnoreCase)));

    // Original RunBytesWithin reconnect/backoff. Callers limit replay to reads
    // and the already-authorized, idempotent stop of the selected game package.
    static async Task<byte[]> RunAdbReadWithReconnect(Func<Task<byte[]>> run, Func<Task> reconnect,
        CancellationToken token, Func<int, Task>? wait = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await run(); }
            catch (Exception ex) when (attempt < 4 && IsAdbConnectionFailure(ex))
            {
                Log($"ADB 연결 복구 {attempt}/3 · 선택한 인스턴스 재연결");
                await (wait?.Invoke(attempt) ?? Task.Delay(attempt * 1500, token));
                token.ThrowIfCancellationRequested();
                try { await reconnect(); }
                catch (Exception reconnectError) when (IsAdbConnectionFailure(reconnectError)) { }
            }
        }
    }
    static async Task<byte[]> RunBytes(string file, string[] args, bool manager = false, CancellationToken? cancellation = null, TimeSpan? timeout = null)
    {
        var token = cancellation ?? Stop.Token;
        token.ThrowIfCancellationRequested();
        ValidateProcessArgs(args, manager);
        // Authorization is consumed above once, before any transport retries.
        // Restarting an ADB server drops TCP devices during Nox startup.
        if (!manager && (IsRepeatableAdbRead(args) || IsSelectedGameStop(args)))
        {
            return await RunAdbReadWithReconnect(() => RunBytesOnce(file, args, manager, token), async () =>
            {
                await OriginalEmulators.Connect(Selected!.Index);
            }, token);
        }
        return await RunBytesOnce(file, args, manager, token, timeout);
    }
    static async Task<byte[]> RunBytesOnce(string file, string[] args, bool manager, CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Only the dead-man-lease-controlled reader may stay connected beyond a normal command timeout.
        var reader = Selected != null && args.SequenceEqual(new[] { "-s", Serial, "shell", "sh " + ReaderRemote("taps.sh") });
        deadline.CancelAfter(timeout ?? (reader ? TimeSpan.FromHours(12) : TimeSpan.FromSeconds(45)));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException($"장치 명령 응답 시간이 초과되었습니다: {Path.GetFileName(file)}");
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await copy;
        var stderr = await error;
        if (!manager && process.ExitCode != 0) throw new InvalidOperationException($"ADB 실패 ({process.ExitCode}): {stderr}");
        if (reader && !string.IsNullOrWhiteSpace(stderr))
            throw new InvalidOperationException($"스토리 연타 스크립트 오류: {stderr.Trim()}");
        return output.ToArray();
    }
}
