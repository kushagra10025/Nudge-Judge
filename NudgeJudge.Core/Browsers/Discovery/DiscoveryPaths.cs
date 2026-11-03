namespace NudgeJudge.Core.Browsers.Discovery;

/// <summary>
/// Where discovery looks. Built from the environment in the app; tests point it at a temp directory.
/// </summary>
public sealed record DiscoveryPaths
{
    public required string Home { get; init; }

    /// <summary>Linux <c>$XDG_CONFIG_HOME</c> (Chromium-family profile data lives here).</summary>
    public required string ConfigHome { get; init; }

    /// <summary>Linux <c>$XDG_DATA_HOME</c> followed by <c>$XDG_DATA_DIRS</c>, highest precedence first.</summary>
    public IReadOnlyList<string> DataDirs { get; init; } = [];

    /// <summary>Directories searched for executables named without a path.</summary>
    public IReadOnlyList<string> SearchPath { get; init; } = [];

    /// <summary>Windows <c>%APPDATA%</c> (Firefox profiles).</summary>
    public string? AppData { get; init; }

    /// <summary>Windows <c>%LOCALAPPDATA%</c> (Chromium-family profiles).</summary>
    public string? LocalAppData { get; init; }

    public bool IsWindows { get; init; }

    public static DiscoveryPaths FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var searchPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted)
            .ToList();

        if (OperatingSystem.IsWindows())
        {
            return new DiscoveryPaths
            {
                Home = home,
                ConfigHome = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SearchPath = searchPath,
                IsWindows = true,
            };
        }

        var dataHome = XdgDir("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathRooted)
            .ToList();

        // Snap and Flatpak exports aren't always on XDG_DATA_DIRS (e.g. in a session started before they
        // were installed). Duplicates are harmless: the first directory providing a desktop-file id wins.
        dataDirs.AddRange([
            Path.Combine(dataHome, "flatpak", "exports", "share"),
            "/var/lib/flatpak/exports/share",
            "/var/lib/snapd/desktop",
        ]);

        return new DiscoveryPaths
        {
            Home = home,
            ConfigHome = XdgDir("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"),
            DataDirs = [dataHome, .. dataDirs.Distinct(StringComparer.Ordinal)],
            SearchPath = searchPath,
        };

        static string? XdgDir(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : null;
    }

    /// <summary>True when <paramref name="executable"/> exists, either as a path or on <see cref="SearchPath"/>.</summary>
    public bool CanExecute(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (Path.IsPathRooted(executable)) return File.Exists(executable);
        if (executable.Contains('/') || executable.Contains('\\')) return false;

        string[] candidates = IsWindows && !Path.HasExtension(executable) ? [executable + ".exe", executable] : [executable];
        return SearchPath.Any(dir => candidates.Any(c => File.Exists(Path.Combine(dir, c))));
    }
}
