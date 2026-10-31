using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexAutoContinue.Core;

internal sealed class WindowsJob : IDisposable
{
    private nint _handle;
    private WindowsJob(nint h) => _handle = h;
    public static WindowsJob? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObjectW(0, null);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var job = new WindowsJob(handle);
        var limits = new ExtendedLimit(); limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>()) || !AssignProcessToJobObject(handle, process.Handle))
        { var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error, "Не удалось привязать дочерние процессы к жизненному циклу приложения."); }
        return job;
    }
    public void Dispose() { var handle=Interlocked.Exchange(ref _handle,0); if (handle != 0) CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTime; public long PerJobUserTime; public uint LimitFlags; public nuint MinimumWorkingSet; public nuint MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
