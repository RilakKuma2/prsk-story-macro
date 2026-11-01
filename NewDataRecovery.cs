using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    sealed record UserSettings(bool AutoResume = false, int MemoryGb = 0);
    static volatile bool AutoResumeEnabled;
    static volatile int RequestedMemoryGb;
    static volatile bool WorkflowActive;
    static bool RestartAuthorized;
    static string SettingsPath => Path.Combine(DataRoot, "local", "settings.json");
    enum RecoveryCause { NewData, UnknownState, GameExited, AccountSafety }
    class RecoveryNeeded(RecoveryCause cause, string message) : Exception(message) { public RecoveryCause Cause { get; } = cause; }
    sealed class NewDataDetected : RecoveryNeeded { public NewDataDetected() : base(RecoveryCause.NewData, "새 데이터 알림이 감지되었습니다.") { } }
    static DateTime LastAliveCheck = DateTime.MinValue;
    static bool CanAutoRecover(bool enabled, int unknownFailures) => enabled && unknownFailures < 2;

    static async Task<bool> GameExitedConfirmed()
    {
        if ((await Shell($"pidof {Package} || true")).Trim().Length != 0) return false;
        await Task.Delay(1000, Stop.Token);
        return (await Shell($"pidof {Package} || true")).Trim().Length == 0;
    }
    static async Task CheckGameLiveness()
    {
        if (DateTime.UtcNow - LastAliveCheck < TimeSpan.FromSeconds(3)) return;
        LastAliveCheck = DateTime.UtcNow;
        if (await GameExitedConfirmed()) throw new RecoveryNeeded(RecoveryCause.GameExited, "셐 프로세스 종료가 두 번 연속 확인되었습니다.");
    }
    static async Task EnsureGameForeground()
    {
        if ((await Foreground()).Contains(Package + "/")) return;
        var until = DateTime.UtcNow.AddSeconds(30);
        do
        {
            if (await GameExitedConfirmed()) throw new RecoveryNeeded(RecoveryCause.GameExited, "셐 종료 확인");
            await Task.Delay(1000, Stop.Token);
            if ((await Foreground()).Contains(Package + "/")) return;
        } while (DateTime.UtcNow < until);
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "셐 전면 화면을 30초 동안 확인하지 못했습니다.");
    }

    static void LoadUserSettings()
    {
        try
        {
            var settings = LoadRecord<UserSettings>(SettingsPath);
            AutoResumeEnabled = settings?.AutoResume == true;
            RequestedMemoryGb = settings?.MemoryGb is >= 3 and <= 12 ? settings.MemoryGb : 0;
        }
        catch { AutoResumeEnabled = false; RequestedMemoryGb = 0; }
    }
    static async Task SaveAutoResume(bool enabled)
    {
        AutoResumeEnabled = enabled;
        await SaveRecord(SettingsPath, JsonSerializer.Serialize(new UserSettings(enabled, RequestedMemoryGb), new JsonSerializerOptions { WriteIndented = true }));
    }
    static async Task SaveMemoryChoice(int gigabytes)
    {
        var maximum = OperatingSystem.IsMacOS() ? MacMemoryLimits(ReadHostMemory()).MaxGb : 12;
        if (gigabytes < 3 || gigabytes > maximum) throw new InvalidOperationException($"RAM은 3~{maximum}GB 범위에서 선택하세요.");
        RequestedMemoryGb = gigabytes;
        await SaveRecord(SettingsPath, JsonSerializer.Serialize(new UserSettings(AutoResumeEnabled, gigabytes), new JsonSerializerOptions { WriteIndented = true }));
    }
    static bool IsNewDataNotice(Bitmap screen) => screen.Width == 960 && screen.Height == 540 &&
        Matches(screen, new("new-data-message", "new-data-message.png", 0, 0, 384, 234, 188, 38)) &&
        Matches(screen, new("new-data-ok", "new-data-ok.png", 0, 0, 467, 308, 27, 21));

    static void PreserveNewDataNotice(byte[] bytes)
    {
        var folder = Path.Combine(Artifacts, "new-data");
        var name = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + $"-instance-{Selected!.Index}";
        var imagePath = Path.Combine(folder, name + ".webp");
        SaveEvidenceImage(bytes,imagePath);
        var recordPath = Path.Combine(folder, name + ".json");
        SaveEvidence(recordPath, () => File.WriteAllText(recordPath, JsonSerializer.Serialize(new
        {
            Instance = Selected.Index, Time = DateTimeOffset.Now, AutoResume = AutoResumeEnabled,
            Message = "新しいデータがあります。タイトルへ戻ります。"
        }, new JsonSerializerOptions { WriteIndented = true })));
    }

    // Called only by an active macro, never by passive preview/percentage monitoring.
    static async Task RunWithNewDataRecovery(Func<Task> work, Func<Task>? continueAfterRecovery = null, bool singleEvent = false, bool virtualLive = false, Func<Action, Task>? enter = null)
    {
        var safetyResume=false;
        Interlocked.Exchange(ref PendingProtectionReason,null);
        LockedStoriesInRun.Clear();
        WorkflowActive = true;
        try
        {
            await RunRecoveryAttempts(work, async interruption =>
            {
                if (interruption.Cause==RecoveryCause.NewData) LockedStoriesInRun.Clear();
                try { await SetReaderLease(false); } catch { }
                if (CurrentFrames.Read(Selected!.Index) is { } frame)
                    SaveEvidenceImage(frame.Png, Path.Combine(Artifacts, $"instance-{Selected.Index}-before-recovery.webp"));
            }, async interruption =>
            {
                safetyResume=interruption.Cause==RecoveryCause.AccountSafety;
                Log($"{interruption.Message} 자동 재개: 셐을 재실행합니다.");
                return await RestartGameForRecovery(interruption.Cause);
            }, async resetUnknownFailures =>
            {
                if (!AutoResumeEnabled && !safetyResume) throw new OperationCanceledException("자동 재개가 해제되어 재진입을 멈췄습니다.");
                await EnsureRunning();
                await ReturnHome();
                if (enter != null)
                {
                    // Side stories re-enter through their own story menu card and skip finished characters.
                    Log("자동 재개 · 메인화면 복귀 후 사이드스토리 재진입");
                    await enter(resetUnknownFailures);
                    return;
                }
                await EnterMainEventStories();
                if (!singleEvent) await ApplyUnreadFilter(virtualLive);
                resetUnknownFailures();
                Log("스토리 목록·필터 복원 성공 · 상태 인식 실패 카운트 초기화");
                Log("자동 재개 완료 · 메인 / 이벤트 스토리 진입 및 미읽음/스킵 필터 복원.");
                if (continueAfterRecovery != null) await continueAfterRecovery();
            }, () => AutoResumeEnabled, singleEvent);
        }
        finally { WorkflowActive = false; RestartAuthorized = false; LockedStoriesInRun.Clear(); Interlocked.Exchange(ref PendingProtectionReason,null); }
    }

    // Only unknown-state failures count. Crashes/new data can recover repeatedly; restoring
    // the story catalogue and filters clears the counter before continuing the current work.
    static async Task RunRecoveryAttempts(Func<Task> work, Func<RecoveryNeeded, Task> revoke,
        Func<RecoveryNeeded, Task<bool>> restart, Func<Action, Task> resume, Func<bool> enabled, bool singleEvent = false)
    {
        var unknownFailures = 0;
        var next = work;
        while (true)
        {
            try { await next(); return; }
            catch (RecoveryNeeded interruption)
            {
                await revoke(interruption);
                if (singleEvent)
                    throw new OperationCanceledException(interruption.Message + " · 1개 이벤스만 실행에서는 자동 재개하지 않습니다.", interruption);
                if (interruption.Cause == RecoveryCause.UnknownState) unknownFailures++;
                if (unknownFailures >= 2)
                    throw new InvalidOperationException("스토리 목록 재진입에 성공하지 못한 채 상태 인식 실패가 2회 발생했습니다. 오류로 중지합니다. " + interruption.Message, interruption);
                if (interruption.Cause!=RecoveryCause.AccountSafety && !CanAutoRecover(enabled(), unknownFailures))
                    throw new OperationCanceledException(interruption.Message + " · 자동 재개가 꺼져 있어 중지했습니다.");
                if (!await restart(interruption)) throw new OperationCanceledException("자동 재개 해제됨");
                next = () => resume(() => unknownFailures = 0);
            }
        }
    }
    static async Task<bool> RestartGameForRecovery(RecoveryCause cause)
    {
        Stop.Token.ThrowIfCancellationRequested();
        await Guard();
        var safety=cause==RecoveryCause.AccountSafety;
        if (!AutoResumeEnabled && !safety) { Log("자동 재개 해제됨 · 앱 종료 없이 중지"); return false; }
        if (cause == RecoveryCause.NewData)
        {
            using var verify = await Capture(detectNewData: false);
            if (!IsNewDataNotice(verify)) throw new InvalidOperationException("새 데이터 알림이 사라져 자동 재실행을 중지했습니다.");
        }
        if (!AutoResumeEnabled && !safety) return false;
        RestartAuthorized = true;
        ManualRestartAuthorized=safety;
        try { await Shell($"am force-stop {Package}"); }
        finally { RestartAuthorized = false; ManualRestartAuthorized=false; }
        ReaderTouch = null;
        LastAliveCheck = DateTime.MinValue;
        await Task.Delay(1000, Stop.Token);
        return true;
    }
}
