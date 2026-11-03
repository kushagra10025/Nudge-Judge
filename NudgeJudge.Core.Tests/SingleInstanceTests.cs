using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using NudgeJudge.Core.Ipc;

namespace NudgeJudge.Core.Tests;

public sealed class InstanceProtocolTests
{
    [Fact]
    public async Task Request_round_trips()
    {
        using var stream = new MemoryStream();
        var sent = new InstanceRequest(InstanceProtocol.Version, "tok", InstanceCommand.Open, ["https://a.example/", "https://b.example/"]);
        await InstanceProtocol.WriteRequestAsync(stream, sent, default);
        stream.Position = 0;

        var received = await InstanceProtocol.ReadRequestAsync(stream, default);

        Assert.NotNull(received);
        Assert.Equal(sent.Token, received.Token);
        Assert.Equal(sent.Command, received.Command);
        Assert.Equal(sent.Urls, received.Urls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(InstanceProtocol.MaxFrameBytes + 1)]
    public async Task Bad_lengths_are_refused_without_reading_further(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        using var stream = new MemoryStream(header);
        Assert.Null(await InstanceProtocol.ReadRequestAsync(stream, default));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"version":1,"token":"t","command":"open"}""")]
    [InlineData("""{"version":1,"token":"t","command":"open","urls":[null]}""")]
    [InlineData("""{"version":1,"token":"t","command":"format-disk","urls":[]}""")]
    public async Task Malformed_payloads_read_as_null(string json)
    {
        using var stream = new MemoryStream();
        await InstanceProtocol.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json), default);
        stream.Position = 0;
        Assert.Null(await InstanceProtocol.ReadRequestAsync(stream, default));
    }

    [Fact]
    public async Task Truncated_frame_reads_as_null()
    {
        using var stream = new MemoryStream();
        await InstanceProtocol.WriteFrameAsync(stream, new byte[100], default);
        stream.SetLength(50);
        stream.Position = 0;
        Assert.Null(await InstanceProtocol.ReadRequestAsync(stream, default));
    }

    [Fact]
    public void Check_limits_url_count_and_version()
    {
        var urls = Enumerable.Repeat("https://a.example/", InstanceProtocol.MaxUrls + 1).ToList();
        Assert.NotNull(InstanceProtocol.Check(new(InstanceProtocol.Version, "t", InstanceCommand.Open, urls)));
        Assert.NotNull(InstanceProtocol.Check(new(99, "t", InstanceCommand.Open, [])));
        Assert.NotNull(InstanceProtocol.Check(new(InstanceProtocol.Version, "t", InstanceCommand.Show, ["https://a.example/"])));
        Assert.Null(InstanceProtocol.Check(new(InstanceProtocol.Version, "t", InstanceCommand.Open, urls[..3])));
    }

    [Fact]
    public void Tokens_are_random_and_compared_exactly()
    {
        var a = InstanceProtocol.NewToken();
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, InstanceProtocol.NewToken());
        Assert.True(InstanceProtocol.TokensMatch(a, a));
        Assert.False(InstanceProtocol.TokensMatch(a, a[..^1]));
        Assert.False(InstanceProtocol.TokensMatch(a, null));
    }

    [Theory]
    [InlineData(new string[0], null, InstanceCommand.Show)]
    [InlineData(new[] { "--background" }, null, null)]
    [InlineData(new[] { "--background", "https://a.example/" }, "https://a.example/", InstanceCommand.Open)]
    public void Launch_arguments_pick_what_to_forward(string[] args, string? url, InstanceCommand? command)
    {
        var launch = LaunchArguments.Parse(args);
        Assert.Equal(url is null ? [] : [url], launch.Urls);
        Assert.Equal(command, launch.ForwardCommand);
    }
}

public sealed class SingleInstanceTests : IDisposable
{
    // Short on purpose: Unix socket paths are limited to ~104 bytes.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nj-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly InstanceEndpoint _endpoint;
    private readonly ConcurrentQueue<InstanceRequest> _handled = new();
    private readonly ConcurrentQueue<string> _rejected = new();

    public SingleInstanceTests()
    {
        _endpoint = OperatingSystem.IsWindows()
            ? new InstanceEndpoint(Path.Combine(_dir, "instance.lock"), Path.Combine(_dir, "instance.token"),
                new NamedPipeTransport("NudgeJudge-test-" + Guid.NewGuid().ToString("N")))
            : InstanceEndpoint.InDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private InstanceServer StartServer()
    {
        var server = new InstanceServer(_endpoint.Transport, _endpoint.TokenFile, request =>
        {
            _handled.Enqueue(request);
            return Task.FromResult(InstanceReply.Success);
        });
        server.Rejected += (_, reason) => _rejected.Enqueue(reason);
        server.Start();
        return server;
    }

    /// <summary>Sends raw bytes the way a hostile local process could, and returns the reply (null if none).</summary>
    private async Task<InstanceReply?> SendRawAsync(Func<Stream, Task> write)
    {
        await using var stream = await _endpoint.Transport.ConnectAsync(TimeSpan.FromSeconds(2), default);
        await write(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await InstanceProtocol.ReadReplyAsync(stream, cts.Token);
    }

    [Fact]
    public void Lock_is_exclusive_until_released()
    {
        using (var first = InstanceLock.TryAcquire(_endpoint.LockFile))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(_endpoint.LockFile));
        }
        using var again = InstanceLock.TryAcquire(_endpoint.LockFile);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Client_with_the_token_is_delivered()
    {
        using var server = StartServer();

        var (result, _) = await InstanceClient.SendAsync(_endpoint, InstanceCommand.Open, ["https://a.example/x"]);

        Assert.Equal(DeliveryResult.Delivered, result);
        var request = Assert.Single(_handled);
        Assert.Equal(["https://a.example/x"], request.Urls);
    }

    [Fact]
    public async Task Wrong_token_is_refused_and_never_handled()
    {
        using var server = StartServer();

        var reply = await SendRawAsync(s => InstanceProtocol.WriteRequestAsync(s,
            new InstanceRequest(InstanceProtocol.Version, InstanceProtocol.NewToken(), InstanceCommand.Open, ["https://evil.example/"]), default));

        Assert.Equal(new InstanceReply(false, InstanceProtocol.UnauthorizedError), reply);
        Assert.Empty(_handled);
        Assert.Contains("Wrong session token.", _rejected);
    }

    [Fact]
    public async Task Garbage_gets_no_reply_and_is_never_handled()
    {
        using var server = StartServer();

        var reply = await SendRawAsync(s => InstanceProtocol.WriteFrameAsync(s, "garbage"u8.ToArray(), default));

        Assert.Null(reply);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task Oversized_frame_is_dropped_before_being_read()
    {
        using var server = StartServer();

        var reply = await SendRawAsync(async s =>
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, int.MaxValue);
            await s.WriteAsync(header);
        });

        Assert.Null(reply);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task Too_many_urls_are_refused_even_with_the_token()
    {
        using var server = StartServer();
        var urls = Enumerable.Repeat("https://a.example/", InstanceProtocol.MaxUrls + 1).ToList();

        var (result, error) = await InstanceClient.SendAsync(_endpoint, InstanceCommand.Open, urls);

        Assert.Equal(DeliveryResult.Refused, result);
        Assert.NotNull(error);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task No_server_is_unreachable()
    {
        var (result, _) = await InstanceClient.SendAsync(_endpoint, InstanceCommand.Show, []);
        Assert.Equal(DeliveryResult.Unreachable, result);
    }

    [Fact]
    public void Token_file_is_private_and_removed_on_stop()
    {
        var server = StartServer();
        Assert.True(File.Exists(_endpoint.TokenFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_endpoint.TokenFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
            var socket = Assert.IsType<UnixSocketTransport>(_endpoint.Transport).SocketPath;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
        }

        server.Dispose();

        Assert.False(File.Exists(_endpoint.TokenFile));
    }

    [Fact]
    public async Task Restarted_server_replaces_a_stale_socket_and_token()
    {
        // What a crashed primary leaves behind: a token file and (on Unix) a socket file nobody listens on.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_endpoint.TokenFile, "stale");
        if (!OperatingSystem.IsWindows() && _endpoint.Transport is UnixSocketTransport unix) File.WriteAllText(unix.SocketPath, "");

        using var server = StartServer();

        Assert.NotEqual("stale", File.ReadAllText(_endpoint.TokenFile));
        var (result, _) = await InstanceClient.SendAsync(_endpoint, InstanceCommand.Show, []);
        Assert.Equal(DeliveryResult.Delivered, result);
    }

    [Fact]
    public void First_claim_is_primary_and_later_launches_forward_to_it()
    {
        using var primary = SingleInstance.Claim(_endpoint, LaunchArguments.Parse([]));
        Assert.True(primary.IsPrimary);
        Assert.True(primary.StartServer(request =>
        {
            _handled.Enqueue(request);
            return Task.FromResult(InstanceReply.Success);
        }));

        using var second = SingleInstance.Claim(_endpoint, LaunchArguments.Parse(["https://a.example/", "https://b.example/"]));
        using var third = SingleInstance.Claim(_endpoint, LaunchArguments.Parse([]));
        using var background = SingleInstance.Claim(_endpoint, LaunchArguments.Parse(["--background"]));

        Assert.True(second.Forwarded);
        Assert.True(third.Forwarded);
        Assert.True(background.Forwarded);
        Assert.False(second.IsPrimary);
        Assert.Collection(_handled,
            r => Assert.Equal((InstanceCommand.Open, 2), (r.Command, r.Urls.Count)),
            r => Assert.Equal(InstanceCommand.Show, r.Command));
    }

    [Fact]
    public async Task Launch_waits_for_a_primary_that_is_still_starting()
    {
        var primary = SingleInstance.Claim(_endpoint, LaunchArguments.Parse([]));
        Assert.True(primary.IsPrimary);
        // The primary takes a moment (as Avalonia's startup does) before it listens.
        var starting = Task.Run(async () =>
        {
            await Task.Delay(400);
            primary.StartServer(request =>
            {
                _handled.Enqueue(request);
                return Task.FromResult(InstanceReply.Success);
            });
        });

        using var second = SingleInstance.Claim(_endpoint, LaunchArguments.Parse(["https://a.example/"]));

        await starting;
        primary.Dispose();
        Assert.True(second.Forwarded);
        Assert.Single(_handled);
    }

    [Fact]
    public void Launch_takes_over_once_the_primary_exits()
    {
        var primary = SingleInstance.Claim(_endpoint, LaunchArguments.Parse([]));
        Assert.True(primary.IsPrimary);
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            primary.Dispose(); // Never started a server: e.g. it was quitting.
        });

        using var second = SingleInstance.Claim(_endpoint, LaunchArguments.Parse(["https://a.example/"]));

        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void Primary_that_cannot_listen_releases_the_lock()
    {
        var endpoint = _endpoint with { Transport = new BrokenTransport() };
        using var primary = SingleInstance.Claim(endpoint, LaunchArguments.Parse([]));
        Assert.True(primary.IsPrimary);

        Assert.False(primary.StartServer(_ => Task.FromResult(InstanceReply.Success)));

        Assert.False(primary.IsPrimary);
        Assert.False(File.Exists(endpoint.TokenFile));
        using var next = SingleInstance.Claim(endpoint, LaunchArguments.Parse(["https://a.example/"]));
        Assert.True(next.IsPrimary); // Straight away, not after the retry budget.
    }

    private sealed class BrokenTransport : IIpcTransport
    {
        public IIpcListener Listen() => throw new IOException("Socket path is too long.");
        public Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken ct) => throw new IOException("Nobody there.");
    }

    [Fact]
    public void Unresponsive_primary_falls_back_to_standalone()
    {
        using var primary = SingleInstance.Claim(_endpoint, LaunchArguments.Parse([]));

        using var second = SingleInstance.Claim(_endpoint, LaunchArguments.Parse(["https://a.example/"]), TimeSpan.FromMilliseconds(300));

        Assert.False(second.IsPrimary);
        Assert.False(second.Forwarded);
        Assert.NotNull(second.Note);
    }
}
