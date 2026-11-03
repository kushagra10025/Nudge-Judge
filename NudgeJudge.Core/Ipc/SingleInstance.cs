using System.Net.Sockets;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Ipc;

/// <summary>The command line, split into links and flags, plus the app that launched us (when there are links).</summary>
public sealed record LaunchArguments(IReadOnlyList<string> Urls, bool Background)
{
    /// <summary>The app that opened these links, detected at startup. Forwarded with them.</summary>
    public SourceApp? Source { get; init; }

    public static LaunchArguments Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        return new(list.Where(a => !a.StartsWith('-')).ToList(), list.Contains("--background"));
    }

    /// <summary>What to ask an already-running instance to do, or null for nothing (a <c>--background</c> launch).</summary>
    public InstanceCommand? ForwardCommand =>
        Urls.Count > 0 ? InstanceCommand.Open : Background ? null : InstanceCommand.Show;
}

/// <summary>Where one user's single-instance pieces live.</summary>
public sealed record InstanceEndpoint(string LockFile, string TokenFile, IIpcTransport Transport)
{
    public static InstanceEndpoint ForCurrentUser() => InDirectory(AppPaths.RuntimeDirectory);

    public static InstanceEndpoint InDirectory(string directory) => new(
        Path.Combine(directory, "instance.lock"),
        Path.Combine(directory, "instance.token"),
        IpcTransport.ForCurrentPlatform(directory));
}

public enum DeliveryResult
{
    Delivered,
    /// <summary>No server answered (not started yet, or just exited). Safe to try again.</summary>
    Unreachable,
    /// <summary>The server answered but refused, or stopped responding mid-request.</summary>
    Refused,
}

public static class InstanceClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    public static async Task<(DeliveryResult Result, string? Error)> SendAsync(
        InstanceEndpoint endpoint, InstanceCommand command, IReadOnlyList<string> urls, SourceApp? source = null, CancellationToken ct = default)
    {
        string token;
        try
        {
            token = (await File.ReadAllTextAsync(endpoint.TokenFile, ct)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (DeliveryResult.Unreachable, "No session token yet.");
        }

        Stream stream;
        try
        {
            stream = await endpoint.Transport.ConnectAsync(ConnectTimeout, ct);
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or UnauthorizedAccessException)
        {
            return (DeliveryResult.Unreachable, e.Message);
        }

        await using (stream)
        {
            try
            {
                await InstanceProtocol.WriteRequestAsync(stream, new InstanceRequest(InstanceProtocol.Version, token, command, urls, source), ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(ReplyTimeout);
                var reply = await InstanceProtocol.ReadReplyAsync(stream, wait.Token);
                return reply switch
                {
                    { Ok: true } => (DeliveryResult.Delivered, null),
                    // The server closed without answering: it was shutting down. Whoever comes next will handle it.
                    null => (DeliveryResult.Unreachable, "The running instance closed the connection."),
                    // We read a token from a primary that has since been replaced. Nothing was done; read it again.
                    { Error: InstanceProtocol.UnauthorizedError } => (DeliveryResult.Unreachable, reply.Error),
                    _ => (DeliveryResult.Refused, reply.Error),
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (DeliveryResult.Refused, "The running instance didn't answer in time.");
            }
            catch (IOException e)
            {
                return (DeliveryResult.Unreachable, e.Message);
            }
        }
    }
}

/// <summary>Outcome of <see cref="SingleInstance.Claim"/>.</summary>
public sealed class InstanceClaim : IDisposable
{
    private InstanceLock? _lock;
    private InstanceServer? _server;

    internal InstanceClaim(InstanceEndpoint endpoint, InstanceLock? instanceLock, bool forwarded, string? note)
    {
        Endpoint = endpoint;
        _lock = instanceLock;
        Forwarded = forwarded;
        Note = note;
    }

    public InstanceEndpoint Endpoint { get; }

    /// <summary>This process holds the lock and should run the server.</summary>
    public bool IsPrimary => _lock is not null;

    /// <summary>Another instance took the request; this process should exit.</summary>
    public bool Forwarded { get; }

    /// <summary>Why the claim ended up standalone or refused, for diagnostics.</summary>
    public string? Note { get; }

    /// <summary>Primary only: starts listening for later launches. Returns false if the endpoint couldn't be opened.</summary>
    public bool StartServer(Func<InstanceRequest, Task<InstanceReply>> handler)
    {
        if (!IsPrimary || _server is not null) return false;
        var server = new InstanceServer(Endpoint.Transport, Endpoint.TokenFile, handler);
        try
        {
            server.Start();
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException)
        {
            server.Dispose();
            // Let go of the lock too, or every later launch would wait out its budget for a server that never comes.
            _lock?.Dispose();
            _lock = null;
            System.Diagnostics.Trace.TraceWarning($"NudgeJudge: single-instance server didn't start: {e.Message}");
            return false;
        }
        _server = server;
        return true;
    }

    /// <summary>Stops taking new messages; the lock stays held until <see cref="Dispose"/>.</summary>
    public void StopServer()
    {
        _server?.Dispose();
        _server = null;
    }

    public void Dispose()
    {
        StopServer();
        _lock?.Dispose();
    }
}

public static class SingleInstance
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Becomes the primary instance, or hands <paramref name="launch"/> to the one already running.
    /// Retries while a primary is starting up or shutting down. If no primary answers within the
    /// budget, returns a standalone claim (neither primary nor forwarded) so the links still get handled here.
    /// </summary>
    public static InstanceClaim Claim(InstanceEndpoint endpoint, LaunchArguments launch, TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? DefaultBudget);
        string? lastError = null;
        while (true)
        {
            InstanceLock? held;
            try
            {
                held = InstanceLock.TryAcquire(endpoint.LockFile);
            }
            catch (UnauthorizedAccessException e)
            {
                return new InstanceClaim(endpoint, null, forwarded: false, $"Can't use the instance lock: {e.Message}");
            }
            if (held is not null) return new InstanceClaim(endpoint, held, forwarded: false, null);

            if (launch.ForwardCommand is not { } command)
                return new InstanceClaim(endpoint, null, forwarded: true, null); // Already running; nothing to say.

            var (result, error) = InstanceClient.SendAsync(endpoint, command, launch.Urls, launch.Source).GetAwaiter().GetResult();
            switch (result)
            {
                case DeliveryResult.Delivered:
                    return new InstanceClaim(endpoint, null, forwarded: true, null);
                case DeliveryResult.Refused:
                    // The running instance saw the request; handling it here too could open links twice.
                    return new InstanceClaim(endpoint, null, forwarded: true, error);
            }

            lastError = error;
            if (DateTime.UtcNow >= deadline)
                return new InstanceClaim(endpoint, null, forwarded: false, $"The running instance didn't answer: {lastError}");
            Thread.Sleep(RetryDelay);
        }
    }
}
