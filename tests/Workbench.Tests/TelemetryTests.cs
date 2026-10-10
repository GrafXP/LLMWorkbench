using Workbench.Core;
using Workbench.Infrastructure;
using Xunit;

namespace Workbench.Tests;

public class TelemetryTests
{
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
