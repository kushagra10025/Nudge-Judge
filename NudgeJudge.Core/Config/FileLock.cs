namespace NudgeJudge.Core.Config;

/// <summary>
/// A cross-process exclusive lock held on a sidecar file (e.g. <c>config.json.lock</c>), so the data file
/// itself can still be replaced by an atomic rename while the lock is held. The lock file is never deleted:
/// removing it would let two processes each lock a different file under the same name.
/// </summary>
public sealed class FileLock : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    /// <summary>Waits up to <paramref name="timeout"/> for the lock. Throws <see cref="IOException"/> if it stays held.</summary>
    public static FileLock Acquire(string lockPath, TimeSpan? timeout = null)
    {
        AppPaths.EnsurePrivateDirectory(Path.GetDirectoryName(lockPath)!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            // On Unix .NET backs FileShare.None with flock(LOCK_EX), which every NudgeJudge process honours.
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // Other holders keep it for the few milliseconds a read-modify-write takes.
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                return new FileLock(new FileStream(lockPath, options));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(15);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
