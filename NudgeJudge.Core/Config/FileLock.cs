namespace NudgeJudge.Core.Config;

/// <summary>
/// A cross-process exclusive lock held on a sidecar file (e.g. <c>config.json.lock</c>), so the data file
/// itself can still be replaced by an atomic rename while the lock is held. The lock and turnstile files are never
/// deleted: removing one would let two processes each lock a different file under the same name.
/// </summary>
public sealed class FileLock : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    /// <summary>Waits up to <paramref name="timeout"/> for the lock. Throws <see cref="IOException"/> if it stays held.</summary>
    /// <remarks>
    /// Waiters queue on a turnstile (<c>&lt;lock&gt;.turn</c>), held only while waiting for the lock itself. Without it,
    /// a holder that releases and takes the lock straight back (a burst of writes) wins every race against a waiter
    /// that polls, and the waiter times out however short each hold is. With it, the returning holder finds the
    /// turnstile taken and waits its turn, so the lock alternates. Older versions skip the turnstile and still
    /// exclude correctly through the lock file.
    /// </remarks>
    public static FileLock Acquire(string lockPath, TimeSpan? timeout = null)
    {
        AppPaths.EnsurePrivateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        using (Take(lockPath + ".turn", deadline))
            return new FileLock(Take(lockPath, deadline));
    }

    private static FileStream Take(string path, DateTime deadline)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            // On Unix .NET backs FileShare.None with flock(LOCK_EX), which every NudgeJudge process honours.
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // Other holders keep it for the few milliseconds a read-modify-write takes (more with a slow disk's fsync).
        while (true)
        {
            try
            {
                return new FileStream(path, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(15);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
