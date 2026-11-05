using System.Diagnostics;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Platform;

/// <summary>One process, as much as the OS tells us. <see cref="Started"/> is only comparable within one table.</summary>
public sealed record ProcessInfo(int Pid, int ParentPid, string Name, string? Path = null, long? Started = null);

/// <summary>Reads processes. Faked in tests.</summary>
public interface IProcessTable
{
    ProcessInfo? Get(int pid);

    /// <summary>The process that owns the foreground window, when the platform can tell.</summary>
    int? ForegroundProcessId { get; }

    /// <summary>A friendly name for a program (the file description on Windows), or null.</summary>
    string? Describe(ProcessInfo process);

    /// <summary>A last-resort guess from inherited environment, e.g. <c>CHROME_DESKTOP</c> set by Electron apps.</summary>
    SourceApp? EnvironmentHint { get; }
}

/// <summary>
/// Works out which app opened the link this process was started with. When an app opens a link, the OS
/// starts the default browser (us) as its child, often through helpers (<c>xdg-open</c>, <c>gio</c>, a shell,
/// <c>OpenWith.exe</c>). So: walk up the parent chain, skipping known helpers, and take the first real app.
/// If the chain dead-ends (the helper exited and we were re-parented), fall back to the foreground window's
/// app, then to environment hints. Best effort: returns null when it can't tell.
/// </summary>
public sealed class SourceAppDetector(IProcessTable processes)
{
    private const int MaxDepth = 12;

    private enum Role { App, Helper, Weak, End }

    /// <summary>Helpers that launch links for other apps. Compared without <c>.exe</c>, ignoring case.</summary>
    private static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Linux openers and session plumbing
        "xdg-open", "gio", "gio-launch-desktop", "gvfs-open", "gnome-open", "kde-open", "kde-open5", "kde-open6",
        "kioclient", "kioclient5", "kioclient6", "kioworker", "klauncher", "kdeinit5", "exo-open", "exo-helper-2",
        "sensible-browser", "x-www-browser", "www-browser", "xdg-desktop-portal", "xdg-desktop-portal-gnome",
        "xdg-desktop-portal-kde", "xdg-desktop-portal-gtk", "xdg-desktop-portal-wlr", "flatpak-spawn",
        "flatpak-portal", "bwrap", "dbus-daemon", "dbus-broker", "dbus-broker-launch", "snap", "snapctl",
        // Shells and wrappers
        "sh", "bash", "dash", "zsh", "fish", "ksh", "env", "nohup", "setsid", "timeout", "sudo", "xargs",
        "cmd", "conhost", "powershell", "pwsh", "wscript", "cscript",
        // Windows launch plumbing
        "rundll32", "OpenWith", "svchost", "RuntimeBroker", "sihost", "dllhost", "ApplicationFrameHost",
        "LaunchWinApp", "browser_broker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        // Ourselves (a launch can be relayed by another copy)
        "NudgeJudge", "NudgeJudge.Desktop", "NudgeJudge.Tray", "dotnet",
    };

    /// <summary>The desktop shell: a real source (a .url file, the Run box) but often just a stand-in for the actual app.</summary>
    private static readonly HashSet<string> WeakApps = new(StringComparer.OrdinalIgnoreCase) { "explorer" };

    /// <summary>Where the chain ends: anything above these isn't the app that opened the link.</summary>
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "init", "launchd", "services", "wininit", "winlogon", "smss", "System", "Idle",
    };

    public static SourceAppDetector ForCurrentPlatform() => new(
        OperatingSystem.IsWindows() ? new WindowsProcessTable()
        : OperatingSystem.IsLinux() ? new LinuxProcessTable()
        : new NoProcessTable());

    /// <summary>Never throws: a link must open even when detection fails.</summary>
    public SourceApp? Detect() => Detect(Environment.ProcessId);

    public SourceApp? Detect(int selfPid)
    {
        try
        {
            return DetectCore(selfPid);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Trace.TraceWarning($"NudgeJudge: couldn't tell which app opened the link: {e.Message}");
            return null;
        }
    }

    private SourceApp? DetectCore(int selfPid)
    {
        var child = processes.Get(selfPid);
        ProcessInfo? weak = null;

        for (var depth = 0; child is not null && depth < MaxDepth; depth++)
        {
            if (child.ParentPid <= 0 || child.ParentPid == child.Pid) break;
            var parent = processes.Get(child.ParentPid);
            if (parent is null) break;
            // A parent that started after its child is a recycled pid: the real parent is gone.
            if (child.Started is { } c && parent.Started is { } p && p > c) break;

            switch (RoleOf(parent))
            {
                case Role.App:
                    return ToApp(parent);
                case Role.Weak:
                    weak ??= parent;
                    break;
                case Role.End:
                    child = null;
                    continue;
            }
            child = parent;
        }

        if (processes.ForegroundProcessId is { } fg && fg != selfPid && processes.Get(fg) is { } front && RoleOf(front) == Role.App)
            return ToApp(front);
        if (processes.EnvironmentHint is { } hint) return hint;
        return weak is null ? null : ToApp(weak);
    }

    private static Role RoleOf(ProcessInfo process)
    {
        if (process.Pid <= 1) return Role.End;
        var name = StripExe(ProgramName(process));
        if (Roots.Contains(name)) return Role.End;
        if (Helpers.Contains(name)) return Role.Helper;
        return WeakApps.Contains(name) ? Role.Weak : Role.App;
    }

    private SourceApp? ToApp(ProcessInfo process)
    {
        var name = ProgramName(process);
        string? title = null;
        try
        {
            title = processes.Describe(process);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return SourceApp.Sanitize(new SourceApp(name, process.Path, title));
    }

    /// <summary>The program's file name. Splits on both separators: the table's paths are the OS's own, tests use both.</summary>
    private static string ProgramName(ProcessInfo process) =>
        process.Path is { Length: > 0 } path ? path[(path.LastIndexOfAny(['/', '\\']) + 1)..] : process.Name;

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

internal sealed class NoProcessTable : IProcessTable
{
    public ProcessInfo? Get(int pid) => null;
    public int? ForegroundProcessId => null;
    public string? Describe(ProcessInfo process) => null;
    public SourceApp? EnvironmentHint => null;
}
