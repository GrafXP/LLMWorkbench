using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class TelemetryService(IResourceCollector collector) : IDisposable
{
    public ResourceHistory History { get; } = new();
    public ResourceSnapshot? Latest { get; private set; }
    public event Action<ResourceSnapshot>? Sampled;
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            ResourceSnapshot sample;
            try { sample = collector.Read(); }
            catch (Exception ex) { sample = new(DateTimeOffset.UtcNow, null, [], null, null, [], [$"Telemetry unavailable: {ex.Message}"]); }
            Latest = sample; History.Add(sample); Sampled?.Invoke(sample);
        } while (await timer.WaitForNextTickAsync(ct));
    }
    public void Dispose() => collector.Dispose();
}
