using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAutoContinue.Core;

public sealed class AppServerTransport : IAppServer
{
    private Process? _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1,1);
    private CancellationTokenSource _lifetime = new();
    private long _id;
    private bool _disposing;
    private bool _alive;
    private Task? _reader;
    private Task? _stderr;
    private WindowsJob? _job;
    public bool IsConnected => _alive && !_disposing && _process is { HasExited: false };
    internal int? ProcessId => _process?.Id;
    public event Action<RpcEvent>? Notification;
    public event Action<ServerRequest>? Request;
    public event Action<string>? Disconnected;

    public static ProcessStartInfo ProcessInfo(string executable, params string[] arguments)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите нативный codex.exe. Скрипты .cmd и WSL этой версией не поддерживаются.");
        var info = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
          StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }

    public async Task ConnectAsync(string executable, CancellationToken ct = default)
    {
        if (IsConnected) return;
        if (_process is not null || _job is not null) await DisposeAsync();
        _disposing = false;
        _lifetime.Dispose(); _lifetime = new();
        _process = Process.Start(ProcessInfo(executable, "app-server", "--listen", "stdio://")) ?? throw new IOException("Не удалось запустить CLI.");
        _alive=true;
        try { _job = WindowsJob.Attach(_process); }
        catch { _process.Kill(true); _process.Dispose(); _process = null; throw; }
        _reader = ReadLoopAsync(_lifetime.Token);
        // Raw server logs may contain credentials or user data; deliberately do not persist stderr.
        _stderr = DrainStderrAsync(_process, _lifetime.Token);
        try
        {
            await CallAsync("initialize", new { clientInfo = new { name = "codex_autocontinue", title = "Codex AutoContinue", version = "1.0.1" } }, ct);
            await SendAsync(new { method = "initialized", @params = new { } }, ct);
        }
        catch { await DisposeAsync(); throw; }
    }
    private static async Task DrainStderrAsync(Process p, CancellationToken ct)
    { try { while (await p.StandardError.ReadLineAsync(ct) is not null) { } } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { } }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("Соединение с Codex App Server закрыто.");
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, ct);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    public Task RespondAsync(JsonElement id, object result, CancellationToken ct = default) => SendAsync(new { id, result }, ct);
    public Task RejectAsync(JsonElement id, string message, CancellationToken ct = default) => SendAsync(new { id, error = new { code = -32601, message } }, ct);
    private async Task SendAsync(object value, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            if (!IsConnected) throw new IOException("App Server недоступен.");
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally { _write.Release(); }
    }
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        string reason = "Процесс Codex завершился.";
        try
        {
            while (await _process!.StandardOutput.ReadLineAsync(ct) is string line)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var method = root.OptionalStr("method");
                    var id = root.Obj("id");
                    if (method is not null)
                    {
                        if (id.ValueKind != JsonValueKind.Undefined) Request?.Invoke(new(id.Clone(), method, root.Obj("params").Clone()));
                        else Notification?.Invoke(new(method, root.Obj("params").Clone()));
                    }
                    else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) && _pending.TryGetValue(n, out var tcs))
                    {
                        var error = root.Obj("error");
                        if (error.ValueKind == JsonValueKind.Object) tcs.TrySetException(new RpcException((int)(error.Long("code") ?? 0), error.Str("message"), error.Obj("data").ValueKind == JsonValueKind.Undefined ? default : error.Obj("data").Clone()));
                        else tcs.TrySetResult(root.Obj("result").Clone());
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { reason = "Соединение с локальным App Server прервано: " + e.GetType().Name; }
        finally
        {
            _alive=false;
            _job?.Dispose();
            foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException(reason));
            if (!_disposing) Disconnected?.Invoke(reason);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        var p = _process;
        if (p is not null)
        {
            try { p.StandardInput.Close(); if (!p.HasExited) await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException) { if (!p.HasExited) p.Kill(true); }
        }
        _job?.Dispose(); _job = null; // Job kills all child processes, including terminals, on a crash or full exit.
        _lifetime.Cancel();
        if (_reader is not null) await _reader;
        if (_stderr is not null) await _stderr;
        p?.Dispose(); _process = null;
    }
}
