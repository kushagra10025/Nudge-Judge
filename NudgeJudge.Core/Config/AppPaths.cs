using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NudgeJudge.Core.Config;

/// <summary>Per-user locations. Directories are created with owner-only permissions on Unix.</summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; } = ResolveConfigDirectory();
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    public static string ActivityLogFile => Path.Combine(ConfigDirectory, "activity.log");

    /// <summary>
    /// Where the single-instance lock, socket and session token live. On Linux this is
    /// <c>$XDG_RUNTIME_DIR/nudgejudge</c> (a per-user tmpfs, cleared at logout) when available.
    /// </summary>
    public static string RuntimeDirectory { get; } = ResolveRuntimeDirectory();

    private static string ResolveRuntimeDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime) && Directory.Exists(runtime))
                return Path.Combine(runtime, "nudgejudge");
        }
        return ConfigDirectory;
    }

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
    /// <param name="privateDirectory">
    /// Also make the folder owner-only. False for shared folders that aren't ours to lock down, such as
    /// <c>~/.local/share/applications</c> or <c>~/.config/autostart</c>.
    /// </param>
    public static void WritePrivateFileAtomic(string path, ReadOnlySpan<byte> contents, bool privateDirectory = true)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (privateDirectory) EnsurePrivateDirectory(dir);
        else Directory.CreateDirectory(dir);
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
        // The rename lives in the directory entry; without this a power cut can bring back the old file.
        if (!OperatingSystem.IsWindows()) SyncDirectory(dir);
    }

    /// <summary>Best effort: fsync a directory so a rename inside it survives a crash. Failures are ignored.</summary>
    [UnsupportedOSPlatform("windows")]
    private static void SyncDirectory(string dir)
    {
        try
        {
            var fd = open(dir, 0 /* O_RDONLY */);
            if (fd < 0) return;
            try { fsync(fd); }
            finally { close(fd); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc")]
    private static extern int close(int fd);
}
