using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed record RuntimeStatus(string State, string Message, string? ProfileId = null, int? Pid = null);

/// <summary>Called while holding the shared GPU lease. Owns exactly one worker, on a private loopback port.</summary>
public sealed class RuntimeManager(Func<ResourceSnapshot?>? resources = null) : IAsyncDisposable
{
    private Process? _process;
    private HttpClient? _http;
    private string? _identity;
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask;
    private readonly Queue<string> _tail = new();
    private readonly object _logGate = new();
    public RuntimeStatus Status { get; private set; } = new("Idle", "No managed model loaded");
    public event Action<RuntimeStatus>? Changed;
    public event Action<string>? Log;
    public int? ManagedPid
    {
        get { try { return _process is { HasExited: false } p ? p.Id : null; } catch (InvalidOperationException) { return null; } }
    }
    private void State(string state, string message, ModelProfile? profile = null)
    {
        Record($"runtime {state}: {message}");
        Status = new(state, message, profile?.Id, ManagedPid); Changed?.Invoke(Status);
    }
    public async Task<HttpClient> EnsureAsync(ModelProfile profile, CancellationToken ct)
    {
        if (!profile.Available) throw new FileNotFoundException($"{profile.Name}: missing {string.Join(", ", profile.MissingFiles)}");
        var identity = DocumentJson.Write(profile);
        if (_process is { HasExited: false } && _identity == identity && _http is not null) return _http;
        await UnloadAsync();
        var external = new[] { "llama-server", "sd-server", "sd-cli" }.SelectMany(Process.GetProcessesByName).ToArray();
        try
        {
            // Windows may enumerate a just-exited owned worker until its final handles close.
            var live = external.Where(p => !p.HasExited).ToArray();
            if (live.Length > 0) throw new InvalidOperationException($"An external inference process is running (PID {string.Join(", ", live.Select(p => p.Id))}). Close it before running v2; it has been left running.");
        }
        finally { foreach (var p in external) p.Dispose(); }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (resources?.Invoke() is { } sample && DateTimeOffset.UtcNow - sample.Timestamp < TimeSpan.FromSeconds(5) &&
            (sample.Memory is { } ram && profile.EstimatedRamBytes > 0 && ram.Available < (ulong)profile.EstimatedRamBytes ||
             sample.Gpu is { VramTotal: { } total, VramUsed: { } used } && profile.EstimatedVramBytes > 0 && total - Math.Min(total, used) < (ulong)profile.EstimatedVramBytes))
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException($"Insufficient measured memory headroom for {profile.Name}. Close memory-heavy applications and retry.");
            State("Waiting", $"Waiting for RAM/VRAM headroom for {profile.Name}", profile); await Task.Delay(500, ct);
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var args = new List<string>();
        foreach (var file in profile.Files) args.AddRange([file.Key, file.Value]);
        args.AddRange(profile.Arguments);
        args.AddRange(profile.Runtime == RuntimeKind.Llama ? ["--host", "127.0.0.1", "--port", port.ToString()] : ["--listen-ip", "127.0.0.1", "--listen-port", port.ToString()]);
        var start = new ProcessStartInfo(profile.Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(profile.Executable)! };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        lock (_logGate) _tail.Clear();
        Record($"launch {profile.Id}: {profile.Executable} {DocumentJson.Write(args)}");
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var owned = _process;
        owned.Exited += (_, _) =>
        {
            try { if (ReferenceEquals(_process, owned) && Status.State != "Unloading") State("Exited", $"{profile.Name} exited ({owned.ExitCode}, 0x{owned.ExitCode:X8})", profile); }
            catch (InvalidOperationException) { /* the unload path disposed an already-observed exit */ }
        };
        try
        {
            _process.Start(); ManagedProcessLifetime.Adopt(_process);
            _stdout = PumpAsync(_process.StandardOutput, "stdout"); _stderr = PumpAsync(_process.StandardError, "stderr");
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.ConnectionClose = true;
            _identity = identity; State("Loading", $"Loading {profile.Name}", profile);
            using var load = CancellationTokenSource.CreateLinkedTokenSource(ct); load.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                load.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException($"{profile.Name} exited ({_process.ExitCode}): {Tail()}");
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(load.Token); probe.CancelAfter(2000);
                    using var response = await _http.GetAsync(profile.Runtime == RuntimeKind.Llama ? "/health" : "/sdcpp/v1/capabilities", probe.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!load.IsCancellationRequested) { }
                await Task.Delay(300, load.Token);
            }
            State(profile.Runtime == RuntimeKind.Llama ? "Loaded" : "Ready", profile.Runtime == RuntimeKind.Llama ? $"Loaded {profile.Name}" : $"{profile.Name} ready; weights load on first render", profile);
            return _http;
        }
        catch { await UnloadAsync(); throw; }
    }
    private void Record(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}";
        lock (_logGate) { _tail.Enqueue(line); while (_tail.Count > 24) _tail.Dequeue(); }
        Log?.Invoke(line);
    }
    private async Task PumpAsync(StreamReader reader, string source)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                Record($"{source}: {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public string Tail() { lock (_logGate) return string.Join(" · ", _tail.TakeLast(4)); }
    public void ModelLoaded(ModelProfile profile) => State("Loaded", $"Loaded {profile.Name}", profile);
    public async Task UnloadAsync()
    {
        if (_process is null) return;
        var process = _process;
        State("Unloading", $"Unloading {Status.ProfileId}");
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        // Process exit is observed before the lease can pass to a different model.
        await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
        ManagedProcessLifetime.Forget(process.Id);
        _process = null; process.Dispose(); _http?.Dispose(); _http = null; _identity = null;
        State("Idle", "No managed model loaded");
    }
    public async ValueTask DisposeAsync() => await UnloadAsync();
}
