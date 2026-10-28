using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentRunner.WorkContainers;

/// <summary>
/// Windows bound for the worktree process executor: one Job Object per attempt
/// with kill-on-close, an active-process ceiling, a job memory ceiling from the
/// same grant, and a hard CPU rate cap. Closing the handle kills every process
/// still in the tree, including detached descendants.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessTreeBound : IDisposable
{
    private IntPtr _job;

    private WindowsProcessTreeBound(IntPtr job) => _job = job;

    public static WindowsProcessTreeBound Create(WorkContainerLimits limits)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new InvalidOperationException($"CreateJobObject failed (Win32 {Marshal.GetLastWin32Error()}).");
        try
        {
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_JOB_MEMORY,
                    ActiveProcessLimit = (uint)limits.PidsLimit,
                },
                JobMemoryLimit = (UIntPtr)(ulong)limits.MemoryBytes,
            };
            Set(job, JobObjectExtendedLimitInformation, extended);
            // CpuRate is in 1/100 of a percent of the whole machine.
            var rate = (uint)Math.Clamp(limits.HalfCores * 5000L / Math.Max(1, Environment.ProcessorCount), 1, 10_000);
            Set(job, JobObjectCpuRateControlInformation, new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = rate,
            });
            return new WindowsProcessTreeBound(job);
        }
        catch
        {
            CloseHandle(job);
            throw;
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.Handle))
            throw new InvalidOperationException($"AssignProcessToJobObject failed (Win32 {Marshal.GetLastWin32Error()}).");
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero) return;
        CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    private static void Set<T>(IntPtr job, int informationClass, T value) where T : struct
    {
        var length = Marshal.SizeOf<T>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            if (!SetInformationJobObject(job, informationClass, pointer, (uint)length))
                throw new InvalidOperationException($"SetInformationJobObject({informationClass}) failed (Win32 {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
