namespace NudgeJudge.Core.Config;

/// <summary>Per-user locations. Directories are created with owner-only permissions on Unix.</summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; } = ResolveConfigDirectory();
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    private static string ResolveConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NudgeJudge");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "NudgeJudge");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".config");
        return Path.Combine(root, "nudgejudge");
    }

    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // %APPDATA% is already per-user ACL'd.
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Writes a file atomically (temp file + rename) readable only by the current user.</summary>
    public static void WritePrivateFileAtomic(string path, ReadOnlySpan<byte> contents)
    {
        var dir = Path.GetDirectoryName(path)!;
        EnsurePrivateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var fs = new FileStream(temp, options))
            {
                fs.Write(contents);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
