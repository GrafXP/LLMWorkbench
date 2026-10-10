using Workbench.Core;
using Workbench.Infrastructure;
using Xunit;

namespace Workbench.Tests;

public class TelemetryTests
{
    [Fact]
    public async Task CollectorFailureIsVisibleAndSamplingRecovers()
    {
        using var service = new TelemetryService(new FailingCollector());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Sampled += s => { if (s.CpuPercent.HasValue) recovered.TrySetResult(); };
        var sampling = service.RunAsync(cts.Token);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sampling);
        var history = service.History.Window(TimeSpan.FromMinutes(2));
        Assert.Null(history[0].CpuPercent);
        Assert.Contains("Telemetry unavailable", history[0].Unavailable[0]);
        Assert.Equal(50d, history[^1].CpuPercent);
    }
    private sealed class FailingCollector : IResourceCollector
    {
        private int _calls;
        public ResourceSnapshot Read()
        {
            if (++_calls == 1) throw new InvalidOperationException("Provider stopped");
            return new(DateTimeOffset.UtcNow, 50, [], null, null, [], []);
        }
        public void Dispose() { }
    }
    [Fact]
    public void HistoryBoundsSamplesAndMarkersAndPreservesUnavailableValues()
    {
        var history = new ResourceHistory(3); var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            history.Mark(new(start.AddSeconds(i), $"Job {i}"));
            history.Add(new(start.AddSeconds(i), null, [], null, null, [], ["Provider unavailable"]));
        }
        var samples = history.Window(TimeSpan.FromMinutes(2));
        Assert.Equal(3, samples.Length);
        Assert.All(samples, s => Assert.Null(s.CpuPercent));
        Assert.Equal(start.AddSeconds(2), samples[0].Timestamp);
        Assert.Equal(3, history.Markers(TimeSpan.FromMinutes(2)).Length);
    }
    [Fact]
    public async Task WindowsCollectorReadsMemoryAndCpuWithoutUiOrRuntimeProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var collector = new WindowsResourceCollector();
        var first = collector.Read();
        await Task.Delay(1100);
        var next = collector.Read();
        Assert.NotNull(next.Memory);
        Assert.True(next.Memory!.Total > 0);
        Assert.InRange(next.Memory.Available, 0ul, next.Memory.Total);
        Assert.NotNull(next.CpuPercent);
        Assert.InRange(next.CpuPercent!.Value, 0, 100);
        Assert.True(next.Timestamp > first.Timestamp);
        Assert.All(next.LogicalCores, c => Assert.InRange(c, 0, 100));
    }
}
