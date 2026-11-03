using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using NudgeJudge.Core.Browsers.Discovery;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Starts NudgeJudge (in the tray, with <c>--background</c>) when the user signs in. The OS entry is the
/// source of truth, so removing it in the system's own startup settings shows up here too.
/// </summary>
public interface ILaunchAtStartup
{
    /// <summary>False where it isn't built (macOS) or the app can't tell how it was started.</summary>
    bool IsSupported { get; }

    /// <summary>True when the OS will start NudgeJudge at sign-in.</summary>
    bool IsEnabled();

    /// <summary>Adds or removes the startup entry. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on failure.</summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Rewrites an existing entry that points at an old location (the app moved or was updated), so it keeps
    /// working. Does nothing when launch at startup is off.
    /// </summary>
    void Refresh();
}

public static class LaunchAtStartup
{
    public const string BackgroundFlag = "--background";

    public static ILaunchAtStartup ForCurrentPlatform()
    {
        var command = AppLaunchCommand.Current();
        if (OperatingSystem.IsWindows()) return new WindowsLaunchAtStartup(command);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return LinuxLaunchAtStartup.FromEnvironment(command);
        return new StubLaunchAtStartup();
    }
}

/// <summary>Freedesktop autostart: <c>$XDG_CONFIG_HOME/autostart/nudgejudge.desktop</c>.</summary>
public sealed class LinuxLaunchAtStartup(string configHome, IReadOnlyList<string>? launchCommand) : ILaunchAtStartup
{
    public static LinuxLaunchAtStartup FromEnvironment(IReadOnlyList<string>? launchCommand)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } v && Path.IsPathRooted(v)
            ? v
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new LinuxLaunchAtStartup(configHome, launchCommand);
    }

    public string EntryPath => Path.Combine(configHome, "autostart", LinuxDefaultBrowserRegistrar.DesktopId);

    private string? Exec => launchCommand is { Count: > 0 } c ? DesktopExec.Build([.. c, LaunchAtStartup.BackgroundFlag], withUrl: false) : null;

    public bool IsSupported => Exec is not null;

    public bool IsEnabled()
    {
        if (IniFile.TryLoad(EntryPath) is not { } entry) return false;
        // Desktops "disable" an autostart entry without deleting it via these keys.
        return !IsTrue(entry.Get("Desktop Entry", "Hidden")) &&
               !string.Equals(entry.Get("Desktop Entry", "X-GNOME-Autostart-enabled"), "false", StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
            return;
        }
        if (Exec is not { } exec) throw new IOException("NudgeJudge can't tell how it was started, so it can't add itself to startup.");
        WriteAtomic(EntryPath, Entry(exec));
    }

    public void Refresh()
    {
        if (Exec is not { } exec || !IsEnabled()) return;
        if (IniFile.TryLoad(EntryPath)?.Get("Desktop Entry", "Exec") != exec) WriteAtomic(EntryPath, Entry(exec));
    }

    internal static string Entry(string exec) => $"""
        [Desktop Entry]
        Type=Application
        Name=NudgeJudge
        Comment=Routes links to the browser your rules choose
        Exec={exec}
        Terminal=false
        StartupNotify=false
        X-GNOME-Autostart-enabled=true

        """;

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(text));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>Per-user <c>HKCU\…\CurrentVersion\Run</c> value; no admin needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLaunchAtStartup(IReadOnlyList<string>? launchCommand) : ILaunchAtStartup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "NudgeJudge";

    private string? Command => launchCommand is { Count: > 0 } c ? WindowsRunCommand.Build(c) : null;

    public bool IsSupported => Command is not null;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }
            if (Command is not { } command) throw new IOException("NudgeJudge can't tell how it was started, so it can't add itself to startup.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch (System.Security.SecurityException e)
        {
            throw new UnauthorizedAccessException(e.Message, e);
        }
    }

    public void Refresh()
    {
        if (Command is not { } command) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string { Length: > 0 } current && current != command)
            key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}

/// <summary>The Run value, kept free of registry calls so it can be tested on any OS.</summary>
internal static class WindowsRunCommand
{
    /// <summary>The Run value for <paramref name="command"/>, or null when it can't be written as a command line.</summary>
    public static string? Build(IReadOnlyList<string> command)
    {
        // Windows paths can't contain quotes, so plain quoting is unambiguous.
        if (command.Any(a => a.Length == 0 || a.Contains('"') || a.Any(char.IsControl))) return null;
        return string.Join(' ', command.Select(a => $"\"{a}\"")) + " " + LaunchAtStartup.BackgroundFlag;
    }
}

/// <summary>Used where launch at startup isn't built yet (macOS; a LaunchAgent or SMAppService later).</summary>
public sealed class StubLaunchAtStartup : ILaunchAtStartup
{
    public bool IsSupported => false;
    public bool IsEnabled() => false;
    public void SetEnabled(bool enabled) => throw new IOException("Launching at startup isn't available on this system yet.");
    public void Refresh() { }
}
