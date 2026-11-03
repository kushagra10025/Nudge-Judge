using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Linux: <c>/proc/&lt;pid&gt;/stat</c> for the parent and start time, <c>/proc/&lt;pid&gt;/exe</c> for the program.
/// Friendly names come from the app's <c>.desktop</c> file when one is named after the program.
/// There's no portable foreground-window API (Wayland has none), so that fallback is absent.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxProcessTable(string procRoot = "/proc", IReadOnlyList<string>? dataDirs = null) : IProcessTable
{
    private IReadOnlyList<string> DataDirs => dataDirs ?? DiscoveryPaths.FromEnvironment().DataDirs;

    public ProcessInfo? Get(int pid)
    {
        if (pid <= 0) return null;
        var dir = Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture));
        string stat;
        try
        {
            stat = File.ReadAllText(Path.Combine(dir, "stat"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return ParseStat(pid, stat, ReadExe(dir));
    }

    /// <summary>
    /// <c>pid (comm) state ppid … starttime …</c>. The name can contain spaces and parentheses, so fields are
    /// counted from the last <c>)</c>.
    /// </summary>
    internal static ProcessInfo? ParseStat(int pid, string stat, string? exe)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var name = stat[(open + 1)..close];
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] is field 3 (state); ppid is field 4, starttime is field 22.
        if (fields.Length < 20 || !int.TryParse(fields[1], CultureInfo.InvariantCulture, out var ppid)) return null;
        long? started = long.TryParse(fields[19], CultureInfo.InvariantCulture, out var s) ? s : null;
        return new ProcessInfo(pid, ppid, name, exe, started);
    }

    private static string? ReadExe(string dir)
    {
        try
        {
            var target = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
            if (string.IsNullOrEmpty(target)) return null;
            const string deleted = " (deleted)"; // the program was updated while running
            return target.EndsWith(deleted, StringComparison.Ordinal) ? target[..^deleted.Length] : target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public int? ForegroundProcessId => null;

    public string? Describe(ProcessInfo process)
    {
        var name = process.Path is { Length: > 0 } p ? Path.GetFileName(p) : process.Name;
        return DesktopName(name + ".desktop");
    }

    /// <summary>Electron and Chromium apps set <c>CHROME_DESKTOP</c> to their desktop file, and it's inherited through <c>xdg-open</c>.</summary>
    public SourceApp? EnvironmentHint
    {
        get
        {
            var id = Environment.GetEnvironmentVariable("CHROME_DESKTOP");
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Length > 255) return null;
            var name = id.EndsWith(".desktop", StringComparison.Ordinal) ? id[..^8] : id;
            return SourceApp.Sanitize(new SourceApp(name, null, DesktopName(name + ".desktop")));
        }
    }

    private string? DesktopName(string desktopId)
    {
        foreach (var dir in DataDirs)
        {
            var file = Path.Combine(dir, "applications", desktopId);
            if (!File.Exists(file)) continue;
            return IniFile.TryLoad(file)?.Get("Desktop Entry", "Name");
        }
        return null;
    }
}

/// <summary>
/// Windows: a Toolhelp snapshot for parents and image names, process times to spot recycled pids, the file
/// description for a friendly name (<c>ms-teams.exe</c> → "Microsoft Teams"), and the foreground window's
/// process for apps whose links arrive through a broker (packaged apps, Explorer).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessTable : IProcessTable
{
    private Dictionary<int, (int Parent, string Exe)>? _snapshot;

    public ProcessInfo? Get(int pid)
    {
        _snapshot ??= Snapshot();
        if (!_snapshot.TryGetValue(pid, out var entry)) return null;
        var (path, started) = Query(pid);
        return new ProcessInfo(pid, entry.Parent, entry.Exe, path, started);
    }

    public int? ForegroundProcessId
    {
        get
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(window, out var pid);
            return pid == 0 ? null : (int)pid;
        }
    }

    public string? Describe(ProcessInfo process)
    {
        if (process.Path is not { Length: > 0 } path || !File.Exists(path)) return null;
        var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        return string.IsNullOrEmpty(description) ? null : description;
    }

    public SourceApp? EnvironmentHint => null;

    private static Dictionary<int, (int, string)> Snapshot()
    {
        var table = new Dictionary<int, (int, string)>();
        using var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snap, ref entry)) return table;
        do
        {
            table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
        } while (Process32NextW(snap, ref entry));
        return table;
    }

    private static (string? Path, long? Started) Query(int pid)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid) return (null, null);

        string? path = null;
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) path = new string(buffer, 0, (int)size);

        long? started = GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null;
        return (path, started);
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags,
        [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        out long creation, out long exit, out long kernel, out long user);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
