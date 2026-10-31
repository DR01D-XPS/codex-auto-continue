using System.Text.Json;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1791500000);
    public void Advance(int seconds) => UtcNow=UtcNow.AddSeconds(seconds);
}
internal sealed class MemoryStore : IStateStore
{
    private string _json=JsonSerializer.Serialize(new AppData());
    public AppData Load() => JsonSerializer.Deserialize<AppData>(_json)!;
    public void Save(AppData data) => _json=JsonSerializer.Serialize(data);
}
internal sealed class FakeLease : IRunLease
{
    public bool Throw { get; set; }
    public void Acquire(string id) { if (Throw) throw new InvalidOperationException("locked"); }
    public void Release() { }
    public void Dispose() { }
}
internal sealed class FakeServer : IAppServer
{
    public const string ThreadId="019a1111-2222-7777-8888-999999999999";
    public bool IsConnected { get; private set; }
    public string Project { get; set; } = Path.GetTempPath();
    public List<JsonElement> Turns { get; } = [];
    public List<(string Method, JsonElement Params)> Calls { get; } = [];
    public List<JsonElement> Responses { get; } = [];
    public JsonElement Rates { get; set; } = RateResponse(20,10);
    public string AuthType { get; set; } = "chatgpt";
    public string Provider { get; set; } = "openai";
    public Exception? RateFailure { get; set; }
    public Exception? TurnFailure { get; set; }
    public Exception? ResumeFailure { get; set; }
    public bool AcceptResumeBeforeFailure { get; set; }
    public bool RejectRepeatedResume { get; set; }
    public HashSet<string> LoadedThreads { get; } = [];
    public bool AcceptBeforeFailure { get; set; }
    public TaskCompletionSource? DelayTurn { get; set; }
    public string RuntimeStatus { get; set; } = "notLoaded";
    public event Action<RpcEvent>? Notification;
    public event Action<ServerRequest>? Request;
    public event Action<string>? Disconnected;
    public int Count(string method) => Calls.Count(c=>c.Method == method);
    public static JsonElement RateResponse(double primary, double secondary, long? reset1=1791500100,long? reset2=1792100000,bool? ordinary=true,bool includeOrdinary=true)
    {
        var obj=new Dictionary<string,object?> { ["rateLimits"]=new { limitId="codex",primary=new { usedPercent=primary,windowDurationMins=300,resetsAt=reset1 },secondary=new { usedPercent=secondary,windowDurationMins=10080,resetsAt=reset2 },rateLimitReachedType=(string?)null } };
        if (includeOrdinary) obj["ordinaryUsageAllowed"]=ordinary;
        return JsonExt.ToJson(obj);
    }
    public Task ConnectAsync(string executable,CancellationToken ct=default) { if (!IsConnected) LoadedThreads.Clear(); IsConnected=true; return Task.CompletedTask; }
    public async Task<JsonElement> CallAsync(string method,object parameters,CancellationToken ct=default)
    {
        var p=JsonExt.ToJson(parameters); Calls.Add((method,p));
        switch (method)
        {
            case "account/read": return JsonExt.ToJson(new { account=new { type=AuthType,planType="plus" },requiresOpenaiAuth=true });
            case "config/read": return JsonExt.ToJson(new { config=new { model_provider=Provider == "openai" ? null : Provider } });
            case "account/rateLimits/read": if (RateFailure is { } rateError) throw rateError; return Rates;
            case "thread/start": LoadedThreads.Add(ThreadId); return JsonExt.ToJson(new { thread=Thread(),model="gpt-6.1-sol",modelProvider=Provider });
            case "thread/resume":
                if (ResumeFailure is not null && !AcceptResumeBeforeFailure) throw ResumeFailure;
                if (RejectRepeatedResume && LoadedThreads.Contains(p.Str("threadId"))) throw new RpcException(-32600,"thread "+p.Str("threadId")+" already has an active writer",default);
                LoadedThreads.Add(p.Str("threadId")); if (ResumeFailure is not null) throw ResumeFailure;
                return JsonExt.ToJson(new { thread=Thread(),model="gpt-6.1-sol",modelProvider=Provider });
            case "thread/loaded/list": return JsonExt.ToJson(new { data=LoadedThreads.ToArray(),nextCursor=(string?)null });
            case "thread/read": return JsonExt.ToJson(new { thread=Thread() });
            case "thread/list": return JsonExt.ToJson(new { data=new[] { Thread() },nextCursor=(string?)null });
            case "turn/start":
                if (TurnFailure is not null && !AcceptBeforeFailure) throw TurnFailure;
                var turn=JsonExt.ToJson(new { id="turn-"+(Turns.Count+1),status="inProgress",error=(object?)null,items=new[] { new { type="userMessage",clientId=p.Str("clientUserMessageId"),content=p.Array("input") } } });
                Turns.Add(turn);
                if (DelayTurn is { } delay) await delay.Task;
                if (TurnFailure is not null) throw TurnFailure;
                return JsonExt.ToJson(new { turn });
            case "turn/interrupt": Complete("interrupted"); return JsonExt.ToJson(new { });
            default: return JsonExt.ToJson(new { });
        }
    }
    public JsonElement Thread() => JsonExt.ToJson(new { id=ThreadId,preview="Тестовая задача",cwd=Project,modelProvider=Provider,model="gpt-6.1-sol",status=new { type=RuntimeStatus },updatedAt=1791500000,source="vscode",turns=Turns });
    public void Emit(string method,object p)
    {
        var json=JsonExt.ToJson(p);
        if (method == "thread/closed") LoadedThreads.Remove(json.Str("threadId"));
        Notification?.Invoke(new(method,json));
    }
    public void Complete(string status,string? info=null)
    {
        var previous=Turns.Last();
        var error=info is null ? (object?)null : new { message="simulated "+info,codexErrorInfo=info };
        var turn=JsonExt.ToJson(new { id=previous.Str("id"),status,error,items=previous.Array("items") });
        Turns[^1]=turn;
        if (info is not null) Emit("error",new { threadId=ThreadId,turnId=turn.Str("id"),error,willRetry=false });
        Emit("turn/completed",new { threadId=ThreadId,turn });
    }
    public void Ask(string method="item/commandExecution/requestApproval",int requestId=1) => Request?.Invoke(new(JsonExt.ToJson(requestId),method,JsonExt.ToJson(new { threadId=ThreadId,turnId=Turns.Last().Str("id"),itemId="item-1",command="test command" })));
    public Task RespondAsync(JsonElement id,object result,CancellationToken ct=default) { Responses.Add(JsonExt.ToJson(new { id,result })); return Task.CompletedTask; }
    public Task RejectAsync(JsonElement id,string message,CancellationToken ct=default) { Responses.Add(JsonExt.ToJson(new { id,error=message })); return Task.CompletedTask; }
    public void BreakConnection() { IsConnected=false; Disconnected?.Invoke("simulated disconnect"); }
    public ValueTask DisposeAsync() { IsConnected=false; return ValueTask.CompletedTask; }
}
internal sealed class Rig : IAsyncDisposable
{
    public FakeServer Server { get; }
    public MemoryStore Store { get; }
    public FakeClock Clock { get; }
    public FakeLease Lease { get; } = new();
    public RunController Controller { get; }
    public Rig(MemoryStore? store=null,FakeServer? server=null,FakeClock? clock=null)
    { Server=server ?? new(); Store=store ?? new(); Clock=clock ?? new(); Controller=new(Server,Store,Lease,Clock); }
    public Task Connect() => Controller.ConnectAsync("codex.exe");
    public Task Start() => Controller.StartNewAsync(Server.Project,"исходное задание",true);
    public Task Flush() => Controller.SaveSettingsAsync();
    public async Task QuotaStop() { Server.Rates=FakeServer.RateResponse(100,20,ordinary:false); Server.Complete("failed","usageLimitExceeded"); await Flush(); }
    public async ValueTask DisposeAsync() => await Controller.DisposeAsync();
}
