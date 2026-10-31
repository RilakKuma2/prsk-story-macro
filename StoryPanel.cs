using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

internal static partial class Program
{
    sealed record StartRequest(int Instance, string Command);
    sealed record SelectRequest(int Instance);
    sealed record SettingsRequest(bool AutoResume);
    sealed record MemoryRequest(int Gigabytes);
    sealed record CreateVmRequest(string Emulator, string Name);
    sealed record EmulatorRequest(string Emulator);
    sealed record LanguageRequest(string Language);
    sealed record GameInstallRequest(int Instance, bool Update = false);
    static bool IsPanelCommand(string command) => command is "read-current" or "restart-read" or "read-one" or "virtual-all";
    sealed partial class StoryPanel : IDisposable
    {
        public string Token { get; } = Guid.NewGuid().ToString("N");
        readonly SemaphoreSlim deviceGate = new(1, 1);
        readonly CancellationTokenSource closing = new();
        readonly ConcurrentQueue<string> logs = new();
        readonly ConcurrentDictionary<int, string> states = new(), errors = new();
        readonly ConcurrentDictionary<int, string> commands = new();
        readonly ConcurrentDictionary<int, string> previewErrors = new();
        volatile Vm[] instances = [];
        volatile int selection = -1;
        volatile string emulatorSelection="";
        volatile bool manualSelection;
        string EmulatorSetting=>Path.Combine(DataRoot,"local","tab.txt");
        volatile bool busy;
        public bool UpdateBusy => busy;
        public bool TryBeginUpdate() => deviceGate.Wait(0);
        public void EndUpdate() => deviceGate.Release();
        Task? worker, monitor;
        long noticeId;
        volatile string? noticeMessage;
        public StoryPanel()
        {
            LogSink = line => { logs.Enqueue(line); while (logs.Count > 300) logs.TryDequeue(out _); };
            NotifySink = message => { noticeMessage = message; Interlocked.Increment(ref noticeId); };
            StatusSink = status => { if (selection >= 0) states[selection] = status; };
        }
        static string Resource(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        public WebApplication BuildServer(string listen="127.0.0.1",int port=0)
        {
            var allowLan=listen=="0.0.0.0";
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = Root });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://{listen}:{port}");
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                if ((!allowLan && context.Request.Host.Host != "127.0.0.1") || (context.Request.Method != "GET" && context.Request.Headers["X-Story-Token"] != Token))
                { context.Response.StatusCode = 403; return; }
                await next(context);
            });
            app.MapGet("/", () => Results.Content(Resource("web.index.html").Replace("__PANEL_TOKEN__", Token), "text/html; charset=utf-8"));
            app.MapGet("/base.css", () => Results.Content(Resource("web.base.css"), "text/css; charset=utf-8"));
            app.MapGet("/i18n.js",()=>Results.Content(Resource("web.i18n.js"),"application/javascript; charset=utf-8"));
            app.MapGet("/api/update", async () =>
            {
                try { return Results.Json(await ReadUpdateInfo()); }
                catch (Exception ex) { return Results.Json(new { error = T("업데이트를 확인하지 못했어요. 인터넷 연결을 확인해 주세요.", "更新を確認できませんでした。接続を確認してください。", "Could not check for updates. Check your connection."), detail = ex.Message }, statusCode: 503); }
            });
            app.MapPost("/api/apply-update", async () =>
            {
                if (OperatingSystem.IsMacOS() && MacAppBundlePath() is { } bundle)
                {
                    if (busy || !TryBeginUpdate()) return Results.Conflict(new { error = T("진행 중인 작업을 먼저 멈춰주세요.", "実行中の処理を先に停止してください。", "Stop the current task first.") });
                    try { await ApplyMacSelfUpdate(bundle); return Results.Ok(); }
                    catch (Exception ex) { EndUpdate(); return Results.BadRequest(new { error = ex.Message }); }
                }
                return Results.BadRequest(new { error = T("자동 설치를 지원하지 않는 환경입니다.", "自動更新に対応していない環境です。", "Self-update is not supported in this environment.") });
            });
            app.MapGet("/app-icon.png",()=>Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream("web.app-icon.png")!,"image/png"));
            app.MapGet("/example/{kind}", (string kind) => kind is "all" or "one" ? Results.File(Path.Combine(Root,"tests","fixtures",kind=="all"?"catalogue-after-completion.png":"top-story-episodes.png"),"image/png") : Results.NotFound());
            app.MapGet("/api/state", (HttpContext context) => Results.Json(new
            {
                listen=ListenState(),listenLocal=!allowLan && context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address),
                language=Language,emulator=emulatorSelection,appPlayers=OriginalEmulators.Kinds(),
                busy, version = CurrentVersion.ToString(), selected = selection, autoResume = AutoResumeEnabled, platform = OperatingSystem.IsMacOS() ? "mac" : "windows", emulators = OriginalEmulators.Installed(), memory = MemoryForUi(selection, instances.Any(vm => vm.Index == selection && vm.Running)),
                instances = instances.Where(vm=>vm.Kind==emulatorSelection).Select(vm => new
                {
                    vm.Index, vm.Name, vm.Running, vm.Kind, vm.Label, selected = vm.Index == selection,
                    state = states.GetValueOrDefault(vm.Index, vm.Running ? "대기" : "꺼짐"), error = errors.GetValueOrDefault(vm.Index),
                    singleEvent = commands.GetValueOrDefault(vm.Index) == "read-one",
                    progress = Progress.GetValueOrDefault(vm.Index), previewError = previewErrors.GetValueOrDefault(vm.Index), frame = CurrentFrames.Read(vm.Index)?.Revision ?? 0
                }), logs = logs.ToArray(), notice = noticeMessage == null ? null : new { id = Interlocked.Read(ref noticeId), message = noticeMessage }
            }));
            app.MapGet("/api/frame/{index:int}", IResult (int index) =>
            {
                if (!instances.Any(v => v.Index == index) || CurrentFrames.Read(index) is not { } frame) return Results.NotFound();
                return Results.Bytes(frame.Png,"image/png");
            });
            app.MapPost("/api/select", (SelectRequest request) =>
            {
                if (busy) return Results.Conflict();
                if (!instances.Any(v => v.Index == request.Instance)) return Results.BadRequest();
                emulatorSelection=instances.Single(v=>v.Index==request.Instance).Kind;
                manualSelection=true;
                selection = request.Instance; return Results.Ok();
            });
            app.MapPost("/api/emulator",async(EmulatorRequest request)=>
            {
                if(busy)return Results.Conflict();
                if(!OriginalEmulators.Kinds().Any(e=>e.Key==request.Emulator))return Results.BadRequest();
                emulatorSelection=request.Emulator;
                manualSelection=false;
                selection=ChooseInstance(instances.Where(vm=>vm.Kind==emulatorSelection).ToArray(),-1);
                Directory.CreateDirectory(Path.GetDirectoryName(EmulatorSetting)!);
                await File.WriteAllTextAsync(EmulatorSetting,emulatorSelection);
                return Results.Ok();
            });
            app.MapPost("/api/language",(LanguageRequest request)=>{try{SetLanguage(request.Language);return Results.Ok();}catch(ArgumentException){return Results.BadRequest();}});
            app.MapPost("/api/settings", async (SettingsRequest request) =>
            {
                await SaveAutoResume(request.AutoResume);
                Log(request.AutoResume ? "자동 재개 켜짐 · 종료/새 데이터는 반복 복구, 상태 인식 실패 2회만 중지 · 스토리 재진입 시 초기화" : "자동 재개 꺼짐");
                return Results.Ok();
            });
            app.MapPost("/api/memory", async (MemoryRequest request) =>
            {
                var memory = MemoryForUi(selection, instances.Any(vm => vm.Index == selection && vm.Running));
                if (memory == null || request.Gigabytes < 3 || request.Gigabytes > memory.MaxGb) return Results.BadRequest();
                await SaveMemoryChoice(request.Gigabytes);
                Log($"RAM {request.Gigabytes}GB 선택 · 다음 실행에 적용");
                return Results.Ok();
            });
            app.MapPost("/api/start", async (StartRequest request) =>
            {
                if (!IsPanelCommand(request.Command) || !instances.Any(v => v.Index == request.Instance)) return Results.BadRequest();
                if (busy || !await deviceGate.WaitAsync(2000)) return Results.Conflict();
                busy = true; selection = request.Instance;
                emulatorSelection=instances.Single(v=>v.Index==selection).Kind;
                Stop.Dispose(); Stop = new();
                states[selection] = "실행 중"; errors.TryRemove(selection, out _);
                commands[selection] = request.Command;
                worker = Task.Run(() => Execute(request));
                return Results.Accepted();
            });
            app.MapPost("/api/stop", () => { RequestStop(); return Results.Ok(); });
            app.MapPost("/api/install-game", async (GameInstallRequest request) =>
            {
                if (!instances.Any(v=>v.Index==request.Instance)) return Results.BadRequest();
                if (busy || !await deviceGate.WaitAsync(0)) return Results.Conflict();
                busy=true;selection=request.Instance;
                emulatorSelection=instances.Single(v=>v.Index==selection).Kind;
                Stop.Dispose();Stop=new();
                commands[selection]="install-game";
                errors.TryRemove(selection,out _);
                states[selection]=request.Update?"게임 업데이트 중":"게임 설치 중";
                worker=Task.Run(async () =>
                {
                    try
                    {
                        using var session=await BeginSession(request.Instance);
                        await InstallGameOnly(request.Update,Log);
                        states[request.Instance]=request.Update?"게임 업데이트 완료":"게임 설치 완료";
                    }
                    catch (OperationCanceledException) when (Stop.IsCancellationRequested) { states[request.Instance]="중지됨";Log("설치를 중지했습니다."); }
                    catch (Exception ex) { states[request.Instance]="오류";errors[request.Instance]=ex.Message;LogFailure(ex); }
                    finally { busy=false;deviceGate.Release(); }
                });
                return Results.Accepted();
            });
            app.MapPost("/api/listen", async (HttpContext context,ListenRequest request) =>
            {
                if(allowLan || context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address)) return Results.StatusCode(403);
                try { await SetListen(request.Enabled,request.Port);return Results.Json(ListenState()); }
                catch(Exception ex){return Results.BadRequest(new {error=ex.Message});}
            });
            app.MapGet("/api/create-info/{key}", async (string key) =>
            {
                try { return Results.Json(new { disk=await OriginalEmulators.DiskForCreation(key), memoryGb=NewVmMemoryGb(ReadHostMemory()) }); }
                catch (Exception ex) { return Results.BadRequest(new { error=ex.Message }); }
            });
            app.MapPost("/api/create-vm", async (CreateVmRequest request) =>
            {
                if (busy || !await deviceGate.WaitAsync(0)) return Results.Conflict();
                busy = true; Stop.Dispose(); Stop = new();
                try
                {
                    var memoryGb=NewVmMemoryGb(ReadHostMemory());
                    Log($"새 VM 생성 · 디스크 필요량 14GB, RAM {memoryGb}GB 자동 선택");
                    selection = await OriginalEmulators.Create(request.Emulator, request.Name, memoryGb);
                    emulatorSelection=request.Emulator;
                    manualSelection=true;
                    instances = (await ListVms()).ToArray();
                    Log("VM 생성 완료 · 게임 설치와 로그인 후 시작하세요.");
                    return Results.Ok();
                }
                catch (Exception ex) { LogFailure(ex); return Results.BadRequest(new { error = ex.Message }); }
                finally { busy = false; deviceGate.Release(); }
            });
            return app;
        }
        async Task Execute(StartRequest request)
        {
            try
            {
                using var session = await BeginSession(request.Instance);
                if (request.Command == "read-one")
                {
                    // Keep the selected event open; apply RAM changes on the next full/reboot start.
                    await ReadSingleEvent();
                }
                else
                {
                    var memoryChanged = await PrepareMemoryForRun();
                    if(request.Command=="virtual-all")
                    {
                        await RunWithNewDataRecovery(async () =>
                        {
                            if(memoryChanged || await EnsureRunning()) await EnterStoryAfterMemoryChange(virtualOnly:true);
                            await ReadVirtualQueue();
                        },ReadVirtualQueue,virtualLive:true);
                    }
                    else await RunWithNewDataRecovery(async () =>
                    {
                        if (request.Command == "restart-read") await RestartGameAndRead();
                        else { if (memoryChanged || await EnsureRunning()) await EnterStoryAfterMemoryChange(); await ReadStoryQueue(); }
                    }, ReadStoryQueue);
                }
                states[request.Instance] = "완료";
            }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { states[request.Instance] = "중지됨"; Log("중지됨 · 게임 상태 유지"); }
            catch (Exception ex) { states[request.Instance] = "오류"; errors[request.Instance] = ex.Message; LogFailure(ex); }
            finally
            {
                try { if (Selected != null && ReaderTouch != null) await SetReaderLease(false); } catch { }
                busy = false; deviceGate.Release();
            }
        }
        public void StartMonitor() => monitor = Task.Run(async () =>
        {
            while (!closing.IsCancellationRequested)
            {
                // Read-only previews have their own explicit VM/serial and never replace the active input target.
                try { instances = (await ListVms()).ToArray();CurrentFrames.Keep(instances.Select(vm=>vm.Index)); }
                catch (Exception ex) when (!closing.IsCancellationRequested) { Log("인스턴스 조회: " + ex.Message); }
                if (!busy && await deviceGate.WaitAsync(0))
                {
                    try
                    {
                        if (Stop.IsCancellationRequested) { Stop.Dispose(); Stop = new(); }
                        if(emulatorSelection=="")
                        {
                            string? saved=null;try{if(File.Exists(EmulatorSetting))saved=await File.ReadAllTextAsync(EmulatorSetting);}catch{ }
                            var running=instances.Where(vm=>vm.Running).ToList();
                            emulatorSelection=running.Count==1?running[0].Kind:OriginalEmulators.Kinds().Any(e=>e.Key==saved)?saved!:OperatingSystem.IsMacOS()?"bluestacks":"mumu";
                        }
                        var visible=instances.Where(vm=>vm.Kind==emulatorSelection).ToArray();
                        if(!manualSelection || !visible.Any(vm=>vm.Index==selection)) selection=ChooseInstance(visible,selection);
                        if (instances.SingleOrDefault(v => v.Index == selection) is { } vm)
                        {
                            Selected = vm; await RefreshMemoryInfo();
                            if (vm.Running && CurrentFrames.Read(vm.Index) is { } frame)
                            {
                                using var stream = new MemoryStream(frame.Png);
                                using var screen = new Bitmap(stream);
                                await ObserveStoryProgress(screen);
                            }
                        }
                    }
                    catch (Exception ex) when (!closing.IsCancellationRequested)
                    { if (!states.ContainsKey(selection)) states[selection] = "연결 대기: " + ex.Message; }
                    finally { deviceGate.Release(); }
                }
                foreach (var vm in instances.Where(vm => vm.Running && vm.Kind==emulatorSelection))
                {
                    if (closing.IsCancellationRequested) break;

                    try
                    {
                        var bytes = await OriginalEmulators.Preview(vm.Index);
                        closing.Token.ThrowIfCancellationRequested();
                        if (bytes.Length < 8 || bytes[0] != 137 || bytes[1] != 80) throw new InvalidDataException("캡처 응답이 PNG가 아닙니다.");
                        if(busy && vm.Index==selection && WorkflowActive)
                        {
                            using var stream=new MemoryStream(bytes);using var screen=new Bitmap(stream);
                            if(ProtectedScreenReason(screen) is { } reason){PreserveProtectionFrame(bytes,vm.Index,reason);Interlocked.CompareExchange(ref PendingProtectionReason,reason,null);}
                            continue;
                        }
                        CurrentFrames.Publish(vm.Index,bytes);
                        previewErrors.TryRemove(vm.Index, out _);
                    }
                    catch (Exception ex) when (!closing.IsCancellationRequested) { previewErrors[vm.Index] = ex.Message; }
                }
                try { await Task.Delay(3000, closing.Token); } catch (OperationCanceledException) { break; }
            }
        });
        public void RequestStop() { if (busy) { states[selection] = "중지 중"; Stop.Cancel(); } }
        public async Task Shutdown()
        {
            closing.Cancel(); RequestStop();
            await CloseListen();
            try { await Task.WhenAll(new[] { worker, monitor }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(6)); } catch { }
            CurrentFrames.Clear();
        }
        public void Dispose() { closing.Dispose(); LogSink = Console.WriteLine; NotifySink = null; StatusSink = null; }
    }
}
