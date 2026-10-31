using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    const string Package = "com.sega.pjsekai";
    static readonly string Root = AppContext.BaseDirectory;
    internal static string DataRoot => OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SekaiStorySkip") : Root;
    static readonly string Artifacts = Path.Combine(DataRoot, "artifacts");
    static CancellationTokenSource Stop = new();
    static Action<string> LogSink = Console.WriteLine;
    static Action<string>? NotifySink;
    static Action<string>? StatusSink;
    static readonly object LogLock = new();
    static string ShotPath => Path.Combine(Artifacts, $"instance-{Selected?.Index ?? 0}-current.png");
    static void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        LogSink(line);
        lock (LogLock)
        {
            try { File.AppendAllText(Path.Combine(Artifacts, "runner.log"), line + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LogSink("[기록 저장 오류] " + ex.Message); }
        }
    }
    static void LogFailure(Exception error)
    {
        Log(error.Message);
        try { File.WriteAllText(Path.Combine(Artifacts, "last-error.txt"), $"{DateTimeOffset.Now:O}\n{error}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogSink(error.ToString()); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(Artifacts);
        _ = Task.Run(()=>EvidenceArchive.Value.Migrate());
        LoadUserSettings();
        OriginalEmulators.Cancellation = () => Stop.Token;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop.Cancel(); };
        try
        {
            ValidateArguments(args); // Reject all legacy commands before discovering or contacting a device.
            if (args.Length == 0 || args[0] == "gui") { ShowGui(); return 0; }
            return RunCommand(args).GetAwaiter().GetResult();
        }
        catch (Exception ex) { if (ex is OperationCanceledException && Stop.IsCancellationRequested) Log("중지됨"); else LogFailure(ex); return 1; }
    }

    internal static void ValidateArguments(string[] args)
    {
        if (args.Length == 0 || args.SequenceEqual(new[] { "gui", "--window" }) || args.SequenceEqual(new[] { "list" }) || args.SequenceEqual(new[] { "check-safety" })) return;
        if (args.Length == 2 && args[0] == "check-screen" && File.Exists(args[1])) return;
        if (args.Length == 3 && args[0] is "home" or "story" or "filter" or "read-current" or "snap" && args[1] == "--instance" &&
            int.TryParse(args[2], out var index) && index >= 0 && index < (OperatingSystem.IsMacOS() ? 1000 : 5000)) return;
        throw new InvalidOperationException("허용 명령: list | home --instance N | story --instance N | snap --instance N | check-safety | check-screen FILE. 기존 실행 명령은 지원하지 않습니다.");
    }

    static async Task<int> RunCommand(string[] args)
    {
        if (args[0] == "check-safety") { VerifySafety(); return 0; }
        if (args[0] == "check-screen")
        {
            using var screen = new Bitmap(args[1]);
            Log($"NewData={IsNewDataNotice(screen)}, Home={IsAreaMain(screen)}, ColorDialogX={FindColorDialogClose(screen)}, HomeAction={FindHomeAction(screen)?.Name ?? "none"}, StoryAction={FindStoryAction(screen)?.Name ?? "none"}, StoryList={IsMainEventList(screen)}, Percent={await ReadStoryPercent(screen)}, Filter={IsFilterFrame(screen)}, Checks={CheckedFilterBoxes(screen).Count}, Episodes={IsEpisodeList(screen)}, SKIP={string.Join(',', EpisodeBadgeRows(screen, true))}, Unread={string.Join(',', EpisodeBadgeRows(screen, false))}, Locked={string.Join(',', EpisodeLockedRows(screen))}, ReaderAction={FindReaderAction(screen)?.Name ?? "none"}");
            return 0;
        }
        if (args[0] == "list") { Log(JsonSerializer.Serialize(await ListVms(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
        var index = int.Parse(args[2]);
        using var session = await BeginSession(index);
        var memoryChanged = args[0] != "snap" && await PrepareMemoryForRun();
        await RunWithNewDataRecovery(async () =>
        {
            if (args[0] == "read-current") { if (memoryChanged || await EnsureRunning()) await EnterStoryAfterMemoryChange(); await ReadStoryQueue(); }
            else if (args[0] == "filter") { if (memoryChanged || await EnsureRunning()) await EnterStoryAfterMemoryChange(); else await ApplyUnreadFilter(); }
            else if (args[0] == "story") { await EnsureRunning(); await EnterMainEventStories(); await ApplyUnreadFilter(); }
            else if (args[0] == "home") { await EnsureRunning(); await ReturnHome(); }
            else { await ConnectVm(); await Snapshot(ShotPath); }
        }, args[0] == "read-current" ? ReadStoryQueue : null);
        return 0;
    }
}
