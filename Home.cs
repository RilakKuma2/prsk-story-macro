using System.Drawing;

internal static partial class Program
{
    // From Loops.cs ReturnHome: title -> login bonuses/notices -> area home. No account loop or tutorial path.
    static async Task<string> LeaveGoogleSignIn(string activity)
    {
        if(!IsGoogleSignIn(activity))return activity;
        if(IsGoogleSignIn(await Foreground()))
        {
            AuthorizedInput="input keyevent 4";
            try{await Shell(AuthorizedInput);}finally{AuthorizedInput=null;}
            Log("Google 로그인 창 뒤로가기 · 기존 실행 흐름으로 복귀");
            await Task.Delay(1500,Stop.Token);
        }
        return await Foreground();
    }
    static async Task ReturnHome()
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var lastInput = DateTime.MinValue;
        DateTime? foreignSince = null;
        while (DateTime.UtcNow < deadline)
        {
            await Guard();
            var activity = await Foreground();
            if (IsGoogleSignIn(activity))
            {
                foreignSince = null;
                await LeaveGoogleSignIn(activity);continue;
            }
            if (!activity.Contains(Package + "/"))
            {
                foreignSince ??= DateTime.UtcNow;
                await CheckGameLiveness();
                if (DateTime.UtcNow - foreignSince > TimeSpan.FromSeconds(30))
                    throw new RecoveryNeeded(RecoveryCause.UnknownState, "게임 또는 로그인 화면을 30초 동안 확인하지 못했습니다.");
                // The original ReturnHome re-evaluates the activity every pass. A loading activity
                // can become Google's sign-in screen between polls; let the next pass handle it.
                await Task.Delay(1500, Stop.Token);
                continue;
            }
            foreignSince = null;
            using var screen = await Capture();
            if (await DismissNotice(screen)) continue;
            if (IsAreaMain(screen) && FindColorDialogClose(screen) == null)
            {
                await Task.Delay(600, Stop.Token);
                using var verify = await Capture();
                if (IsAreaMain(verify) && FindColorDialogClose(verify) == null)
                {
                    CopyEvidence(ShotPath, Path.Combine(Artifacts, $"instance-{Selected!.Index}-main.png"));
                    Log("메인화면 도착");
                    return;
                }
            }
            var action = FindHomeAction(screen);
            if (action != null && DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(3))
            {
                if (await GuardedTap(action)) lastInput = DateTime.UtcNow;
            }
            await Task.Delay(1500, Stop.Token);
        }
        throw new RecoveryNeeded(RecoveryCause.UnknownState, "5분 안에 메인화면을 확인하지 못했습니다.");
    }

    static async Task<bool> GuardedTap(HomeAction expected, Func<Bitmap, HomeAction?>? recognize = null)
    {
        await Task.Delay(350, Stop.Token);
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) return false;
        using var verify = await Capture();
        if ((recognize ?? FindHomeAction)(verify) != expected) return false;
        Stop.Token.ThrowIfCancellationRequested();
        AuthorizedInput = $"input tap {expected.X} {expected.Y}";
        try { await Shell(AuthorizedInput); }
        finally { AuthorizedInput = null; }
        Log($"화면 재확인 후 {expected.Name}: ({expected.X}, {expected.Y})");
        return true;
    }

    // Same binary ADB screenshot path and 960x540 validation as the original Snapshot implementation.
    static DateTimeOffset LastCaptureCompletedAt;
    static async Task<byte[]> Snapshot(string path, Func<Task<byte[]>>? capture = null)
    {
        // Original Snapshot behavior: a transient capture/file lock is retried with a
        // fresh ADB frame, at most three times and 700 ms apart. Never reuse a stale frame.
        for (var attempt = 0; ; attempt++)
        {
            Stop.Token.ThrowIfCancellationRequested();
            try
            {
                var bytes = await (capture?.Invoke() ?? RunBytes(Adb, ["-s", Serial, "exec-out", "screencap", "-p"]));
                LastCaptureCompletedAt = DateTimeOffset.Now;
                using var stream = new MemoryStream(bytes);
                using var decoded = new Bitmap(stream);
                if (decoded.Width != 960 || decoded.Height != 540) throw new InvalidOperationException($"화면이 {decoded.Width}x{decoded.Height}입니다. 960x540이 아니므로 입력하지 않습니다.");
                _ = decoded.GetPixel(959, 539);
                await File.WriteAllBytesAsync(path + ".pending", bytes, Stop.Token);
                File.Move(path + ".pending", path, true);
                return bytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !Stop.IsCancellationRequested)
            {
                if (attempt >= 2) throw new IOException($"캡처 저장을 3회 시도했지만 실패했습니다: {path} · {ex.Message}", ex);
                Log($"캡처 재시도 {attempt + 1}/3 · {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(700, Stop.Token);
            }
        }
    }
    static async Task<Bitmap> Capture(bool detectNewData = true)
    {
        CheckPendingProtection();
        if (detectNewData && WorkflowActive) await CheckGameLiveness();
        var bytes = await Snapshot(ShotPath);
        using var stream = new MemoryStream(bytes);
        using var bitmap = new Bitmap(stream);
        if(WorkflowActive && ProtectedScreenReason(bitmap) is { } protectedReason)
        {
            PreserveProtectionFrame(bytes,Selected!.Index,protectedReason);
            throw new RecoveryNeeded(RecoveryCause.AccountSafety,protectedReason+" · 터치 없이 게임 재실행");
        }
        if (detectNewData && WorkflowActive && IsNewDataNotice(bitmap))
        {
            PreserveNewDataNotice(bitmap);
            throw new NewDataDetected();
        }
        await ObserveStoryProgress(bitmap);
        return new Bitmap(bitmap);
    }
}
