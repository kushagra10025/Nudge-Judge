using System.Reflection;

namespace NudgeJudge.Core.Platform;

/// <summary>The argv that starts this app again, which is what the OS link handler must run.</summary>
public static class AppLaunchCommand
{
    /// <summary>Null when it can't be worked out (e.g. no process path); registration is then unavailable.</summary>
    public static IReadOnlyList<string>? Current() => Resolve(
        Environment.ProcessPath,
        Assembly.GetEntryAssembly()?.Location,
        Environment.GetEnvironmentVariable("APPIMAGE"));

    internal static IReadOnlyList<string>? Resolve(string? processPath, string? entryAssemblyPath, string? appImage)
    {
        // Inside an AppImage the process runs from a temporary mount; the image file itself is what lasts.
        if (!string.IsNullOrEmpty(appImage) && Path.IsPathRooted(appImage)) return [appImage];

        if (string.IsNullOrEmpty(processPath) || !Path.IsPathRooted(processPath)) return null;

        // Started as "dotnet NudgeJudge.Desktop.dll": the host needs the app's dll as its first argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrEmpty(entryAssemblyPath) && Path.IsPathRooted(entryAssemblyPath)
                ? [processPath, entryAssemblyPath]
                : null;
        }

        return [processPath];
    }
}
