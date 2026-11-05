using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Whether this process will be killed when the program that started it closes, because it runs inside that program's
/// Windows job with "kill on job close" set. Firefox runs native messaging hosts in such a job, and a NudgeJudge the
/// extension's host starts lands in it when it can't break away (<see cref="DetachedProcess"/>); so do children of
/// some terminals and IDEs. Such a NudgeJudge works, but goes away with that program, tray icon and all.
/// </summary>
public static class ProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    /// <summary>
    /// True inside a kill-on-close job (the innermost one; an outer job isn't visible from here). Always false outside
    /// Windows, where a detached NudgeJudge has its own session. Never throws.
    /// </summary>
    public static bool ClosesWithLauncher()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return QueryOwnJob() is { } flags && (flags & KillOnJobClose) != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The limit flags of the job this process is in, or null when it isn't in one.</summary>
    [SupportedOSPlatform("windows")]
    private static uint? QueryOwnJob()
    {
        // A null job handle means "the job this process is in"; the call fails when there's none.
        return QueryInformationJobObject(0, JobObjectExtendedLimitInformation, out var info,
            Marshal.SizeOf<ExtendedLimitInformation>(), out _)
            ? info.Basic.LimitFlags
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int infoClass, out ExtendedLimitInformation info, int length,
        out int returnLength);
}
