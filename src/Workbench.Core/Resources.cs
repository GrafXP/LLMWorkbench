namespace Workbench.Core;

public sealed record MemoryUsage(ulong Total, ulong Available, ulong CommitUsed, ulong CommitLimit)
{
    public ulong Used => Total - Math.Min(Total, Available);
}
public sealed record GpuUsage(string Name, double? Utilization, ulong? VramUsed, ulong? VramTotal, double? Temperature, double? PowerWatts, double? ClockMhz);
public sealed record ProcessUsage(int Pid, string Name, string Ownership, double? CpuPercent, long WorkingSet, long PrivateBytes);
public sealed record ResourceSnapshot(DateTimeOffset Timestamp, double? CpuPercent, double[] LogicalCores, MemoryUsage? Memory, GpuUsage? Gpu,
    ProcessUsage[] Processes, string[] Unavailable);
public sealed record ResourceMarker(DateTimeOffset Timestamp, string Description, Guid? JobId = null);
public interface IResourceCollector : IDisposable { ResourceSnapshot Read(); }

public sealed class ResourceHistory(int capacity = 3600)
{
    private readonly Queue<ResourceSnapshot> _samples = new();
    private readonly Queue<ResourceMarker> _markers = new();
    private readonly object _gate = new();
    public void Add(ResourceSnapshot snapshot)
    {
        lock (_gate)
        {
            _samples.Enqueue(snapshot);
            while (_samples.Count > capacity) _samples.Dequeue();
            if (_samples.TryPeek(out var first))
                while (_markers.TryPeek(out var marker) && marker.Timestamp < first.Timestamp) _markers.Dequeue();
        }
    }
    public void Mark(ResourceMarker marker) { lock (_gate) { _markers.Enqueue(marker); while (_markers.Count > capacity) _markers.Dequeue(); } }
    public ResourceSnapshot[] Window(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return [];
            var start = _samples.Last().Timestamp - duration;
            return _samples.Where(s => s.Timestamp >= start).ToArray();
        }
    }
    public ResourceMarker[] Markers(TimeSpan duration)
    {
        lock (_gate)
        {
            var end = _samples.Count > 0 ? _samples.Last().Timestamp : DateTimeOffset.UtcNow;
            return _markers.Where(m => m.Timestamp >= end - duration).ToArray();
        }
    }
}
