using System.Text.Json;

namespace CodexAutoContinue.Core;

/// <summary>Serializes all commands and events. A turn is started only by an explicit action
/// or by a confirmed quota recovery. Unacknowledged starts remain quarantined.</summary>
public sealed class RunController : IAsyncDisposable
{
    private readonly IAppServer _server;
    private readonly IStateStore _store;
    private readonly IRunLease _lease;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1,1);
    private readonly HashSet<string> _terminalTurns = [];
    private readonly HashSet<string> _unknownMethods = [];
    private readonly HashSet<string> _loadedThreads = [];
    private readonly Dictionary<string, ServerRequest> _requests = [];
    private readonly Dictionary<string, JsonElement> _fileChanges = [];
    private int _backoff;
    private bool _closing;
    private string? _lastErrorTag;
    private JsonElement _lastError;
    public AppData Data { get; }
    public RateLimits? Limits { get; private set; }
    public string CliStatus { get; private set; } = "CLI не подключён";
    public string AuthStatus { get; private set; } = "Авторизация не проверена";
    public bool ChatGptAuthenticated { get; private set; }
    public IReadOnlyList<ServerRequest> PendingRequests => _requests.Values.ToArray();
    public event Action? Changed;
    public event Action<string>? Log;
    public event Action<string>? Output;
    public event Action<string, string>? Notice;

    public RunController(IAppServer server, IStateStore store, IRunLease lease, IClock? clock = null)
    {
        _server=server; _store=store; _lease=lease; _clock=clock ?? new SystemClock(); Data = store.Load();
        _server.Notification += e => Queue(() => HandleNotificationAsync(e));
        _server.Request += r => Queue(() => HandleRequestAsync(r));
        _server.Disconnected += reason => Queue(() =>
        {
            _loadedThreads.Clear();
            ChatGptAuthenticated=false; CliStatus="Соединение закрыто";
            if (Data.Run.State == RunState.WaitingLimit && !Data.Run.UserStopped && !Data.Run.Paused)
            { Data.Run.Detail=reason+" Ожидание лимита сохранено; подключение будет проверено повторно."; Data.Run.NextCheck=_clock.UtcNow.AddSeconds(Math.Max(120,Data.Settings.PollSeconds)); Save(); Emit(Data.Run.Detail); }
            else { if (Data.Run.State is RunState.Running or RunState.AwaitingApproval) Data.Run.StartUncertain=true; SetState(RunState.Error,reason); }
            return Task.CompletedTask;
        });
    }
    private void Queue(Func<Task> action)
    {
        _ = ProcessQueuedAsync(action);
    }
    private async Task ProcessQueuedAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { if (!_closing) await action(); }
        catch (Exception e) { HandleException(e); }
        finally { _gate.Release(); }
    }
    private async Task WithGate(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { if (!_closing) await action(); }
        finally { _gate.Release(); }
    }
    private void Save() { _store.Save(Data); Changed?.Invoke(); }
    private void Emit(string text) => Log?.Invoke(Redactor.Clean(text));
    private void SetState(RunState state, string detail)
    {
        var previous = Data.Run.State;
        Data.Run.State=state; Data.Run.Detail=Redactor.Clean(detail); Save(); Emit(detail);
        if (previous != state && Data.Settings.Notifications && state is RunState.WaitingLimit or RunState.Running or RunState.Completed or RunState.Error or RunState.NeedsLogin or RunState.Offline or RunState.AwaitingApproval)
            Notice?.Invoke(StateLabel(state), Redactor.Clean(detail));
    }
    public static string StateLabel(RunState s) => s switch
    {
        RunState.Ready=>"Готово", RunState.Running=>"Выполняется", RunState.WaitingLimit=>"Ожидание лимита", RunState.Offline=>"Нет интернета",
        RunState.NeedsLogin=>"Нужен вход", RunState.AwaitingApproval=>"Ожидание разрешения", RunState.Paused=>"Приостановлено", RunState.Completed=>"Завершено", _=>"Ошибка"
    };

    public Task ConnectAsync(string cliPath) => WithGate(async () =>
    {
        if (Data.Run.State is RunState.Running or RunState.AwaitingApproval && _server.IsConnected) return;
        Data.Settings.CliPath=cliPath;
        if (!_server.IsConnected) _loadedThreads.Clear();
        await _server.ConnectAsync(cliPath);
        CliStatus="Codex App Server подключён";
        await ReadAccountAsync();
        if (ChatGptAuthenticated) await RefreshLimitsAsync();
        await RecoverAsync(); Save();
    });
    private async Task ReadAccountAsync()
    {
        var account = (await _server.CallAsync("account/read", new { refreshToken=false })).Obj("account");
        ChatGptAuthenticated=account.Str("type") == "chatgpt";
        AuthStatus=ChatGptAuthenticated ? "ChatGPT · " + account.Str("planType", "план неизвестен") : account.ValueKind == JsonValueKind.Null ? "Нужен вход через ChatGPT" : "Требуется авторизация ChatGPT; выбран другой способ входа";
        if (!ChatGptAuthenticated && Data.Run.State == RunState.WaitingLimit) { Data.Run.Detail="Нужен вход. Сохранённое ожидание лимита продолжится после авторизации."; Save(); }
        else if (!ChatGptAuthenticated && Data.Run.State is not RunState.Running and not RunState.AwaitingApproval) SetState(RunState.NeedsLogin, AuthStatus);
        Changed?.Invoke();
    }
    public Task LoginAsync(Action<string> openBrowser) => WithGate(async () =>
    {
        RequireConnected();
        var result=await _server.CallAsync("account/login/start", new { type="chatgpt" });
        var url=result.Str("authUrl");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "auth.openai.com" || uri.Host == "chatgpt.com" || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)))
            openBrowser(url);
        else throw new InvalidOperationException("CLI не вернул допустимый адрес официального входа.");
        Emit("Открыт официальный вход ChatGPT. Завершите вход в браузере.");
    });
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? cursor = null)
    {
        IReadOnlyList<SessionInfo> sessions=[];
        await WithGate(async () =>
        {
            RequireConnected(); var all=new List<SessionInfo>(); var cursors=new HashSet<string>();
            do
            {
                var result=await _server.CallAsync("thread/list", new { cursor, limit=100, sourceKinds=new[] {"cli","vscode","exec","appServer"}, modelProviders=Array.Empty<string>(), sortKey="updated_at" });
                all.AddRange(result.Array("data").Select(SessionInfo.FromJson));
                cursor=result.OptionalStr("nextCursor");
                if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("CLI повторил курсор списка сессий.");
            } while (cursor is not null);
            sessions=all;
        }); return sessions;
    }
    public Task SaveSettingsAsync() => WithGate(() => { Data.Settings.PollSeconds=Math.Clamp(Data.Settings.PollSeconds,30,900); Save(); return Task.CompletedTask; });
    private void RequireConnected() { if (!_server.IsConnected) throw new IOException("Подключите Codex CLI. Установка: npm install -g @openai/codex, затем codex login."); }
    private void RequireIdle()
    {
        if (Data.Run.State is RunState.Running or RunState.AwaitingApproval || Data.Run.StartUncertain || Data.Run.CreatingThread)
            throw new InvalidOperationException("Предыдущая работа выполняется или её результат не определён. Сначала остановите её и проверьте историю.");
    }
    private void RequireAccount() { RequireConnected(); if (!ChatGptAuthenticated) throw new InvalidOperationException("Нужен вход через ChatGPT. API-ключи и другие провайдеры не используются."); }
    private static void ValidateProject(string project)
    {
        if (project.StartsWith("\\\\wsl",StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(project) || !Directory.Exists(project))
            throw new InvalidOperationException("Выберите существующую папку нативного Windows. Сессии WSL и удалённых сред недоступны в этой версии.");
    }
    private async Task ValidateProviderAsync(string cwd)
    {
        var cfg=(await _server.CallAsync("config/read", new { includeLayers=false, cwd })).Obj("config");
        var provider=cfg.OptionalStr("model_provider");
        if (provider is not null && provider != "openai") throw new InvalidOperationException("В конфигурации проекта выбран другой провайдер. Приложение работает только с ChatGPT/OpenAI и не переключает провайдера автоматически.");
    }

    public Task StartNewAsync(string project, string prompt, bool autoContinue) => WithGate(async () =>
    {
        RequireIdle(); RequireAccount(); ValidateProject(project);
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Введите исходное задание.");
        await ValidateProviderAsync(project);
        _lease.Release(); _terminalTurns.Clear(); _lastError=default;
        Data.Run=new() { ProjectPath=Path.GetFullPath(project), OriginalPrompt=prompt, AutoContinue=autoContinue, Owned=true, CreatedByApp=true };
        Save();
        if (!await RefreshLimitsAsync()) { SetState(RunState.WaitingLimit,Limits!.Evaluate().Reason); return; }
        await CreateOwnedThreadAsync();
        await StartTurnIfAvailableAsync(prompt, initial:true);
    });
    private async Task CreateOwnedThreadAsync()
    {
        ValidateProject(Data.Run.ProjectPath); await ValidateProviderAsync(Data.Run.ProjectPath);
        Data.Run.CreatingThread=true; Save();
        JsonElement response;
        try { response=await _server.CallAsync("thread/start", new { cwd=Data.Run.ProjectPath }); }
        catch { SetState(RunState.Paused,"Создание сессии не подтверждено. Проверьте список сессий перед повторным созданием."); throw; }
        Data.Run.CreatingThread=false;
        Data.Run.ThreadId=response.Obj("thread").Str("id"); Data.Run.Model=response.OptionalStr("model");
        Save(); _lease.Acquire(Data.Run.ThreadId);
        await EnsureOpenAiResponseAsync(response);
        _loadedThreads.Add(Data.Run.ThreadId);
    }
    private Task EnsureOpenAiResponseAsync(JsonElement response)
    {
        if (response.Str("modelProvider", "openai") != "openai") throw new InvalidOperationException("Сессия использует другой провайдер. Запуск отклонён.");
        // No sandbox or approval override is ever sent. Effective CLI restrictions remain in force.
        Emit("Ограничения CLI сохранены. Запросы разрешений появятся в приложении.");
        return Task.CompletedTask;
    }

    public Task TakeOverAsync(string threadId, bool handoffConfirmed, bool autoContinue) => WithGate(async () =>
    {
        if (handoffConfirmed && Data.Run.CreatingThread && !Data.Run.StartUncertain) { Data.Run.CreatingThread=false; Save(); }
        RequireIdle(); RequireAccount();
        if (!handoffConfirmed) throw new InvalidOperationException("Остановите и закройте задачу в исходном клиенте Codex, затем подтвердите передачу приложению.");
        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Введите ID сессии.");
        _lease.Acquire(threadId);
        try
        {
            var thread=await ReadThreadAsync(threadId);
            ValidateProject(thread.Str("cwd"));
            if (thread.Str("modelProvider", "openai") != "openai") throw new InvalidOperationException("Сессия другого провайдера не поддерживается.");
            await ValidateProviderAsync(thread.Str("cwd"));
            AssertNoLiveTurn(thread);
            var turns=thread.Array("turns");
            Data.Run=new() { ThreadId=threadId, ProjectPath=thread.Str("cwd"), OriginalPrompt=thread.Str("preview"), Model=thread.OptionalStr("model"), Owned=false, HasSubmittedTurn=turns.Length>0, AutoContinue=false,
                TurnId=turns.LastOrDefault().OptionalStr("id"), BaselineLastTurnId=turns.LastOrDefault().OptionalStr("id"), BaselineTurnCount=turns.Length };
            Save();
            await EnsureThreadLoadedAsync(threadId);
            Data.Run.Owned=true; Data.Run.HandoffAcquired=true; Data.Run.AutoContinue=autoContinue;
            Save();
            await StartTurnIfAvailableAsync(Data.Settings.ContinueMessage, initial:false);
        }
        catch { if (Data.Run.ThreadId != threadId || !Data.Run.Owned) _lease.Release(); throw; }
    });

    private async Task EnsureThreadLoadedAsync(string threadId)
    {
        // Resume only once in this App Server lifetime. An idle loaded thread still owns its writer.
        if (_loadedThreads.Contains(threadId)) return;
        // A resume acknowledgement can be lost even though this private server acquired the writer.
        // Its own loaded list is authoritative for that case; a foreign server's idle status isn't.
        try
        {
            string? cursor=null; var cursors=new HashSet<string>();
            do
            {
                var loaded=await _server.CallAsync("thread/loaded/list",new { cursor,limit=100 });
                if (loaded.Array("data").Any(id=>id.ValueKind == JsonValueKind.String && id.GetString() == threadId))
                { _loadedThreads.Add(threadId); return; }
                cursor=loaded.OptionalStr("nextCursor");
                if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("CLI повторил курсор загруженных сессий.");
            } while (cursor is not null);
        }
        catch (RpcException e) when (e.Code == -32601) { } // Older CLI: retain the conservative resume path.
        JsonElement resumed;
        try { resumed=await _server.CallAsync("thread/resume",new { threadId }); }
        catch (RpcException e) when (e.Message.Contains("already has an active writer",StringComparison.OrdinalIgnoreCase))
        {
            Data.Run.Owned=false; Data.Run.HandoffAcquired=false; Data.Run.AutoContinue=false; Data.Run.Paused=true;
            Data.Run.NextCheck=null; _lease.Release();
            var message="Сессия ещё занята другим клиентом Codex. Закройте эту задачу в исходном клиенте. Если ошибка остаётся, сохраните работу и полностью закройте тот клиент (например VS Code), затем повторите передачу сессии. Автопродолжение этой сессии отключено.";
            SetState(RunState.Paused,message);
            throw new InvalidOperationException(message,e);
        }
        if (resumed.Obj("thread").Str("id") != threadId) throw new InvalidOperationException("Сервер вернул другой ID при передаче сессии. Запуск отменён.");
        await EnsureOpenAiResponseAsync(resumed);
        AssertNoLiveTurn(resumed.Obj("thread"));
        Data.Run.Model=resumed.OptionalStr("model") ?? Data.Run.Model;
        _loadedThreads.Add(threadId);
    }

    private void RequireOwned()
    {
        if (!Data.Run.Owned) throw new InvalidOperationException("Сессия ещё не передана приложению. Остановите и закройте её в исходном клиенте, затем выберите «Локальная сессия», подтвердите передачу и нажмите «Запустить».");
    }

    private async Task<JsonElement> ReadThreadAsync(string id)
    {
        var t=(await _server.CallAsync("thread/read",new { threadId=id, includeTurns=true })).Obj("thread");
        if (t.Str("id") != id) throw new InvalidOperationException("Сервер вернул другой ID сессии. Продолжение отменено.");
        return t;
    }
    private static void AssertNoLiveTurn(JsonElement thread)
    {
        if (thread.Obj("status").Str("type") == "active" || thread.Array("turns").Any(t=>t.Str("status") == "inProgress"))
            throw new InvalidOperationException("История содержит активный или незавершённый запуск. Неопределённость требует проверки и остановки в исходном клиенте; повторный запуск заблокирован.");
    }

    private async Task<bool> RefreshLimitsAsync()
    {
        RequireAccount();
        var response=await _server.CallAsync("account/rateLimits/read",new { });
        Limits=RateLimits.Parse(response,Data.Run.Model);
        var decision=Limits.Evaluate(); Data.Run.WaitUntil=decision.WaitUntil;
        var baseInterval=Data.Settings.PollSeconds;
        if (decision.Availability == Availability.Unknown || decision.UnknownReset)
            _backoff=Math.Min(_backoff+1,5);
        else _backoff=0;
        var interval=_backoff == 0 ? baseInterval : Math.Min(1800,Math.Max(120,baseInterval)*(1<<(_backoff-1)));
        var next=_clock.UtcNow.AddSeconds(interval);
        if (decision.WaitUntil is { } until && until > _clock.UtcNow && !decision.UnknownReset)
            next=next < until.AddSeconds(2) ? next : until.AddSeconds(2);
        Data.Run.NextCheck=next;
        if (Data.Run.State == RunState.WaitingLimit) Data.Run.Detail=decision.Reason + (decision.UnknownReset ? " Время восстановления неизвестно; интервал проверки увеличен." : "");
        Save(); return decision.Availability == Availability.Available;
    }
    public Task CheckLimitsAsync() => WithGate(async () => { await ReadAccountAsync(); if (ChatGptAuthenticated) { await RefreshLimitsAsync(); Emit(Limits!.Evaluate().Reason); } });

    private async Task StartTurnIfAvailableAsync(string message, bool initial)
    {
        RequireAccount();
        RequireOwned();
        if (Data.Run.UserStopped || Data.Run.Paused) return;
        if (!await RefreshLimitsAsync()) { SetState(RunState.WaitingLimit,Limits!.Evaluate().Reason); return; }
        var id=Data.Run.ThreadId ?? throw new InvalidOperationException("Сессия не выбрана.");
        _lease.Acquire(id);
        if (!initial)
        {
            var history=await ReadThreadAsync(id); AssertNoLiveTurn(history);
            var turns=history.Array("turns"); var last=turns.LastOrDefault();
            if (Data.Run.BaselineLastTurnId is not null && last.OptionalStr("id") != Data.Run.BaselineLastTurnId)
            { Data.Run.Paused=true; SetState(RunState.Paused,"История изменилась в другом клиенте. Повторно передайте сессию приложению после проверки."); return; }
            await EnsureThreadLoadedAsync(id);
            Data.Run.BaselineTurnCount=turns.Length; Data.Run.BaselineLastTurnId=last.OptionalStr("id");
            if (!await RefreshLimitsAsync()) { SetState(RunState.WaitingLimit,Limits!.Evaluate().Reason); return; }
        }
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Сообщение продолжения не может быть пустым.");
        Data.Run.PendingMessageId=Guid.NewGuid().ToString(); Data.Run.StartUncertain=true; Data.Run.HasSubmittedTurn=true; Data.Run.TurnId=null; _lastError=default; _lastErrorTag=null;
        SetState(RunState.Running,initial ? "Задача запущена." : "Доступ восстановлен. Продолжаем выбранную сессию.");
        // Persist intent before write. clientUserMessageId is a correlation key, never assumed to be server idempotency.
        try
        {
            var response=await _server.CallAsync("turn/start",new { threadId=id, clientUserMessageId=Data.Run.PendingMessageId, input=new[] { new { type="text", text=message, text_elements=Array.Empty<object>() } } });
            var turn=response.Obj("turn"); var turnId=turn.Str("id");
            if (string.IsNullOrEmpty(turnId)) throw new IOException("CLI не подтвердил ID запуска.");
            Data.Run.TurnId=turnId; Data.Run.StartUncertain=false; Save();
            if (turn.Str("status") != "inProgress") await CompleteTurnAsync(turn);
        }
        catch (RpcException e) when (e.DataJson.ValueKind != JsonValueKind.Undefined && ErrorClassifier.Classify(JsonExt.ToJson(new { data=e.DataJson })) == RunState.WaitingLimit)
        {
            Data.Run.StartUncertain=false; Data.Run.PendingMessageId=null;
            SetState(RunState.WaitingLimit,"CLI отклонил запуск: лимит исчерпан."); await RefreshLimitsAsync();
        }
        catch
        {
            SetState(RunState.Paused,"Подтверждение запуска не получено. Сначала будет проверена история; автоматический повтор заблокирован."); throw;
        }
    }

    public Task EnableAutoAsync() => WithGate(() =>
    {
        if (Data.Run.ThreadId is not null) RequireOwned();
        Data.Run.AutoContinue=true;
        if (Data.Run.State == RunState.WaitingLimit && !Data.Run.UserStopped) Data.Run.Paused=false;
        Save(); Emit("Автопродолжение включено. Оно срабатывает после остановки из-за лимита."); return Task.CompletedTask;
    });
    public Task ContinueNowAsync() => WithGate(async () =>
    {
        RequireIdle(); RequireAccount();
        RequireOwned();
        Data.Run.Paused=false; Data.Run.UserStopped=false;
        Save();
        if (Data.Run.ThreadId is null)
        {
            if (!Data.Run.Owned || string.IsNullOrWhiteSpace(Data.Run.OriginalPrompt)) throw new InvalidOperationException("Сначала запустите или передайте сессию.");
            if (!await RefreshLimitsAsync()) { SetState(RunState.WaitingLimit,Limits!.Evaluate().Reason); return; }
            await CreateOwnedThreadAsync(); await StartTurnIfAvailableAsync(Data.Run.OriginalPrompt,initial:true); return;
        }
        var neverSent=Data.Run.CreatedByApp && !Data.Run.HasSubmittedTurn;
        await StartTurnIfAvailableAsync(neverSent ? Data.Run.OriginalPrompt : Data.Settings.ContinueMessage,initial:neverSent);
    });
    public Task PauseAsync() => WithGate(async () =>
    {
        Data.Run.Paused=true; Save();
        await InterruptAsync(); SetState(RunState.Paused,"Автопродолжение приостановлено. Текущий запуск прерван, если он выполнялся.");
    });
    public Task StopAsync() => WithGate(async () =>
    {
        Data.Run.UserStopped=true; Data.Run.Paused=true; Data.Run.AutoContinue=false; Data.Run.NextCheck=null; Save();
        try { await InterruptAsync(); }
        finally { SetState(RunState.Paused,"Остановлено пользователем. Автоматическое восстановление отключено."); }
    });
    private async Task InterruptAsync()
    {
        if (_server.IsConnected && Data.Run.ThreadId is { } id && Data.Run.TurnId is { } turn && Data.Run.State is RunState.Running or RunState.AwaitingApproval)
        {
            await _server.CallAsync("turn/interrupt",new { threadId=id, turnId=turn });
            Data.Run.StartUncertain=true; Save(); // Only turn/completed proves interruption finished.
        }
        else if (Data.Run.StartUncertain && _server.IsConnected) await RecoverUncertainAsync();
    }

    public Task TickAsync(bool wokeFromSleep=false) => WithGate(async () =>
    {
        if (Data.Run.UserStopped || Data.Run.Paused || Data.Run.State != RunState.WaitingLimit) return;
        if (!wokeFromSleep && Data.Run.NextCheck is { } next && _clock.UtcNow < next) return;
        if (wokeFromSleep) Emit("Компьютер пробудился. Повторно проверяем лимиты.");
        try
        {
            if (!_server.IsConnected) { _loadedThreads.Clear(); await _server.ConnectAsync(Data.Settings.CliPath); }
            await ReadAccountAsync();
            if (!ChatGptAuthenticated) { Data.Run.NextCheck=_clock.UtcNow.AddSeconds(Math.Max(120,Data.Settings.PollSeconds)); Save(); return; }
            var available=await RefreshLimitsAsync();
            if (available && Data.Run.AutoContinue)
            {
                if (Data.Run.ThreadId is null) { await CreateOwnedThreadAsync(); await StartTurnIfAvailableAsync(Data.Run.OriginalPrompt,initial:true); }
                else { var neverSent=Data.Run.CreatedByApp && !Data.Run.HasSubmittedTurn; await StartTurnIfAvailableAsync(neverSent ? Data.Run.OriginalPrompt : Data.Settings.ContinueMessage, initial:neverSent); }
            }
        }
        catch (Exception e)
        {
            var state=ExceptionState(e);
            Data.Run.NextCheck=_clock.UtcNow.AddSeconds(Math.Min(1800,Math.Max(120,Data.Settings.PollSeconds)*(1<<Math.Min(++_backoff,4))));
            // Keep quota waiting as the persisted cause. Never turn network recovery into a model probe.
            Data.Run.Detail=Redactor.Clean(state == RunState.NeedsLogin ? "Нужен вход. Автопродолжение ожидает авторизации." : state == RunState.Offline ? "Нет интернета. Проверка лимитов будет повторена." : "Не удалось проверить лимиты: " + e.Message);
            Save(); Emit(Data.Run.Detail);
        }
    });

    private async Task RecoverAsync()
    {
        var r=Data.Run;
        if (r.ThreadId is not null && !r.CreatedByApp && (!r.Owned || !r.HandoffAcquired))
        {
            r.Owned=false; r.HandoffAcquired=false; r.AutoContinue=false; r.Paused=true; r.NextCheck=null;
            SetState(RunState.Paused,"Передача этой сессии не подтверждена. Закройте её в исходном клиенте и повторно передайте приложению через «Локальная сессия»."); return;
        }
        if (r.CreatingThread) { SetState(RunState.Paused,"Создание сессии ранее не подтвердилось. Выберите точный ID из списка и передайте её вручную."); return; }
        if (r.CreatedByApp && !r.HasSubmittedTurn && !r.StartUncertain && r.ThreadId is not null && r.State == RunState.WaitingLimit)
        {
            // The native CLI doesn't materialize a rollout before the first turn. This is only
            // unused metadata for our own new task, never a handed-over existing session.
            r.ThreadId=null; _lease.Release(); Save();
            Emit("Первый запуск ещё не отправлялся. Восстановлено исходное задание до создания постоянной сессии.");
        }
        if (r.ThreadId is null)
        {
            if (r.State == RunState.WaitingLimit && r.Owned && !r.UserStopped && !r.Paused && Data.Settings.RestoreWaiting) { r.NextCheck=_clock.UtcNow; Save(); return; }
            if (r.UserStopped || r.Paused || r.State == RunState.WaitingLimit && !Data.Settings.RestoreWaiting) { r.Paused=true; SetState(RunState.Paused,"Сохранённая задача приостановлена."); return; }
            if (ChatGptAuthenticated) SetState(RunState.Ready,"Выберите проект и задачу."); return;
        }
        if (r.StartUncertain || r.State is RunState.Running or RunState.AwaitingApproval) { r.StartUncertain=true; await RecoverUncertainAsync(); return; }
        if (r.UserStopped || r.Paused || !Data.Settings.RestoreWaiting)
        { r.Paused=true; SetState(RunState.Paused,"Сохранённая задача приостановлена. Автоматический запуск не выполняется."); return; }
        if (r.State == RunState.WaitingLimit && ChatGptAuthenticated)
        {
            _lease.Acquire(r.ThreadId);
            var thread=await ReadThreadAsync(r.ThreadId); AssertNoLiveTurn(thread);
            if (thread.Array("turns").LastOrDefault().OptionalStr("id") != r.BaselineLastTurnId)
            { r.Paused=true; SetState(RunState.Paused,"История сохранённой сессии изменилась. Требуется повторная передача."); return; }
            r.NextCheck=_clock.UtcNow; Save(); Emit("Восстановлено ожидание для сохранённого ID сессии.");
        }
    }
    private async Task RecoverUncertainAsync()
    {
        var r=Data.Run;
        if (r.ThreadId is null) return;
        var thread=await ReadThreadAsync(r.ThreadId);
        var turns=thread.Array("turns");
        var match=turns.FirstOrDefault(t => t.Str("id") == r.TurnId || r.PendingMessageId is not null && t.Array("items").Any(i => i.Str("type") == "userMessage" && i.Str("clientId") == r.PendingMessageId));
        if (match.ValueKind == JsonValueKind.Object && match.Str("status") is "completed" or "failed" or "interrupted")
        {
            r.TurnId=match.Str("id"); r.StartUncertain=false;
            await CompleteTurnAsync(match);
            if (r.State == RunState.WaitingLimit && !Data.Settings.RestoreWaiting) { r.Paused=true; SetState(RunState.Paused,"Восстановление ожидания отключено."); }
            return;
        }
        r.Paused=true;
        SetState(RunState.Paused,"Результат предыдущего запуска неизвестен или он не завершён. Повтор заблокирован: проверьте эту сессию в исходном клиенте.");
    }
    public Task ReconcileAsync() => WithGate(async () => { RequireConnected(); await RecoverUncertainAsync(); });

    private async Task HandleNotificationAsync(RpcEvent ev)
    {
        var p=ev.Params;
        if (ev.Method == "account/login/completed")
        { if (p.Bool("success") == true) { await ReadAccountAsync(); if (ChatGptAuthenticated) { await RefreshLimitsAsync(); if (Data.Run.ThreadId is null) SetState(RunState.Ready,"Вход выполнен. Выберите задачу."); } } else Emit("Вход не завершён. Повторите официальный вход."); return; }
        if (ev.Method == "account/updated") { await ReadAccountAsync(); return; }
        if (ev.Method == "account/rateLimits/updated")
        {
            // Notifications may omit ordinaryUsageAllowed. Never use a sparse update to prove recovery.
            if (Data.Run.State == RunState.WaitingLimit && Data.Run.NextCheck < _clock.UtcNow) Data.Run.NextCheck=_clock.UtcNow;
            Changed?.Invoke(); return;
        }
        if (ev.Method == "thread/closed" || ev.Method == "thread/status/changed" && p.Obj("status").Str("type") == "notLoaded")
        { _loadedThreads.Remove(p.Str("threadId")); Changed?.Invoke(); return; }
        if (p.Str("threadId") != Data.Run.ThreadId) return;
        if (ev.Method == "serverRequest/resolved")
        { _requests.Remove(p.Obj("requestId").GetRawText()); if (_requests.Count == 0 && Data.Run.State == RunState.AwaitingApproval) SetState(RunState.Running,"Разрешение обработано. Работа продолжается."); return; }
        if (ev.Method == "turn/started")
        {
            var turn=p.Obj("turn");
            if (Data.Run.StartUncertain && Data.Run.State == RunState.Running) { Data.Run.TurnId=turn.Str("id"); Data.Run.StartUncertain=false; Save(); }
            return;
        }
        var turnId=p.OptionalStr("turnId") ?? p.Obj("turn").OptionalStr("id");
        if (turnId is not null && turnId != Data.Run.TurnId) return;
        switch (ev.Method)
        {
            case "turn/completed": await CompleteTurnAsync(p.Obj("turn")); break;
            case "error": _lastError=p.Obj("error").Clone(); _lastErrorTag=_lastError.Str("message"); Emit("Codex: " + _lastErrorTag + (p.Bool("willRetry") == true ? " (CLI повторит запрос)" : "")); break;
            case "item/agentMessage/delta": break; // Render complete items after redaction; never persist token fragments.
            case "item/commandExecution/outputDelta": break;
            case "item/started":
                var item=p.Obj("item");
                if (item.Str("type") == "commandExecution") Emit("Команда: " + item.Str("command"));
                else if (item.Str("type") == "fileChange") { _fileChanges[item.Str("id")]=item.Obj("changes").Clone(); Emit("Изменение файлов: " + string.Join(", ",item.Array("changes").Select(c=>c.Str("path")))); }
                break;
            case "item/completed":
                var completed=p.Obj("item");
                if (completed.Str("type") == "agentMessage") Output?.Invoke(Redactor.Clean(completed.Str("text")));
                if (completed.Str("type") == "commandExecution" && completed.OptionalStr("aggregatedOutput") is { } commandOutput) Output?.Invoke(Redactor.Clean(commandOutput));
                if (completed.Str("type") == "agentMessage" && completed.Array("questions").Length > 0) SetState(RunState.AwaitingApproval,"Codex ожидает ответа. Ответьте через поле «Ответ / уточнение».");
                break;
            case "thread/status/changed":
                var flags=p.Obj("status").Array("activeFlags");
                if (flags.Any(f=>f.ValueKind == JsonValueKind.String && f.GetString() is "waitingOnApproval" or "waitingOnUserInput")) SetState(RunState.AwaitingApproval,"Codex ожидает разрешения или ответа пользователя.");
                break;
            default: if (_unknownMethods.Add(ev.Method)) Emit("Уведомление CLI: " + ev.Method + " (без автоматического действия)"); break;
        }
    }
    private async Task CompleteTurnAsync(JsonElement turn)
    {
        var id=turn.Str("id");
        if (id.Length == 0 || !_terminalTurns.Add(id)) return;
        Data.Run.StartUncertain=false; Data.Run.PendingMessageId=null; Data.Run.TurnId=id; Data.Run.BaselineLastTurnId=id;
        Data.Run.BaselineTurnCount++; _requests.Clear();
        if (Data.Run.UserStopped || Data.Run.Paused) { SetState(RunState.Paused,Data.Run.UserStopped ? "Остановлено пользователем." : "Работа приостановлена."); return; }
        if (turn.Str("status") == "completed")
        {
            var questions=turn.Array("items").Any(i=>i.Str("type") == "agentMessage" && i.Array("questions").Length > 0);
            SetState(questions ? RunState.AwaitingApproval : RunState.Completed,questions ? "Codex ожидает ответа пользователя." : "Задача завершена. Автоматические сообщения больше не отправляются."); return;
        }
        if (turn.Str("status") == "interrupted") { Data.Run.Paused=true; SetState(RunState.Paused,"Запуск прерван."); return; }
        var error=turn.Obj("error"); if (error.ValueKind != JsonValueKind.Object) error=_lastError;
        var state=ErrorClassifier.Classify(error);
        SetState(state,error.Str("message",_lastErrorTag ?? "CLI завершил запуск с ошибкой."));
        if (state == RunState.WaitingLimit)
        { try { await RefreshLimitsAsync(); } catch (Exception e) { Data.Run.NextCheck=_clock.UtcNow.AddSeconds(Data.Settings.PollSeconds); Data.Run.Detail="Лимит исчерпан; чтение лимитов не удалось: " + Redactor.Clean(e.Message); Save(); } }
    }

    private async Task HandleRequestAsync(ServerRequest request)
    {
        if (Data.Run.UserStopped || Data.Run.Paused)
        { await _server.RejectAsync(request.Id,"Controlled task was stopped or paused by its user"); return; }
        if (request.Params.Str("threadId") != Data.Run.ThreadId || (request.Params.OptionalStr("turnId") is { } turn && Data.Run.TurnId is not null && turn != Data.Run.TurnId))
        { await _server.RejectAsync(request.Id,"Request does not belong to the controlled turn"); return; }
        if (request.Method is not ("item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval" or "item/tool/requestUserInput" or "mcpServer/elicitation/request"))
        { await _server.RejectAsync(request.Id,"Unsupported server request; no automatic permission granted"); Emit("Неподдерживаемый запрос отклонён: " + request.Method); return; }
        if (request.Method == "item/fileChange/requestApproval" && _fileChanges.TryGetValue(request.Params.Str("itemId"),out var changes))
        {
            var display=request.Params.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone()); display["_previewChanges"]=changes;
            request=request with { Params=JsonExt.ToJson(display) };
        }
        if (_requests.TryAdd(request.Id.GetRawText(),request))
            SetState(RunState.AwaitingApproval,request.Method == "item/tool/requestUserInput" ? "Требуется ответ пользователя." : "Требуется ваше разрешение. Автоматическое принятие отключено.");
    }
    public Task RespondAsync(ServerRequest request, object response) => WithGate(async () =>
    {
        if (!_requests.ContainsKey(request.Id.GetRawText())) throw new InvalidOperationException("Этот запрос уже обработан.");
        await _server.RespondAsync(request.Id,response); _requests.Remove(request.Id.GetRawText());
        // Never log response payloads: inputs can be marked isSecret.
        Emit("Ответ пользователя передан CLI.");
        if (_requests.Count == 0) SetState(RunState.Running,"Ответ передан. Работа продолжается."); else Save();
    });
    public Task ReplyAsync(string text) => WithGate(async () =>
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Введите ответ.");
        if (_requests.Count != 0) throw new InvalidOperationException("Сначала ответьте на запрос разрешения.");
        if (Data.Run.State == RunState.AwaitingApproval && !Data.Run.StartUncertain)
        {
            var thread=await ReadThreadAsync(Data.Run.ThreadId!);
            if (thread.Array("turns").Any(t=>t.Str("status") == "inProgress")) throw new InvalidOperationException("CLI всё ещё ждёт структурированный ответ. Проверьте запросы разрешений.");
            Data.Run.State=RunState.Completed;
        }
        RequireIdle(); Data.Run.UserStopped=false; Data.Run.Paused=false;
        await StartTurnIfAvailableAsync(text,initial:false);
    });
    private static RunState ExceptionState(Exception e) => e switch
    {
        RpcException rpc => ErrorClassifier.Classify(JsonExt.ToJson(new { data=rpc.DataJson.ValueKind == JsonValueKind.Undefined ? (object?)null : rpc.DataJson })),
        TimeoutException=>RunState.Offline, IOException=>RunState.Error, _=>RunState.Error
    };
    public void HandleException(Exception e) { var state=ExceptionState(e); SetState(state,Redactor.Clean(e.Message)); }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _closing=true;
            if (Data.Run.State is RunState.Running or RunState.AwaitingApproval)
            {
                Data.Run.StartUncertain=true; Save();
                try { await InterruptAsync(); } catch (Exception e) { Emit("Выход: " + e.GetType().Name); }
            }
            // Quota waiting survives a full exit. Explicit Stop/Pause flags always win on restore.
            await _server.DisposeAsync(); _lease.Dispose();
        }
        finally { _gate.Release(); }
    }
}
