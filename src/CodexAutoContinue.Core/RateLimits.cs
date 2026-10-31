using System.Text.Json;

namespace CodexAutoContinue.Core;

public record LimitWindow(string Bucket, string Label, double UsedPercent, long? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public string ResetLabel => ResetsAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "Время сброса неизвестно";
}
public enum Availability { Available, Blocked, Unknown }
public record LimitDecision(Availability Availability, DateTimeOffset? WaitUntil, bool UnknownReset, string Reason);

public sealed class RateLimits
{
    public List<LimitWindow> Windows { get; } = [];
    public bool HasOrdinaryUsageField { get; private set; }
    public bool? OrdinaryUsageAllowed { get; private set; }
    public string? AccountId { get; private set; }
    public bool BackendBlocked { get; private set; }
    public bool UnknownBucket { get; private set; }

    public static RateLimits Parse(JsonElement response, string? selectedModel = null)
    {
        var result = new RateLimits
        {
            HasOrdinaryUsageField = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("ordinaryUsageAllowed", out _),
            OrdinaryUsageAllowed = response.Bool("ordinaryUsageAllowed"), AccountId = response.OptionalStr("accountId")
        };
        var all = response.Obj("rateLimitsByLimitId");
        var buckets = all.ValueKind == JsonValueKind.Object
            ? all.EnumerateObject().Select(p => (p.Name, p.Value)).ToList()
            : new List<(string, JsonElement)>();
        if (buckets.Count == 0 && response.Obj("rateLimits").ValueKind == JsonValueKind.Object)
            buckets.Add((response.Obj("rateLimits").Str("limitId", "codex"), response.Obj("rateLimits")));
        // General Codex quota and the selected model's explicitly identified quota apply.
        // An unidentifiable depleted quota requires manual review instead of an optimistic retry.
        foreach (var (id, bucket) in buckets)
        {
            var relevant = id == "codex" || id == selectedModel || bucket.Str("normalModelSlug") == selectedModel || buckets.Count == 1;
            var reached = bucket.OptionalStr("rateLimitReachedType");
            var windows = new List<LimitWindow>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                var w = bucket.Obj(key);
                if (w.ValueKind != JsonValueKind.Object) continue;
                var used = w.Number("usedPercent");
                if (used is null || used < 0 || used > 100 || !double.IsFinite(used.Value)) { result.UnknownBucket = true; continue; }
                var duration = w.Long("windowDurationMins");
                var label = duration >= 10080 ? "Недельное окно" : duration == 300 ? "Окно 5 часов" : duration is > 0 ? $"Окно {duration} мин" : key == "primary" ? "Основное окно" : "Дополнительное окно";
                DateTimeOffset? reset = null;
                if (w.Long("resetsAt") is long unix && unix >= 0 && unix <= 253402300799) reset = DateTimeOffset.FromUnixTimeSeconds(unix);
                windows.Add(new(id, label, used.Value, duration, reset));
            }
            if (relevant)
            {
                result.Windows.AddRange(windows);
                result.BackendBlocked |= reached is not null || bucket.Bool("spendControlReached") == true;
            }
            else if (windows.Any(w => w.UsedPercent >= 100) || reached is not null || bucket.Bool("spendControlReached") == true)
                result.UnknownBucket = true;
        }
        return result;
    }

    public LimitDecision Evaluate()
    {
        var depleted = Windows.Where(w => w.UsedPercent >= 100).ToList();
        var unknownReset = depleted.Any(w => w.ResetsAt is null);
        DateTimeOffset? wait = depleted.Where(w => w.ResetsAt is not null).Select(w => w.ResetsAt).DefaultIfEmpty(null).Max();
        if (OrdinaryUsageAllowed == false || BackendBlocked || depleted.Count > 0)
            return new(Availability.Blocked, wait, unknownReset || depleted.Count == 0, "Обычное использование пока недоступно. Ожидаем все исчерпанные окна.");
        if (UnknownBucket || Windows.Count == 0)
            return new(Availability.Unknown, null, true, "Сервер не сообщил достаточно данных о применимых лимитах.");
        if (HasOrdinaryUsageField && OrdinaryUsageAllowed is null)
            return new(Availability.Unknown, null, true, "Сервер не подтвердил доступ к обычному использованию (ordinaryUsageAllowed неизвестно).");
        return new(Availability.Available, null, false, "Обычное использование доступно.");
    }
}

public static class ErrorClassifier
{
    public static RunState Classify(JsonElement error)
    {
        var info = error.Obj("codexErrorInfo");
        if (info.ValueKind == JsonValueKind.Undefined) info = error.Obj("data").Obj("codexErrorInfo");
        var tag = info.ValueKind == JsonValueKind.String ? info.GetString()! : info.ValueKind == JsonValueKind.Object ? info.EnumerateObject().FirstOrDefault().Name ?? "" : "";
        var http = info.Long("httpStatusCode");
        if (info.ValueKind == JsonValueKind.Object && http is null)
            foreach (var p in info.EnumerateObject()) http ??= p.Value.Long("httpStatusCode");
        if (tag.Equals("usageLimitExceeded", StringComparison.OrdinalIgnoreCase)) return RunState.WaitingLimit;
        if (tag.Equals("unauthorized", StringComparison.OrdinalIgnoreCase) || http is 401 or 403) return RunState.NeedsLogin;
        if ((tag.Contains("Connection", StringComparison.OrdinalIgnoreCase) || tag.Contains("Disconnected", StringComparison.OrdinalIgnoreCase)) && http is null)
            return RunState.Offline;
        return RunState.Error; // HTTP 429, rateLimitExceeded, service errors are not subscription quota exhaustion.
    }
}
