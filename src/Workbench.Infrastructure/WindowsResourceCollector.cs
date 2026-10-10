using System.Diagnostics;
using System.Runtime.InteropServices;
using Workbench.Core;

namespace Workbench.Infrastructure;

/// <summary>Windows/NVML telemetry sampled by a background service, without starting nvidia-smi processes.</summary>
public sealed class WindowsResourceCollector : IResourceCollector
{
    private ulong _idle, _kernel, _user;
    private bool _cpuBaseline;
    private readonly PdhCoreCounters _cores = new();
    private readonly NvmlDevice _gpu = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _processBaseline = new();
    private static readonly string[] RuntimeNames = ["Workbench.Desktop", "LLMWorkbench", "llama-server", "sd-server", "sd-cli", "crispasr", "ace-server", "ace-synth"];
    public ResourceSnapshot Read()
    {
        var now = DateTimeOffset.UtcNow;
        var unavailable = new List<string>();
        double? cpu = null; MemoryUsage? memory = null;
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                if (_cpuBaseline)
                {
                    var elapsed = kernel.Value - _kernel + user.Value - _user;
                    cpu = elapsed > 0 ? Math.Clamp(100d * (elapsed - Math.Min(elapsed, idle.Value - _idle)) / elapsed, 0, 100) : null;
                }
                _idle = idle.Value; _kernel = kernel.Value; _user = user.Value; _cpuBaseline = true;
            }
            var perf = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
            if (GetPerformanceInfo(ref perf, perf.Size))
            {
                var page = (ulong)perf.PageSize;
                memory = new((ulong)perf.PhysicalTotal * page, (ulong)perf.PhysicalAvailable * page, (ulong)perf.CommitTotal * page, (ulong)perf.CommitLimit * page);
            }
        }
        if (cpu is null) unavailable.Add(_cpuBaseline ? "CPU baseline: waiting for the next sample" : "CPU telemetry unavailable");
        if (memory is null) unavailable.Add("RAM telemetry unavailable");
        var cores = _cores.Read();
        if (cores.Length == 0) unavailable.Add("Per-core counters unavailable or warming up");
        var gpu = _gpu.Read();
        if (gpu is null) unavailable.Add("NVIDIA telemetry unavailable");
        var processes = ReadProcesses(now);
        return new(now, cpu, cores, memory, gpu, processes, [.. unavailable]);
    }
    private ProcessUsage[] ReadProcesses(DateTimeOffset now)
    {
        var samples = new List<ProcessUsage>(); var seen = new HashSet<int>();
        foreach (var name in RuntimeNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (!seen.Add(process.Id)) continue;
                        var cpu = process.TotalProcessorTime;
                        double? usage = null;
                        if (_processBaseline.TryGetValue(process.Id, out var previous) && now > previous.At && cpu >= previous.Cpu)
                            usage = Math.Clamp((cpu - previous.Cpu).TotalSeconds / (now - previous.At).TotalSeconds / Environment.ProcessorCount * 100, 0, 100);
                        _processBaseline[process.Id] = (cpu, now);
                        samples.Add(new(process.Id, process.ProcessName, process.Id == Environment.ProcessId ? "Workbench" : ManagedProcessLifetime.IsManaged(process.Id) ? "Managed" : "External", usage, process.WorkingSet64, process.PrivateMemorySize64));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
            }
        }
        foreach (var id in _processBaseline.Keys.Where(id => !seen.Contains(id)).ToArray()) _processBaseline.Remove(id);
        return samples.OrderByDescending(p => p.WorkingSet).ToArray();
    }
    public void Dispose() { _cores.Dispose(); _gpu.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);

    private sealed class PdhCoreCounters : IDisposable
    {
        private IntPtr _query, _counter;
        private bool _sampled;
        public PdhCoreCounters()
        {
            if (!OperatingSystem.IsWindows()) return;
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            if (PdhAddEnglishCounter(_query, @"\Processor(*)\% Processor Time", IntPtr.Zero, out _counter) != 0) _counter = IntPtr.Zero;
        }
        public double[] Read()
        {
            if (_counter == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return [];
            if (!_sampled) { _sampled = true; return []; }
            uint bytes = 0, count = 0;
            var result = PdhGetFormattedCounterArray(_counter, 0x200, ref bytes, ref count, IntPtr.Zero);
            if (result != 0x800007D2 || bytes == 0) return [];
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, 0x200, ref bytes, ref count, buffer) != 0) return [];
                var readings = new List<(int Index, double Value)>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + i * Marshal.SizeOf<CounterItem>());
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (int.TryParse(name, out var index) && item.Value.Status <= 1)
                        readings.Add((index, Math.Clamp(item.Value.Number, 0, 100)));
                }
                return readings.OrderBy(x => x.Index).Select(x => x.Value).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }
        [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
        [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    }

    private sealed class NvmlDevice : IDisposable
    {
        private IntPtr _device;
        private bool _initialised;
        private string _name = "NVIDIA GPU";
        public NvmlDevice()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (nvmlInit_v2() != 0) return;
                _initialised = true;
                if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0) return;
                var name = new byte[96];
                if (nvmlDeviceGetName(_device, name, (uint)name.Length) == 0) _name = System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0');
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { _device = IntPtr.Zero; }
        }
        public GpuUsage? Read()
        {
            if (_device == IntPtr.Zero) return null;
            double? utilization = nvmlDeviceGetUtilizationRates(_device, out var use) == 0 ? use.Gpu : null;
            var hasMemory = nvmlDeviceGetMemoryInfo(_device, out var memory) == 0;
            double? temperature = nvmlDeviceGetTemperature(_device, 0, out var temp) == 0 ? temp : null;
            double? power = nvmlDeviceGetPowerUsage(_device, out var milliwatts) == 0 ? milliwatts / 1000d : null;
            double? clocks = nvmlDeviceGetClockInfo(_device, 0, out var mhz) == 0 ? mhz : null;
            return new(_name, utilization, hasMemory ? memory.Used : null, hasMemory ? memory.Total : null, temperature, power, clocks);
        }
        public void Dispose() { if (_initialised) nvmlShutdown(); }
        [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temp);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint power);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, uint clock, out uint mhz);
    }
}
