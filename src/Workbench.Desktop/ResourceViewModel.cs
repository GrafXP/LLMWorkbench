using System.Collections.ObjectModel;
using Workbench.Core;
using Workbench.Infrastructure;

namespace Workbench.Desktop;

public sealed class ResourceViewModel(TelemetryService service) : Bindable
{
    public ResourceSnapshot? Snapshot { get; private set; }
    public int[] Windows { get; } = [2, 10, 60];
    private int _minutes = 2;
    public int Minutes { get => _minutes; set { if (Set(ref _minutes, value)) Refresh(); } }
    public ResourceSnapshot[] Samples { get; private set; } = [];
    public ResourceMarker[] Markers { get; private set; } = [];
    public DateTimeOffset? HighlightStart { get; set; }
    public DateTimeOffset? HighlightEnd { get; set; }
    public ObservableCollection<ProcessUsage> Processes { get; } = [];
    public ObservableCollection<CoreReading> Cores { get; } = [];
    public string Cpu => Percent(Snapshot?.CpuPercent);
    public string Ram => Snapshot?.Memory is { } m ? $"{GiB(m.Used)} / {GiB(m.Total)} GiB" : "Unavailable";
    public string RamDetail => Snapshot?.Memory is { } m ? $"Available {GiB(m.Available)} GiB · Commit {GiB(m.CommitUsed)} / {GiB(m.CommitLimit)} GiB" : "Waiting for Windows memory telemetry";
    public string Gpu => Percent(Snapshot?.Gpu?.Utilization);
    public string GpuName => Snapshot?.Gpu?.Name ?? "GPU telemetry unavailable";
    public string GpuDetail => Snapshot?.Gpu is { } g ? $"Temperature {Number(g.Temperature, "°C")} · Power {Number(g.PowerWatts, " W")} · Clock {Number(g.ClockMhz, " MHz")}" : "NVIDIA provider unavailable";
    public string Vram => Snapshot?.Gpu is { VramUsed: { } used, VramTotal: { } total } ? $"{GiB(used)} / {GiB(total)} GiB" : "Unavailable";
    public string VramDetail => Snapshot?.Gpu is { VramUsed: { } used, VramTotal: { } total } ? $"Free {GiB(total - Math.Min(total, used))} GiB · Device totals include other applications" : "Dedicated VRAM reading unavailable";
    public string Freshness => Snapshot is null ? "Waiting for resource samples" : $"Updated {Snapshot.Timestamp.ToLocalTime():HH:mm:ss} · One sample per second";
    public string Availability => Snapshot is null ? "" : string.Join(" · ", Snapshot.Unavailable);
    public string RuntimeSummary => Snapshot?.Processes.Where(p => p.Ownership == "External").ToArray() is { Length: > 0 } external
        ? "External runtimes: " + string.Join(", ", external.Select(p => $"{p.Name} (PID {p.Pid})"))
        : "No external inference runtimes detected";
    public string Compact => $"CPU {Cpu}    RAM {Ram}    GPU {Gpu}    VRAM {Vram}";
    public void Update(ResourceSnapshot snapshot)
    {
        Snapshot = snapshot;
        Processes.Clear(); foreach (var p in snapshot.Processes) Processes.Add(p);
        Cores.Clear(); for (var i = 0; i < snapshot.LogicalCores.Length; i++) Cores.Add(new(i, snapshot.LogicalCores[i]));
        Refresh();
        foreach (var name in new[] { nameof(Cpu), nameof(Ram), nameof(RamDetail), nameof(Gpu), nameof(GpuName), nameof(GpuDetail), nameof(Vram), nameof(VramDetail), nameof(Freshness), nameof(Availability), nameof(Compact), nameof(RuntimeSummary) }) Notify(name);
    }
    public void Refresh()
    {
        Samples = service.History.Window(TimeSpan.FromMinutes(Minutes));
        Markers = service.History.Markers(TimeSpan.FromMinutes(Minutes));
        Notify(nameof(Samples)); Notify(nameof(Markers));
    }
    private static string Percent(double? value) => value.HasValue ? $"{value:0}%" : "Unavailable";
    private static string GiB(ulong bytes) => (bytes / 1073741824d).ToString("0.0");
    private static string Number(double? value, string unit) => value.HasValue ? $"{value:0}{unit}" : "Unavailable";
}
public sealed record CoreReading(int Index, double Percent)
{
    public string Label => $"Core {Index}";
    public string Value => $"{Percent:0}%";
}
