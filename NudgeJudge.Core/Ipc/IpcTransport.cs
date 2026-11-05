using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using NudgeJudge.Core.Config;

namespace NudgeJudge.Core.Ipc;

/// <summary>A local, current-user-only byte channel between launches.</summary>
public interface IIpcTransport
{
    /// <summary>Starts listening. Throws if the endpoint can't be claimed.</summary>
    IIpcListener Listen();

    /// <summary>Connects to a listener. Throws <see cref="IOException"/>, <see cref="SocketException"/> or <see cref="TimeoutException"/> when none is there.</summary>
    Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken ct);
}

public interface IIpcListener : IDisposable
{
    /// <summary>Waits for the next connection from a peer that passed the transport's own checks.</summary>
    Task<Stream> AcceptAsync(CancellationToken ct);
}

public static class IpcTransport
{
    /// <param name="role">Null for the main single-instance channel; otherwise a separate channel by that name.</param>
    public static IIpcTransport ForCurrentPlatform(string runtimeDirectory, string? role = null) =>
        OperatingSystem.IsWindows()
            ? new NamedPipeTransport(NamedPipeTransport.PipeNameForCurrentUser() + (role is null ? "" : "-" + role))
            : new UnixSocketTransport(Path.Combine(runtimeDirectory, role is null ? "nudgejudge.sock" : $"nudgejudge-{role}.sock"));
}

/// <summary>
/// Unix domain socket in an owner-only (0700) directory. The socket itself is 0600 and, on Linux,
/// each peer's uid is checked with SO_PEERCRED.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixSocketTransport(string socketPath) : IIpcTransport
{
    /// <summary>sun_path is 108 bytes on Linux and 104 on macOS, including the terminator.</summary>
    private const int MaxSocketPathBytes = 103;

    public string SocketPath { get; } = socketPath;

    public IIpcListener Listen()
    {
        if (Encoding.UTF8.GetByteCount(SocketPath) > MaxSocketPathBytes)
            throw new IOException($"Socket path is too long: {SocketPath}");

        AppPaths.EnsurePrivateDirectory(Path.GetDirectoryName(SocketPath)!);
        // A socket file left by a process that crashed. Only the lock holder listens, so it's safe to remove.
        if (File.Exists(SocketPath)) File.Delete(SocketPath);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(16);
            return new Listener(socket, SocketPath);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException("Timed out connecting to the running instance.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Listener(Socket socket, string path) : IIpcListener
    {
        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            while (true)
            {
                var peer = await socket.AcceptAsync(ct);
                if (PeerIsCurrentUser(peer)) return new NetworkStream(peer, ownsSocket: true);
                peer.Dispose();
            }
        }

        public void Dispose()
        {
            socket.Dispose();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Linux only: compares the peer's uid (SO_PEERCRED) with ours. Elsewhere the 0700 directory
    /// and the session token are what keep other users out.
    /// </summary>
    internal static bool PeerIsCurrentUser(Socket peer)
    {
        if (!OperatingSystem.IsLinux()) return true;
        if (CurrentUid.Value is not { } self) return true;

        const int SolSocket = 1, SoPeerCred = 17;
        Span<byte> cred = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        try
        {
            if (peer.GetRawSocketOption(SolSocket, SoPeerCred, cred) < 12) return false;
        }
        catch (SocketException)
        {
            return false;
        }
        return MemoryMarshal.Read<uint>(cred[4..]) == self;
    }

    /// <summary>Our effective uid, read from /proc so no native interop is needed.</summary>
    private static readonly Lazy<uint?> CurrentUid = new(() =>
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var fields = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // Real, effective, saved, filesystem.
                return fields.Length > 1 && uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var euid)
                    ? euid : null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    });
}

/// <summary>
/// Named pipe restricted to the current user (<see cref="PipeOptions.CurrentUserOnly"/>: the pipe's ACL on the
/// server side, and a check of the server's owner on the client side). The first instance is created with
/// <see cref="PipeOptions.FirstPipeInstance"/>, so listening fails if someone else already holds the name.
/// </summary>
public sealed class NamedPipeTransport(string pipeName) : IIpcTransport
{
    public string PipeName { get; } = pipeName;

    [SupportedOSPlatform("windows")]
    public static string PipeNameForCurrentUser()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        return "NudgeJudge-" + hash;
    }

    public IIpcListener Listen() => new Listener(PipeName);

    public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, ct);
            if (OperatingSystem.IsWindows()) LetServerTakeFocus(pipe);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static NamedPipeServerStream Create(string name, bool first) =>
        new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));

    private sealed class Listener : IIpcListener
    {
        private readonly string _name;
        private NamedPipeServerStream? _waiting;

        public Listener(string name)
        {
            _name = name;
            _waiting = Create(name, first: true);
        }

        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            var pipe = _waiting ?? throw new ObjectDisposedException(nameof(Listener));
            await pipe.WaitForConnectionAsync(ct);
            // Open the next instance before handing this one off, so the name is never free to be taken.
            _waiting = Create(_name, first: false);
            return pipe;
        }

        public void Dispose()
        {
            _waiting?.Dispose();
            _waiting = null;
        }
    }

    /// <summary>
    /// Windows only lets the foreground process hand focus on. This launch was just started by the user
    /// (or the app they clicked a link in), so it lets the server bring its window forward.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void LetServerTakeFocus(NamedPipeClientStream pipe)
    {
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) AllowSetForegroundWindow(pid);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
