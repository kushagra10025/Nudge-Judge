using System.Diagnostics;
using NudgeJudge.Core.Config;

namespace NudgeJudge.Core.Ipc;

/// <summary>
/// Exclusive per-user lock deciding which process is the primary instance. Backed by an open file with
/// no sharing (flock on Unix), so the OS releases it if the process dies and nothing is ever left stale.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _file;

    private InstanceLock(FileStream file) => _file = file;

    /// <summary>Returns the lock, or null while another process holds it.</summary>
    public static InstanceLock? TryAcquire(string path)
    {
        AppPaths.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            return new InstanceLock(new FileStream(path, options));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Accepts messages from later launches. A fresh random token is written to an owner-only file before
/// listening; every request must carry it. Requests are size-limited and time-limited, and the handler
/// still validates every URL itself.
/// </summary>
public sealed class InstanceServer : IDisposable
{
    public const int MaxConcurrentConnections = 4;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);

    private readonly IIpcTransport _transport;
    private readonly string _tokenFile;
    private readonly Func<InstanceRequest, Task<InstanceReply>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentConnections);
    private IIpcListener? _listener;
    private string? _token;
    private Task? _acceptLoop;

    /// <param name="handler">Called for each authenticated, well-formed request. Its reply is sent back.</param>
    public InstanceServer(IIpcTransport transport, string tokenFile, Func<InstanceRequest, Task<InstanceReply>> handler)
    {
        _transport = transport;
        _tokenFile = tokenFile;
        _handler = handler;
    }

    /// <summary>Raised (on a pool thread) when a connection is turned away, for diagnostics and tests.</summary>
    public event EventHandler<string>? Rejected;

    public void Start()
    {
        if (_acceptLoop is not null) throw new InvalidOperationException("Already started.");
        _token = InstanceProtocol.NewToken();
        // Token first: a client that reaches the socket must be able to read the matching token.
        AppPaths.WritePrivateFileAtomic(_tokenFile, System.Text.Encoding.UTF8.GetBytes(_token));
        _listener = _transport.Listen();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Stream connection;
            try
            {
                await _slots.WaitAsync(ct);
                connection = await _listener!.AcceptAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
            {
                _slots.Release();
                Trace.TraceWarning($"NudgeJudge: accepting an instance connection failed: {e.Message}");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await ServeAsync(connection, ct); }
                finally { _slots.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(Stream connection, CancellationToken ct)
    {
        await using var _ = connection;
        try
        {
            InstanceRequest? request;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(RequestTimeout);
                request = await InstanceProtocol.ReadRequestAsync(connection, read.Token);
            }

            if (request is null)
            {
                Reject("Malformed message.");
                return; // No reply: nothing tells an unauthenticated peer more than it needs.
            }
            if (!InstanceProtocol.TokensMatch(_token, request.Token))
            {
                Reject("Wrong session token.");
                await InstanceProtocol.WriteReplyAsync(connection, new InstanceReply(false, InstanceProtocol.UnauthorizedError), ct);
                return;
            }
            if (InstanceProtocol.Check(request) is { } error)
            {
                Reject(error);
                await InstanceProtocol.WriteReplyAsync(connection, new InstanceReply(false, error), ct);
                return;
            }

            var reply = await _handler(request).WaitAsync(HandlerTimeout, ct);
            await InstanceProtocol.WriteReplyAsync(connection, reply, ct);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
            // Peer went away, was too slow, or we're shutting down.
        }
    }

    private void Reject(string reason) => Rejected?.Invoke(this, reason);

    /// <summary>Stops accepting, closes the endpoint and removes the token file.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener?.Dispose();
        try
        {
            // Only remove our own token: a newer primary may already have written its own.
            if (_token is not null && File.Exists(_tokenFile) && File.ReadAllText(_tokenFile) == _token)
                File.Delete(_tokenFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
