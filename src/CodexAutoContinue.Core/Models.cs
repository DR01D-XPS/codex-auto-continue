using System.Text.Json;

namespace CodexAutoContinue.Core;

public enum RunState { Ready, Running, WaitingLimit, Offline, NeedsLogin, AwaitingApproval, Paused, Completed, Error }

public sealed class AppSettings
{
    public string CliPath { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public bool RestoreWaiting { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string ContinueMessage { get; set; } = "Продолжи исходную задачу с места остановки. Сначала проверь текущие файлы, последние изменения и состояние запущенных процессов. Сохрани уже выполненную работу и выполняй оставшиеся пункты. Если исходная задача завершена, сообщи результат и остановись";
}

public sealed class SavedRun
{
    public string ProjectPath { get; set; } = "";
    public string OriginalPrompt { get; set; } = "";
    public string? ThreadId { get; set; }
    public string? TurnId { get; set; }
    public string? Model { get; set; }
    public string? PendingMessageId { get; set; }
    public int BaselineTurnCount { get; set; }
    public string? BaselineLastTurnId { get; set; }
    public bool StartUncertain { get; set; }
    public bool AutoContinue { get; set; }
    public bool UserStopped { get; set; }
    public bool Paused { get; set; }
    public bool Owned { get; set; }
    public bool HandoffAcquired { get; set; }
    public bool CreatedByApp { get; set; }
    public bool HasSubmittedTurn { get; set; }
    public bool CreatingThread { get; set; }
    public RunState State { get; set; } = RunState.Ready;
    public DateTimeOffset? NextCheck { get; set; }
    public DateTimeOffset? WaitUntil { get; set; }
    public string Detail { get; set; } = "Выберите проект и задачу.";
}

public sealed class AppData
{
    public int FormatVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public SavedRun Run { get; set; } = new();
}

public record RpcEvent(string Method, JsonElement Params);
public record ServerRequest(JsonElement Id, string Method, JsonElement Params);
public record SessionInfo(string Id, string Name, string Cwd, DateTimeOffset UpdatedAt, string Status, string Source, string? Model)
{
    public string LocalUpdated => UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string StatusLabel => Status switch { "active" => "Активна в этом сервере", "idle" => "Ожидает", "notLoaded" => "Не загружена", "systemError" => "Ошибка", _ => Status };
    public static SessionInfo FromJson(JsonElement t) => new(t.Str("id"), t.Str("name", t.Str("preview", "Без названия")).Replace('\n',' '), t.Str("cwd"),
        DateTimeOffset.FromUnixTimeSeconds(t.Long("updatedAt") ?? 0), t.Obj("status").Str("type", "неизвестно"), t.Str("source", "неизвестно"), t.OptionalStr("model"));
}

public static class JsonExt
{
    public static JsonElement Obj(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string Str(this JsonElement e, string key, string fallback = "") => e.Obj(key) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    public static string? OptionalStr(this JsonElement e, string key) => e.Obj(key).ValueKind == JsonValueKind.String ? e.Obj(key).GetString() : null;
    public static long? Long(this JsonElement e, string key) => e.Obj(key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    public static double? Number(this JsonElement e, string key) => e.Obj(key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
    public static bool? Bool(this JsonElement e, string key) => e.Obj(key).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    public static JsonElement[] Array(this JsonElement e, string key) => e.Obj(key).ValueKind == JsonValueKind.Array ? e.Obj(key).EnumerateArray().ToArray() : [];
    public static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);
}

public interface IAppServer : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<RpcEvent>? Notification;
    event Action<ServerRequest>? Request;
    event Action<string>? Disconnected;
    Task ConnectAsync(string executable, CancellationToken ct = default);
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct = default);
    Task RespondAsync(JsonElement id, object result, CancellationToken ct = default);
    Task RejectAsync(JsonElement id, string message, CancellationToken ct = default);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public interface IStateStore { AppData Load(); void Save(AppData data); }
public interface IRunLease : IDisposable { void Acquire(string threadId); void Release(); }

public sealed class RpcException(int code, string message, JsonElement data) : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement DataJson { get; } = data;
}
