using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAutoContinue.Core;

public sealed class StateStore(string directory) : IStateStore
{
    private readonly object _sync = new();
    public string DirectoryPath { get; } = directory;
    public string? LoadWarning { get; private set; }
    public AppData Load()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "state.json");
            if (!File.Exists(path)) return new();
            try
            {
                var d = JsonSerializer.Deserialize<AppData>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new JsonException();
                if (d.FormatVersion != 1 || d.Settings is null || d.Run is null || !Enum.IsDefined(d.Run.State)) throw new JsonException();
                d.Settings.PollSeconds = Math.Clamp(d.Settings.PollSeconds, 30, 900);
                if (string.IsNullOrWhiteSpace(d.Settings.ContinueMessage)) d.Settings.ContinueMessage = new AppSettings().ContinueMessage;
                return d;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                LoadWarning = "Файл состояния повреждён или недоступен. Автоматическое восстановление отключено.";
                if (e is JsonException) File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true);
                return new() { Settings = new() { RestoreWaiting = false } };
            }
        }
    }
    public void Save(AppData data)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var target = Path.Combine(DirectoryPath, "state.json");
            var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { JsonSerializer.Serialize(fs, data, new JsonSerializerOptions { WriteIndented = true }); fs.Flush(true); }
                File.Move(tmp, target, true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}

public sealed class FileRunLease(string directory) : IRunLease
{
    private FileStream? _stream;
    private string? _thread;
    public void Acquire(string threadId)
    {
        if (_thread == threadId && _stream is not null) return;
        Release();
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(threadId)));
        try { _stream = new FileStream(Path.Combine(directory, hash + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); _thread = threadId; }
        catch (IOException) { throw new InvalidOperationException("Эта сессия уже передана другому экземпляру Codex AutoContinue."); }
    }
    public void Release() { _stream?.Dispose(); _stream = null; _thread = null; }
    public void Dispose() => Release();
}

public static partial class Redactor
{
    [GeneratedRegex(@"(?i)(Bearer\s+)[^\s\""']+|\bsk-[A-Za-z0-9_-]{8,}|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"(?i)([\""']?(?:access_token|refresh_token|id_token|api_key|apikey|password|authorization|client_secret|userCode|authUrl)[\""']?\s*[:=]\s*)(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;}]+)")]
    private static partial Regex FieldRegex();
    public static string Clean(string text) => FieldRegex().Replace(TokenRegex().Replace(text, "[секрет скрыт]"), "$1[секрет скрыт]");
}

public sealed class RotatingLog(string directory)
{
    private readonly object _sync = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    public string CurrentPath => Path.Combine(directory, "events.log");
    public void Write(string message)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length > MaxBytes)
            {
                for (var i = 4; i >= 1; i--)
                {
                    var source = i == 1 ? CurrentPath : CurrentPath + "." + (i-1);
                    if (File.Exists(source)) File.Move(source, CurrentPath + "." + i, true);
                }
            }
            File.AppendAllText(CurrentPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {Redactor.Clean(message)}\n", Encoding.UTF8);
        }
    }
    public void Clear()
    {
        lock (_sync) for (int i = 0; i <= 4; i++) { var p = i == 0 ? CurrentPath : CurrentPath + "." + i; if (File.Exists(p)) File.Delete(p); }
    }
}
