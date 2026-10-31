using System.Diagnostics;
using System.Text.Json;
using CodexAutoContinue.Core;
using CodexAutoContinue.Tests;

if (args.FirstOrDefault() == "--echo-args") { Console.WriteLine(JsonSerializer.Serialize(args.Skip(1))); return; }
if (args.FirstOrDefault() == "--idle") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
if (args.FirstOrDefault() == "--job-parent")
{
    Console.ReadLine();
    var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
    start.ArgumentList.Add("--idle");
    using var child=Process.Start(start)!; Console.WriteLine(child.Id); Console.Out.Flush(); Console.ReadLine(); return;
}
if (args.FirstOrDefault() == "--integration")
{
    await using var server=new AppServerTransport();
    await server.ConnectAsync(Path.GetFullPath(args[1]));
    var serverPid=server.ProcessId;
    var account=await server.CallAsync("account/read",new { refreshToken=false });
    Check(account.Obj("account").Str("type") == "chatgpt","ChatGPT account");
    var rates=await server.CallAsync("account/rateLimits/read",new { });
    Check(RateLimits.Parse(rates).Windows.Count>0,"limits decode");
    var loaded=await server.CallAsync("thread/loaded/list",new { limit=100 });
    Check(loaded.Array("data").Length == 0,"private server starts without loaded writers");
    Console.WriteLine("thread/loaded/list: PASS; private server has no loaded threads");
    var sessions=await server.CallAsync("thread/list",new { limit=100,sourceKinds=new[] {"cli","vscode","exec","appServer"},modelProviders=Array.Empty<string>(),sortKey="updated_at" });
    var data=sessions.Array("data");
    if (data.FirstOrDefault() is var first && first.ValueKind == JsonValueKind.Object)
    {
        var id=first.Str("id");
        var read=await server.CallAsync("thread/read",new { threadId=id,includeTurns=true });
        Check(read.Obj("thread").Str("id") == id,"exact thread id read");
        Console.WriteLine("thread/read: PASS; returned turn count="+read.Obj("thread").Array("turns").Length);
    }
    await server.DisposeAsync(); Check(!server.IsConnected,"server stopped");
    bool processAlive=false; try { using var exited=Process.GetProcessById(serverPid!.Value); processAlive=!exited.HasExited; } catch (ArgumentException) { }
    Check(!processAlive,"native app-server process exited");
    Console.WriteLine($"INTEGRATION PASS: managed ChatGPT account, {RateLimits.Parse(rates).Windows.Count} windows, {data.Length} local sessions, own loaded list, exact ID read, child shutdown. No model turns."); return;
}

var tests=new List<(string Name,Func<Task> Run)>();
void Test(string name,Func<Task> run) => tests.Add((name,run));
void Sync(string name,Action run) => Test(name,()=> { run(); return Task.CompletedTask; });

Sync("one exhausted window waits only for its reset",()=>
{ var d=RateLimits.Parse(FakeServer.RateResponse(100,20,reset1:100,reset2:200,ordinary:false)).Evaluate(); Check(d.Availability == Availability.Blocked && d.WaitUntil == DateTimeOffset.FromUnixTimeSeconds(100),"one window"); });
Sync("both exhausted windows wait for latest reset",()=>
{ var d=RateLimits.Parse(FakeServer.RateResponse(100,100,reset1:100,reset2:200,ordinary:false)).Evaluate(); Check(d.WaitUntil == DateTimeOffset.FromUnixTimeSeconds(200),"all blocking windows"); });
Sync("weekly only exhaustion",()=>
{ var d=RateLimits.Parse(FakeServer.RateResponse(10,100,reset1:100,reset2:200,ordinary:false)).Evaluate(); Check(d.WaitUntil == DateTimeOffset.FromUnixTimeSeconds(200),"weekly window"); });
Sync("missing reset remains blocked",()=>
{ var d=RateLimits.Parse(FakeServer.RateResponse(100,5,reset1:null,ordinary:false)).Evaluate(); Check(d.UnknownReset && d.WaitUntil is null,"unknown reset"); });
Sync("null ordinaryUsageAllowed never proves recovery",()=>
{ Check(RateLimits.Parse(FakeServer.RateResponse(0,0,ordinary:null)).Evaluate().Availability == Availability.Unknown,"permission unknown"); });
Sync("ordinaryUsageAllowed false prevents spending extra credits",()=>
{ Check(RateLimits.Parse(FakeServer.RateResponse(0,0,ordinary:false)).Evaluate().Availability == Availability.Blocked,"backend permission"); });
Sync("legacy full quota response supported",()=>
{ Check(RateLimits.Parse(FakeServer.RateResponse(0,20,includeOrdinary:false)).Evaluate().Availability == Availability.Available,"legacy response"); });
Sync("empty quota response unknown",()=>
{ Check(RateLimits.Parse(JsonExt.ToJson(new { })).Evaluate().Availability == Availability.Unknown,"unknown"); });
Sync("backend spend and credits depletion block",()=>
{ var rates=JsonExt.ToJson(new { ordinaryUsageAllowed=true,rateLimits=new { limitId="codex",primary=new { usedPercent=20 },spendControlReached=true } }); Check(RateLimits.Parse(rates).Evaluate().Availability == Availability.Blocked,"spend-control"); });
Sync("unmapped depleted model quota is uncertain",()=>
{ var rates=JsonDocument.Parse("{\"ordinaryUsageAllowed\":true,\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":20}},\"other-model\":{\"primary\":{\"usedPercent\":100}}}}").RootElement; Check(RateLimits.Parse(rates,"chosen-model").Evaluate().Availability == Availability.Unknown,"unknown model bucket"); });
Sync("documented usage error variants",()=>
{ foreach (var code in new[] {"usageLimitExceeded","UsageLimitExceeded"}) Check(ErrorClassifier.Classify(JsonExt.ToJson(new { codexErrorInfo=code })) == RunState.WaitingLimit,"usage code"); });
Sync("authorization is separate from quota",()=>
{ Check(ErrorClassifier.Classify(JsonExt.ToJson(new { codexErrorInfo="unauthorized" })) == RunState.NeedsLogin,"auth"); Check(ErrorClassifier.Classify(JsonDocument.Parse("{\"codexErrorInfo\":{\"httpConnectionFailed\":{\"httpStatusCode\":401}}}").RootElement) == RunState.NeedsLogin,"http401"); });
Sync("network disconnect is separate from quota",()=>
{ Check(ErrorClassifier.Classify(JsonDocument.Parse("{\"codexErrorInfo\":{\"httpConnectionFailed\":{\"httpStatusCode\":null}}}").RootElement) == RunState.Offline,"offline"); });
Sync("service and generic rate errors never trigger quota retry",()=>
{ foreach (var code in new[] {"rateLimitExceeded","serverOverloaded","internalServerError","other","unknownNewError"}) Check(ErrorClassifier.Classify(JsonExt.ToJson(new { codexErrorInfo=code })) == RunState.Error,"not quota"); });
Test("normal completion stops automatic prompts",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Complete("completed"); await r.Flush(); r.Clock.Advance(10000); await r.Controller.TickAsync(true); Check(r.Controller.Data.Run.State == RunState.Completed && r.Server.Count("turn/start") == 1,"one normal turn"); });
Test("100 percent does not kill active task",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Rates=FakeServer.RateResponse(100,100,ordinary:false); await r.Controller.CheckLimitsAsync(); Check(r.Controller.Data.Run.State == RunState.Running && r.Server.Count("turn/interrupt") == 0,"active preserved"); });
Test("quota failure waits then resumes exact thread once",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); Check(r.Controller.Data.Run.State == RunState.WaitingLimit,"waiting"); r.Server.Rates=FakeServer.RateResponse(0,20); r.Clock.Advance(61); await r.Controller.TickAsync(); Check(r.Server.Count("turn/start") == 2,"one retry"); Check(r.Server.Calls.Last(x=>x.Method == "turn/start").Params.Str("threadId") == FakeServer.ThreadId,"exact id"); r.Server.Complete("completed"); await r.Flush(); await r.Controller.TickAsync(true); Check(r.Server.Count("turn/start") == 2,"no chain"); });
Test("renewed exhaustion repeats waiting cycle",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.Rates=FakeServer.RateResponse(0,0); await r.Controller.TickAsync(true); await r.QuotaStop(); Check(r.Controller.Data.Run.State == RunState.WaitingLimit && r.Server.Count("turn/start") == 2,"second quota stop"); });
Test("wake forces fresh quota read before recovery",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); var before=r.Server.Count("account/rateLimits/read"); r.Clock.Advance(2); await r.Controller.TickAsync(); Check(r.Server.Count("account/rateLimits/read") == before,"scheduled wait"); r.Server.Rates=FakeServer.RateResponse(100,100,ordinary:false); await r.Controller.TickAsync(true); Check(r.Server.Count("account/rateLimits/read")>before && r.Server.Count("turn/start") == 1,"wake fresh blocked"); });
Test("missing reset uses capped backoff and no model probe",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Rates=FakeServer.RateResponse(100,0,reset1:null,ordinary:false); r.Server.Complete("failed","usageLimitExceeded"); await r.Flush(); Check(r.Controller.Data.Run.NextCheck>=r.Clock.UtcNow.AddSeconds(120),"rare retry"); for(int i=0;i<8;i++) { r.Clock.Advance(2000); await r.Controller.TickAsync(); } Check(r.Server.Count("turn/start") == 1 && r.Controller.Data.Run.NextCheck<=r.Clock.UtcNow.AddSeconds(1800),"bounded no probe"); });
Test("waiting restores after application restart",async ()=>
{ var store=new MemoryStore(); JsonElement[] turns; await using (var a=new Rig(store)) { await a.Connect(); await a.Start(); await a.QuotaStop(); turns=a.Server.Turns.ToArray(); } var fake=new FakeServer { Rates=FakeServer.RateResponse(100,20,ordinary:false) }; fake.Turns.AddRange(turns); await using var b=new Rig(store,fake); await b.Connect(); Check(b.Controller.Data.Run.State == RunState.WaitingLimit && b.Controller.Data.Run.ThreadId == FakeServer.ThreadId,"restored ID"); fake.Rates=FakeServer.RateResponse(0,20); await b.Controller.TickAsync(true); Check(fake.Count("turn/start") == 1,"single restored retry"); });
Test("disabled restore does not run",async ()=>
{ var store=new MemoryStore(); var d=store.Load(); d.Settings.RestoreWaiting=false; d.Run=new() { ThreadId=FakeServer.ThreadId,State=RunState.WaitingLimit,AutoContinue=true,Owned=true }; store.Save(d); await using var r=new Rig(store); await r.Connect(); await r.Controller.TickAsync(true); Check(r.Controller.Data.Run.State == RunState.Paused && r.Server.Count("turn/start") == 0,"no restoration"); });
Test("user stop persists and never restores",async ()=>
{ var store=new MemoryStore(); await using (var a=new Rig(store)) { await a.Connect(); await a.Start(); await a.QuotaStop(); await a.Controller.StopAsync(); } await using var b=new Rig(store); await b.Connect(); await b.Controller.TickAsync(true); Check(b.Controller.Data.Run.UserStopped && b.Server.Count("turn/start") == 0,"user stop wins"); });
Test("pause interrupts and blocks quota retry",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.Controller.PauseAsync(); await r.Flush(); await r.Controller.TickAsync(true); Check(r.Controller.Data.Run.State == RunState.Paused && r.Server.Count("turn/interrupt") == 1 && r.Server.Count("turn/start") == 1,"paused"); });
Test("no concurrent duplicate start",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await MustThrow(()=>r.Controller.ContinueNowAsync()); Check(r.Server.Count("turn/start") == 1,"duplicate rejected"); });
Test("cross-process lease rejects task takeover",async ()=>
{ await using var r=new Rig(); await r.Connect(); r.Lease.Throw=true; await MustThrow(()=>r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true)); Check(r.Server.Count("turn/start") == 0,"lease protected"); });
Test("handoff confirmation mandatory even when notLoaded",async ()=>
{ await using var r=new Rig(); await r.Connect(); await MustThrow(()=>r.Controller.TakeOverAsync(FakeServer.ThreadId,false,true)); Check(r.Server.Count("thread/resume") == 0,"no unconfirmed takeover"); });
Test("handoff resumes VS Code session exact ID",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true); Check(r.Server.Count("turn/start") == 1 && r.Controller.Data.Run.ThreadId == FakeServer.ThreadId,"takeover"); });
Test("handoff resumes once even when duplicate resume rejects active writer",async ()=>
{ await using var r=new Rig(server:new() { RejectRepeatedResume=true }); await r.Connect(); await r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true); Check(r.Server.Count("thread/resume") == 1 && r.Server.Count("turn/start") == 1 && r.Controller.Data.Run.HandoffAcquired,"one resume before turn"); });
Test("quota recovery reuses owned writer in same server",async ()=>
{ await using var r=new Rig(server:new() { RejectRepeatedResume=true }); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.Rates=FakeServer.RateResponse(0,0); await r.Controller.TickAsync(true); Check(r.Server.Count("thread/resume") == 0 && r.Server.Count("turn/start") == 2,"reuse new-thread writer"); });
Test("external writer conflict pauses and disables false ownership",async ()=>
{
    await using var r=new Rig(server:new() { ResumeFailure=new RpcException(-32600,"thread "+FakeServer.ThreadId+" already has an active writer",default) });
    await r.Connect();
    try { await r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true); throw new Exception("Expected conflict"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("другим клиентом") && !e.Message.Contains("active writer"),"Russian explanation"); }
    Check(!r.Controller.Data.Run.Owned && !r.Controller.Data.Run.HandoffAcquired && !r.Controller.Data.Run.AutoContinue && r.Controller.Data.Run.State == RunState.Paused,"ownership not claimed");
    await MustThrow(r.Controller.ContinueNowAsync); await MustThrow(r.Controller.EnableAutoAsync); await r.Controller.TickAsync(true);
    Check(r.Server.Count("turn/start") == 0 && r.Server.Count("thread/resume") == 1,"no blind retries");
    r.Server.ResumeFailure=null; await r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true);
    Check(r.Server.Count("turn/start") == 1 && r.Controller.Data.Run.Owned,"explicit retry after release works");
});
Test("lost resume response is recovered from own loaded list",async ()=>
{ await using var r=new Rig(server:new() { ResumeFailure=new TimeoutException("lost resume ack"),AcceptResumeBeforeFailure=true,RejectRepeatedResume=true }); await r.Connect(); await MustThrow(()=>r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true)); Check(!r.Controller.Data.Run.Owned,"unconfirmed ownership"); r.Server.ResumeFailure=null; await r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true); Check(r.Server.Count("thread/resume") == 1 && r.Server.Count("turn/start") == 1,"own loaded writer reused"); });
Test("legacy unconfirmed handoff cannot restore automatic execution",async ()=>
{ var store=new MemoryStore(); var d=store.Load(); d.Run=new() { ThreadId=FakeServer.ThreadId,Owned=true,HandoffAcquired=false,AutoContinue=true,State=RunState.Ready }; store.Save(d); await using var r=new Rig(store); await r.Connect(); Check(!r.Controller.Data.Run.Owned && !r.Controller.Data.Run.AutoContinue && r.Controller.Data.Run.State == RunState.Paused,"legacy false ownership cleared"); await MustThrow(r.Controller.ContinueNowAsync); Check(r.Server.Count("turn/start") == 0,"no legacy execution"); });
Test("thread closed notification invalidates loaded writer cache",async ()=>
{ await using var r=new Rig(server:new() { RejectRepeatedResume=true }); await r.Connect(); await r.Start(); r.Server.Complete("completed"); await r.Flush(); r.Server.Emit("thread/closed",new { threadId=FakeServer.ThreadId }); await r.Flush(); await r.Controller.ContinueNowAsync(); Check(r.Server.Count("thread/resume") == 1 && r.Server.Count("turn/start") == 2,"fresh resume after unload"); });
Test("writer conflict after restart stops restored waiting",async ()=>
{ var store=new MemoryStore(); JsonElement[] turns; await using (var a=new Rig(store)) { await a.Connect(); await a.Controller.TakeOverAsync(FakeServer.ThreadId,true,true); await a.QuotaStop(); turns=a.Server.Turns.ToArray(); } var fake=new FakeServer { ResumeFailure=new RpcException(-32600,"thread already has an active writer",default) }; fake.Turns.AddRange(turns); await using var b=new Rig(store,fake); await b.Connect(); await b.Controller.TickAsync(true); Check(!b.Controller.Data.Run.Owned && !b.Controller.Data.Run.AutoContinue && b.Controller.Data.Run.State == RunState.Paused && fake.Count("turn/start") == 0,"restored ownership conflict blocked"); });
Test("incomplete external history blocks takeover",async ()=>
{ await using var r=new Rig(); await r.Connect(); r.Server.Turns.Add(JsonExt.ToJson(new { id="external",status="inProgress" })); await MustThrow(()=>r.Controller.TakeOverAsync(FakeServer.ThreadId,true,true)); Check(r.Server.Count("turn/start") == 0,"history unsafe"); });
Test("history changed during waiting requires another handoff",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.Turns.Add(JsonExt.ToJson(new { id="other-client",status="completed" })); r.Server.Rates=FakeServer.RateResponse(0,0); await r.Controller.TickAsync(true); Check(r.Server.Count("turn/start") == 1 && r.Controller.Data.Run.State == RunState.Paused,"external history protected"); });
Test("lost start acknowledgement never retries blindly",async ()=>
{ await using var r=new Rig(); await r.Connect(); r.Server.TurnFailure=new TimeoutException("lost ack"); r.Server.AcceptBeforeFailure=true; await MustThrow(r.Start); Check(r.Controller.Data.Run.StartUncertain,"pending intent"); await MustThrow(()=>r.Controller.ContinueNowAsync()); await r.Controller.ReconcileAsync(); Check(r.Server.Count("turn/start") == 1 && r.Controller.Data.Run.StartUncertain,"blocked in-progress history"); });
Test("lost acknowledgement reconciles completed history",async ()=>
{ await using var r=new Rig(); await r.Connect(); r.Server.TurnFailure=new TimeoutException("lost ack"); r.Server.AcceptBeforeFailure=true; await MustThrow(r.Start); var old=r.Server.Turns.Last(); r.Server.Turns[^1]=JsonExt.ToJson(new { id=old.Str("id"),status="completed",items=old.Array("items"),error=(object?)null }); await r.Controller.ReconcileAsync(); Check(r.Controller.Data.Run.State == RunState.Completed && !r.Controller.Data.Run.StartUncertain && r.Server.Count("turn/start") == 1,"completion from history"); });
Test("unrelated history never resolves lost acknowledgement",async ()=>
{ await using var r=new Rig(); await r.Connect(); r.Server.TurnFailure=new TimeoutException("lost ack"); await MustThrow(r.Start); r.Server.Turns.Add(JsonExt.ToJson(new { id="unrelated-turn",status="failed",error=new { codexErrorInfo="usageLimitExceeded" } })); await r.Controller.ReconcileAsync(); Check(r.Controller.Data.Run.StartUncertain && r.Controller.Data.Run.State == RunState.Paused,"no unrelated recovery"); });
Test("initial exhausted account waits before creating thread",async ()=>
{ var store=new MemoryStore(); await using (var a=new Rig(store,new() { Rates=FakeServer.RateResponse(100,20,ordinary:false) })) { await a.Connect(); await a.Start(); Check(a.Server.Count("thread/start") == 0 && a.Controller.Data.Run.ThreadId is null,"no unpersisted empty thread"); } await using var b=new Rig(store); await b.Connect(); await b.Controller.TickAsync(true); Check(b.Server.Count("thread/start") == 1 && b.Server.Count("turn/start") == 1,"initial waiting restored"); });
Test("unused own thread metadata restores without nonexistent rollout",async ()=>
{ var store=new MemoryStore(); var saved=store.Load(); saved.Run=new() { Owned=true,CreatedByApp=true,HasSubmittedTurn=false,State=RunState.WaitingLimit,ThreadId=FakeServer.ThreadId,OriginalPrompt="original",ProjectPath=Path.GetTempPath(),AutoContinue=true }; store.Save(saved); await using var r=new Rig(store); await r.Connect(); Check(r.Controller.Data.Run.ThreadId is null && r.Server.Count("thread/read") == 0,"unused metadata discarded"); await r.Controller.TickAsync(true); Check(r.Server.Count("thread/start") == 1 && r.Server.Count("turn/start") == 1,"first actual turn once"); });
Test("authorization failure does not auto-resume",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Complete("failed","unauthorized"); await r.Flush(); await r.Controller.TickAsync(true); Check(r.Controller.Data.Run.State == RunState.NeedsLogin && r.Server.Count("turn/start") == 1,"auth distinct"); });
Test("network loss during quota polling keeps waiting cause",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.RateFailure=new TimeoutException("network timeout"); await r.Controller.TickAsync(true); Check(r.Controller.Data.Run.State == RunState.WaitingLimit && r.Controller.Data.Run.Detail.Contains("Нет интернета") && r.Server.Count("turn/start") == 1,"no model probe on network"); });
Test("auth loss during waiting uses rare read and restores waiting cause",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.AuthType="apiKey"; await r.Controller.TickAsync(true); var before=r.Server.Count("account/read"); await r.Controller.TickAsync(); Check(r.Server.Count("account/read") == before && r.Controller.Data.Run.State == RunState.WaitingLimit,"auth backoff"); r.Server.AuthType="chatgpt"; r.Server.Rates=FakeServer.RateResponse(0,20); r.Clock.Advance(121); await r.Controller.TickAsync(); Check(r.Server.Count("turn/start") == 2,"normal account recovery"); });
Test("local server exit during waiting reconnects without model probe",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.BreakConnection(); await r.Flush(); Check(r.Controller.Data.Run.State == RunState.WaitingLimit,"waiting cause retained"); await r.Controller.TickAsync(true); Check(r.Server.IsConnected && r.Server.Count("turn/start") == 1,"blocked after reconnect"); r.Server.Rates=FakeServer.RateResponse(0,20); await r.Controller.TickAsync(true); Check(r.Server.Count("turn/start") == 2,"recovered after fresh limits"); });
Test("permission request waits and responds once",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Ask(); await r.Flush(); Check(r.Controller.Data.Run.State == RunState.AwaitingApproval,"approval wait"); await r.Controller.TickAsync(true); Check(r.Server.Count("turn/start") == 1,"no retry pending permission"); var req=r.Controller.PendingRequests.Single(); await r.Controller.RespondAsync(req,new { decision="decline" }); await MustThrow(()=>r.Controller.RespondAsync(req,new { decision="accept" })); Check(r.Server.Responses.Count == 1 && r.Controller.Data.Run.State == RunState.Running,"response once"); });
Test("repeated approval request is deduplicated",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Ask(); r.Server.Ask(); await r.Flush(); Check(r.Controller.PendingRequests.Count == 1,"same request id"); r.Server.Emit("serverRequest/resolved",new { threadId=FakeServer.ThreadId,requestId=1 }); await r.Flush(); Check(r.Controller.PendingRequests.Count == 0,"resolved"); });
Test("unknown server request is explicitly rejected",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Ask("unknown/request"); await r.Flush(); Check(r.Server.Responses.Count == 1 && r.Controller.PendingRequests.Count == 0,"rejected unsupported"); });
Test("permissions and questions wait for an explicit response",async ()=>
{ foreach(var method in new[] {"item/fileChange/requestApproval","item/permissions/requestApproval","item/tool/requestUserInput","mcpServer/elicitation/request"}) { await using var r=new Rig(); await r.Connect(); await r.Start(); r.Server.Ask(method); await r.Flush(); Check(r.Controller.Data.Run.State == RunState.AwaitingApproval && r.Server.Responses.Count == 0,"no auto answer for "+method); } });
Test("duplicate and unknown notifications cause no new turn",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); var turn=r.Server.Turns.Last(); r.Server.Emit("turn/completed",new { threadId=FakeServer.ThreadId,turn }); r.Server.Emit("future/notification",new { threadId=FakeServer.ThreadId }); r.Server.Emit("future/notification",new { threadId=FakeServer.ThreadId }); await r.Flush(); Check(r.Controller.Data.Run.BaselineTurnCount == 1 && r.Server.Count("turn/start") == 1,"duplicates inert"); });
Test("sparse rate notification never proves recovery",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); await r.QuotaStop(); r.Server.Emit("account/rateLimits/updated",new { rateLimits=new { primary=new { usedPercent=0 } } }); await r.Flush(); await r.Controller.TickAsync(true); Check(r.Server.Count("turn/start") == 1,"fresh read stays blocked"); });
Test("API key mode refused",async ()=>
{ await using var r=new Rig(server:new() { AuthType="apiKey" }); await r.Connect(); await MustThrow(r.Start); Check(r.Server.Count("turn/start") == 0 && !r.Controller.ChatGptAuthenticated,"chatgpt required"); });
Test("third-party provider refused",async ()=>
{ await using var r=new Rig(server:new() { Provider="third-party" }); await r.Connect(); await MustThrow(r.Start); Check(r.Server.Count("thread/start") == 0,"no provider switch"); });
Test("permissions are never weakened in requests",async ()=>
{ await using var r=new Rig(); await r.Connect(); await r.Start(); foreach(var c in r.Server.Calls.Where(x=>x.Method is "thread/start" or "turn/start" or "thread/resume")) { Check(c.Params.Obj("sandbox").ValueKind == JsonValueKind.Undefined && c.Params.Obj("sandboxPolicy").ValueKind == JsonValueKind.Undefined && c.Params.Obj("approvalPolicy").ValueKind == JsonValueKind.Undefined,"no bypass"); } });
Test("paths spaces Cyrillic quotes are passed without shell",async ()=>
{ var exe=Environment.ProcessPath!; var values=new[] { @"C:\Проект с пробелами\задача","кавычки \"пример\"","$(never-execute) `literal`" }; using var p=Process.Start(AppServerTransport.ProcessInfo(exe,["--echo-args",..values]))!; var output=await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); Check(JsonSerializer.Deserialize<string[]>(output)!.SequenceEqual(values),"argument exact"); });
Sync("atomic state save and corrupted file quarantine",()=>
{ var folder=Path.Combine(Path.GetTempPath(),"CodexAutoContinue-state-"+Guid.NewGuid()); Directory.CreateDirectory(folder); try { var store=new StateStore(folder); var data=new AppData { Run=new() { ThreadId="saved-id",State=RunState.WaitingLimit } }; store.Save(data); Check(store.Load().Run.ThreadId == "saved-id","round trip"); Check(Directory.GetFiles(folder,"*.tmp-*").Length == 0,"atomic temp removed"); File.WriteAllText(Path.Combine(folder,"state.json"),"{broken"); Check(!store.Load().Settings.RestoreWaiting && store.LoadWarning is not null && Directory.GetFiles(folder,"*.corrupt-*").Length == 1,"quarantine"); } finally { Directory.Delete(folder,true); } });
Sync("file lease prevents separate owners",()=>
{ var folder=Path.Combine(Path.GetTempPath(),"CodexAutoContinue-lock-"+Guid.NewGuid()); try { using var a=new FileRunLease(folder); using var b=new FileRunLease(folder); a.Acquire("id"); bool blocked=false; try { b.Acquire("id"); } catch (InvalidOperationException) { blocked=true; } Check(blocked,"exclusive lease"); a.Release(); b.Acquire("id"); } finally { Directory.Delete(folder,true); } });
Sync("secrets redacted from journal",()=>
{ var s=Redactor.Clean("Bearer secret-test-value api_key=sk-abcdef123456 access_token=some-secret password='мойпароль' eyJabc.def.ghi"); foreach(var secret in new[] {"secret-test-value","sk-abcdef123456","some-secret","мойпароль","eyJabc.def.ghi"}) Check(!s.Contains(secret),"redacted "+secret); });
Sync("journal rotation and cleanup",()=>
{ var folder=Path.Combine(Path.GetTempPath(),"CodexAutoContinue-log-"+Guid.NewGuid()); try { var log=new RotatingLog(folder); log.Write(new string('x',2100000)); log.Write("next"); Check(File.Exists(log.CurrentPath+".1"),"rotated"); log.Clear(); Check(Directory.GetFiles(folder).Length == 0,"cleared"); } finally { Directory.Delete(folder,true); } });
Test("Windows job terminates parent and descendant",async ()=>
{
    using var parent=Process.Start(AppServerTransport.ProcessInfo(Environment.ProcessPath!,"--job-parent"))!;
    using var job=WindowsJob.Attach(parent)!;
    await parent.StandardInput.WriteLineAsync("go"); await parent.StandardInput.FlushAsync();
    var childPid=int.Parse((await parent.StandardOutput.ReadLineAsync())!);
    using var child=Process.GetProcessById(childPid);
    job.Dispose(); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(parent.HasExited && child.HasExited,"all descendants stopped");
});

int failed=0;
foreach(var (name,run) in tests)
{
    try { await run(); Console.WriteLine("PASS "+name); }
    catch(Exception e) { failed++; Console.WriteLine("FAIL "+name+": "+e); }
}
Console.WriteLine($"RESULT {tests.Count-failed}/{tests.Count} passed; {failed} failed");
Environment.ExitCode=failed == 0 ? 0 : 1;

static void Check(bool condition,string detail) { if (!condition) throw new Exception("Assertion: "+detail); }
static async Task MustThrow(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected operation rejection"); }
