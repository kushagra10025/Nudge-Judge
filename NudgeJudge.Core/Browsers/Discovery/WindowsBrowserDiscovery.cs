using System.Runtime.Versioning;
using Microsoft.Win32;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Browsers.Discovery;

/// <summary>
/// Finds browsers registered under <c>SOFTWARE\Clients\StartMenuInternet</c> in HKCU and HKLM
/// (both registry views), which is where Windows' own Default Apps list comes from.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBrowserDiscovery(DiscoveryPaths? paths = null) : IBrowserDiscovery
{
    private const string ClientsKey = @"SOFTWARE\Clients\StartMenuInternet";

    public bool IsSupported => true;

    public Task<IReadOnlyList<BrowserProfile>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => WindowsRegistryBrowsers.FromEntries(ReadRegistry(cancellationToken),
            paths ?? DiscoveryPaths.FromEnvironment(), File.Exists, Environment.ProcessPath), cancellationToken);

    private static List<(string Name, string? Command)> ReadRegistry(CancellationToken cancellationToken)
    {
        var entries = new List<(string, string?)>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var clients = baseKey.OpenSubKey(ClientsKey);
                if (clients is null) continue;

                foreach (var keyName in clients.GetSubKeyNames())
                {
                    using var key = clients.OpenSubKey(keyName);
                    if (key is null) continue;
                    using var command = key.OpenSubKey(@"shell\open\command");
                    entries.Add((key.GetValue(null) as string is { Length: > 0 } n ? n : keyName, command?.GetValue(null) as string));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A locked-down hive just contributes nothing.
            }
        }
        return entries;
    }
}

/// <summary>Registry-independent half of Windows discovery, so it can be tested anywhere.</summary>
internal static class WindowsRegistryBrowsers
{
    public static IReadOnlyList<BrowserProfile> FromEntries(IEnumerable<(string Name, string? Command)> entries,
        DiscoveryPaths paths, Func<string, bool> fileExists, string? selfPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var browsers = new List<List<BrowserProfile>>();

        foreach (var (name, command) in entries)
        {
            if (ExecutableOf(command) is not { } exe) continue;
            if (!fileExists(exe) || !seen.Add(exe)) continue;
            if (selfPath is not null && string.Equals(exe, selfPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("NudgeJudge", StringComparison.OrdinalIgnoreCase)) continue;
            // Internet Explorer is still registered on some systems but only forwards to Edge.
            if (BrowserFamily.ExecutableName(exe) == "iexplore") continue;

            // The registered command line is meant for the shell (-osint, "--", "%1"); the browser
            // takes a plain URL argument, which is what the launcher appends.
            var found = BrowserVariants.Expand(name.Trim(), [exe], null, null, paths);
            if (found.Count > 0) browsers.Add(found);
        }

        return BrowserVariants.Distinct(
            browsers.OrderBy(b => b[0].Name, StringComparer.CurrentCultureIgnoreCase).SelectMany(b => b),
            ignorePathCase: true);
    }

    /// <summary>The executable at the start of a registry command line, environment variables expanded.</summary>
    internal static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());

        string exe;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return null;
            exe = command[1..end];
        }
        else
        {
            // Unquoted paths may contain spaces; take up to and including ".exe" when present.
            var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = exeEnd >= 0 ? command[..(exeEnd + 4)] : command.Split(' ', 2)[0];
        }
        exe = exe.Trim();
        return exe.Length > 0 && Path.IsPathRooted(exe) ? exe : null;
    }
}
