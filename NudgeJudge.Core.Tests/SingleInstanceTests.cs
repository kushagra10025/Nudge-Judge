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
    public void Check_allows_links_and_pages_only_where_they_mean_something()
    {
        InstanceRequest R(InstanceCommand c, string[] urls, string? page = null) => new(InstanceProtocol.Version, "t", c, urls, null, page);

        Assert.Null(InstanceProtocol.Check(R(InstanceCommand.Choose, ["https://a.example/"])));
        Assert.NotNull(InstanceProtocol.Check(R(InstanceCommand.Choose, [])));
        Assert.Null(InstanceProtocol.Check(R(InstanceCommand.Quit, [])));
        Assert.NotNull(InstanceProtocol.Check(R(InstanceCommand.Quit, ["https://a.example/"])));
        Assert.NotNull(InstanceProtocol.Check(R(InstanceCommand.ToggleRouting, ["https://a.example/"])));
        Assert.Null(InstanceProtocol.Check(R(InstanceCommand.Show, [], "rules")));
        Assert.NotNull(InstanceProtocol.Check(R(InstanceCommand.Open, ["https://a.example/"], "rules")));
        Assert.NotNull(InstanceProtocol.Check(R(InstanceCommand.Show, [], new string('x', InstanceProtocol.MaxPageLength + 1))));
    }

    [Fact]
    public async Task Page_round_trips()
    {
        using var stream = new MemoryStream();
        await InstanceProtocol.WriteRequestAsync(stream, new(InstanceProtocol.Version, "t", InstanceCommand.Show, [], null, "activity"), default);
        stream.Position = 0;
        Assert.Equal("activity", (await InstanceProtocol.ReadRequestAsync(stream, default))?.Page);
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

    [Fact]
    public void The_ui_flag_marks_a_window_process_and_is_not_a_link()
    {
        var launch = LaunchArguments.Parse(["--ui"]);
        Assert.True(launch.Ui);
        Assert.Empty(launch.Urls);
        Assert.False(LaunchArguments.Parse([]).Ui);
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
    public void An_exclusive_claim_waits_for_the_holder_to_let_go()
    {
        var first = SingleInstance.ClaimExclusive(_endpoint);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.ClaimExclusive(_endpoint, TimeSpan.FromMilliseconds(150)));

        // The tray handover: the launch lets go while the tray host is already waiting for it.
        var waiting = Task.Run(() => SingleInstance.ClaimExclusive(_endpoint, TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);
        first.Dispose();
        using var second = waiting.GetAwaiter().GetResult();
        Assert.NotNull(second);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public async Task The_ui_channel_is_separate_from_the_main_one()
    {
        if (OperatingSystem.IsWindows()) return; // Pipe names are per user, not per directory: covered by the Unix case.
        var ui = InstanceEndpoint.InDirectory(_dir, "ui");
        Assert.NotEqual(_endpoint.LockFile, ui.LockFile);

        using var main = SingleInstance.ClaimExclusive(_endpoint)!;
        using var window = SingleInstance.ClaimExclusive(ui)!;
        Assert.True(main.StartServer(_ => Task.FromResult(new InstanceReply(false, "main"))));
        Assert.True(window.StartServer(request =>
        {
            _handled.Enqueue(request);
            return Task.FromResult(InstanceReply.Success);
        }));

        var (result, _) = await InstanceClient.SendAsync(ui, InstanceCommand.Choose, ["https://a.example/"], page: null);
        Assert.Equal(DeliveryResult.Delivered, result);
        Assert.Equal(InstanceCommand.Choose, Assert.Single(_handled).Command);
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
