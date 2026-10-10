using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Workbench.Infrastructure;

internal static class ManagedProcessLifetime
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> Owned = new();
        public static bool IsManaged(int pid) => Owned.ContainsKey(pid);
        public static void Forget(int pid) => Owned.TryRemove(pid, out _);
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        // Deliberately never closed: the handle going away with the process is the mechanism.
        private static readonly IntPtr Job = Create();

        public static void Adopt(Process process)
        {
            Owned[process.Id] = 0;
            if (Job == IntPtr.Zero) return;
            try { AssignProcessToJobObject(Job, process.Handle); }
            catch { /* best effort - Dispose still covers the normal exit path */ }
        }

        private static IntPtr Create()
        {
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                var ok = SetInformationJobObject(
                    job, JobObjectExtendedLimitInformation, ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                return ok ? job : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
