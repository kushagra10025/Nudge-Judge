using System.Reflection;
using System.Runtime.InteropServices;

namespace NudgeJudge.Core.Platform;

/// <summary>The argv that starts this app again, which is what the OS link handler must run.</summary>
public static class AppLaunchCommand
{
    /// <summary>Null when it can't be worked out (e.g. no process path); registration is then unavailable.</summary>
    public static IReadOnlyList<string>? Current()
    {
        var root = RuntimeRoot();
        var muxer = root is null ? null : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return Resolve(
            Environment.ProcessPath,
            Assembly.GetEntryAssembly()?.Location,
            Environment.GetEnvironmentVariable("APPIMAGE"),
            muxer is not null && File.Exists(muxer) ? root : null,
            HostSearchedLocations());
    }

    /// <param name="runtimeRoot">The .NET install this process runs on (the folder holding <c>shared/</c> and the <c>dotnet</c> host).</param>
    /// <param name="hostSearched">
    /// Where the app's native launcher looks for .NET when no environment variables help: the registered install
    /// location and the OS default. A link handler is started by the desktop, not a terminal, so it never sees
    /// <c>DOTNET_ROOT</c> from a shell profile.
    /// </param>
    internal static IReadOnlyList<string>? Resolve(string? processPath, string? entryAssemblyPath, string? appImage,
        string? runtimeRoot = null, IReadOnlyList<string>? hostSearched = null)
    {
        // Inside an AppImage the process runs from a temporary mount; the image file itself is what lasts.
        if (!string.IsNullOrEmpty(appImage) && Path.IsPathRooted(appImage)) return [appImage];

        if (string.IsNullOrEmpty(processPath) || !Path.IsPathRooted(processPath)) return null;

        var hasDll = !string.IsNullOrEmpty(entryAssemblyPath) && Path.IsPathRooted(entryAssemblyPath);

        // Started as "dotnet NudgeJudge.Desktop.dll": the host needs the app's dll as its first argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return hasDll ? [processPath, entryAssemblyPath!] : null;

        // Started through the app's own launcher. It only finds .NET via DOTNET_ROOT, the app folder (self-contained),
        // or the registered/default install. When this runtime is none of the last two (a per-user install such as
        // ~/.dotnet that only a shell profile points at), links opened from desktop apps would fail with "You must
        // install .NET". Run the dll on this runtime's own host instead: that needs no environment at all.
        if (hasDll && runtimeRoot is not null && hostSearched is not null &&
            !IsUnder(runtimeRoot, Path.GetDirectoryName(processPath)!) &&
            !hostSearched.Any(l => SamePath(l, runtimeRoot)))
        {
            return [Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), entryAssemblyPath!];
        }

        return [processPath];
    }

    /// <summary><c>…/shared/Microsoft.NETCore.App/10.0.x/</c> → the install root, or null for a self-contained app.</summary>
    private static string? RuntimeRoot()
    {
        var dir = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/', '\\'));
        var shared = dir.Parent?.Parent;
        return shared is { Name: "shared" } ? shared.Parent?.FullName : null;
    }

    private static IReadOnlyList<string> HostSearchedLocations()
    {
        var found = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
                using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32)
                    .OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}");
                if (key?.GetValue("InstallLocation") is string registered) found.Add(registered);
                found.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            }
            else
            {
                var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
                foreach (var file in new[] { $"/etc/dotnet/install_location_{arch}", "/etc/dotnet/install_location" })
                {
                    if (File.Exists(file) && File.ReadLines(file).FirstOrDefault()?.Trim() is { Length: > 0 } location)
                        found.Add(location);
                }
                found.Add(OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : "/usr/share/dotnet");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return found;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsUnder(string path, string dir)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.Equals(d, cmp) || p.StartsWith(d + Path.DirectorySeparatorChar, cmp);
    }
}
