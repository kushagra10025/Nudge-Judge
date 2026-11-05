using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Ipc;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.NativeMessaging;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core.Tests;

public sealed class NativeHostProtocolTests
{
    private sealed class FakeBackend : INativeHostBackend
    {
        public List<(Uri Url, bool Auto)> Opened { get; } = [];
        public NativeOpenOutcome Outcome { get; set; } = NativeOpenOutcome.Sent;
        public Exception? Throw { get; set; }
        public string AppVersion => "9.8.7";

        public NativeOpenOutcome Open(Uri url, bool auto)
        {
            if (Throw is not null) throw Throw;
            Opened.Add((url, auto));
            return Outcome;
        }

        public IReadOnlyList<string> AutoRouteHosts() => ["*.zoom.us"];
    }

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static List<JsonElement> Serve(FakeBackend backend, params byte[][] frames)
    {
        using var input = new MemoryStream(frames.SelectMany(f => f).ToArray());
        using var output = new MemoryStream();
        NativeHost.Serve(input, output, backend);
        output.Position = 0;
        var replies = new List<JsonElement>();
        while (NativeMessageFraming.Read(output) is { } reply) replies.Add(JsonDocument.Parse(reply).RootElement.Clone());
        return replies;
    }

    [Fact]
    public void Ping_answers_with_the_app_version()
    {
        var reply = Assert.Single(Serve(new FakeBackend(), Frame("""{"v":1,"type":"ping"}""")));
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("pong", reply.GetProperty("action").GetString());
        Assert.Equal("9.8.7", reply.GetProperty("app").GetString());
    }

    [Theory]
    [InlineData(NativeOpenOutcome.Sent, "sent", "running")]
    [InlineData(NativeOpenOutcome.Started, "sent", "started")]
    [InlineData(NativeOpenOutcome.StartedTied, "sent", "startedTied")]
    [InlineData(NativeOpenOutcome.Stay, "stay", null)]
    public void Open_reports_what_the_backend_did(NativeOpenOutcome outcome, string action, string? via)
    {
        var backend = new FakeBackend { Outcome = outcome };
        var reply = Assert.Single(Serve(backend, Frame("""{"v":1,"type":"open","url":"https://example.com/a","auto":true}""")));
        Assert.Equal(action, reply.GetProperty("action").GetString());
        Assert.Equal(via, reply.TryGetProperty("via", out var v) ? v.GetString() : null);
        Assert.Equal((new Uri("https://example.com/a"), true), Assert.Single(backend.Opened));
    }

    [Theory]
    [InlineData("""{"v":1,"type":"open","url":"javascript:alert(1)"}""")]
    [InlineData("""{"v":1,"type":"open","url":"file:///etc/passwd"}""")]
    [InlineData("""{"v":1,"type":"open"}""")]
    [InlineData("""{"v":1,"type":"open","url":"https://exa mple.com"}""")]
    public void Open_never_passes_on_an_invalid_link(string json)
    {
        var backend = new FakeBackend();
        var reply = Assert.Single(Serve(backend, Frame(json)));
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Empty(backend.Opened);
    }

    [Theory]
    [InlineData("""{"v":2,"type":"ping"}""", "version")]
    [InlineData("""{"v":1,"type":"launch"}""", "Unknown")]
    [InlineData("not json", "valid")]
    [InlineData("null", "valid")]
    public void Bad_requests_get_an_error(string json, string expected)
    {
        var reply = Assert.Single(Serve(new FakeBackend(), Frame(json)));
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains(expected, reply.GetProperty("error").GetString());
    }

    [Fact]
    public void A_failed_hand_over_is_reported_not_thrown()
    {
        var backend = new FakeBackend { Throw = new IOException("nope") };
        var reply = Assert.Single(Serve(backend, Frame("""{"v":1,"type":"open","url":"https://example.com/"}""")));
        Assert.Equal("nope", reply.GetProperty("error").GetString());
    }

    [Fact]
    public void Several_messages_in_one_session_each_get_a_reply()
    {
        var replies = Serve(new FakeBackend(), Frame("""{"v":1,"type":"ping"}"""), Frame("""{"v":1,"type":"autoRoutes"}"""));
        Assert.Equal(2, replies.Count);
        Assert.Equal("*.zoom.us", replies[1].GetProperty("hosts")[0].GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(NativeMessageFraming.MaxRequestBytes + 1)]
    public void A_bad_length_ends_the_session_with_an_error(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        var replies = Serve(new FakeBackend(), header, Frame("""{"v":1,"type":"ping"}"""));
        var reply = Assert.Single(replies);
        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void A_truncated_message_ends_the_session_with_an_error()
    {
        var frame = Frame("""{"v":1,"type":"ping"}""");
        var reply = Assert.Single(Serve(new FakeBackend(), frame[..^3]));
        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void An_empty_input_ends_quietly()
    {
        Assert.Empty(Serve(new FakeBackend()));
    }
}

public sealed class NativeHostBackendTests
{
    private sealed class Boot : ISystemBoot
    {
        public string CurrentBootId => "b";
        public bool IsCurrentBoot(string bootId) => bootId == "b";
    }

    private sealed class MemoryConfig(AppConfig config) : IConfigStore
    {
        public AppConfig Current => config;
        public string? LoadError => null;
        public void Update(Action<AppConfig> mutate) => mutate(config);
        public event EventHandler? Changed { add { } remove { } }
    }

    private static readonly SourceApp Chrome = new("chrome", "/opt/google/chrome/chrome", "Google Chrome");
    private static readonly SourceApp Firefox = new("firefox", "/usr/lib/firefox/firefox", "Firefox");

    private static readonly BrowserProfile ChromePlain = new() { Name = "Chrome", ExecutablePath = "/usr/bin/google-chrome-stable" };
    private static readonly BrowserProfile ChromeWork = new()
        { Name = "Chrome (Work)", ExecutablePath = "/usr/bin/google-chrome-stable", Arguments = "--profile-directory=\"Profile 1\"", ProfileLabel = "Work" };
    private static readonly BrowserProfile ChromeIncognito = new()
        { Name = "Chrome Incognito", ExecutablePath = "/usr/bin/google-chrome-stable", Arguments = "--incognito", IsPrivate = true };
    private static readonly BrowserProfile FirefoxPlain = new() { Name = "Firefox", ExecutablePath = "firefox" };

    private static AppConfig Config(params (string Domain, BrowserProfile Browser)[] rules) => new()
    {
        Browsers = [ChromePlain, ChromeWork, ChromeIncognito, FirefoxPlain],
        Rules = rules.Select(r => new Rule { Name = r.Domain, BrowserId = r.Browser.Id, Conditions = [RuleCondition.Link(MatchKind.Domain, r.Domain)] }).ToList(),
    };

    private static (NativeHostBackend Backend, List<(Uri, SourceApp?)> Delivered) Make(AppConfig config, SourceApp? caller, ActivityLog? activity = null)
    {
        var delivered = new List<(Uri, SourceApp?)>();
        var backend = new NativeHostBackend(new MemoryConfig(config), new RuleEngine(new Boot()), activity, caller, "1.0.0",
            (url, source) =>
            {
                delivered.Add((url, source));
                return NativeOpenOutcome.Sent;
            });
        return (backend, delivered);
    }

    [Fact]
    public void A_link_whose_rule_picks_the_calling_browser_stays()
    {
        var (backend, delivered) = Make(Config(("github.com", ChromePlain)), Chrome);
        Assert.Equal(NativeOpenOutcome.Stay, backend.Open(new Uri("https://github.com/"), auto: false));
        Assert.Empty(delivered);
    }

    [Fact]
    public void A_link_for_another_browser_is_handed_over_with_the_caller_as_its_source()
    {
        var (backend, delivered) = Make(Config(("github.com", FirefoxPlain)), Chrome);
        Assert.Equal(NativeOpenOutcome.Sent, backend.Open(new Uri("https://github.com/"), auto: false));
        Assert.Equal((new Uri("https://github.com/"), Chrome), Assert.Single(delivered));
    }

    [Theory]
    [InlineData("work.example")]
    [InlineData("bank.example")]
    public void A_profile_or_private_window_of_the_same_browser_is_still_opened(string domain)
    {
        var (backend, delivered) = Make(Config(("work.example", ChromeWork), ("bank.example", ChromeIncognito)), Chrome);
        Assert.Equal(NativeOpenOutcome.Sent, backend.Open(new Uri($"https://{domain}/"), auto: false));
        Assert.Single(delivered);
    }

    [Fact]
    public void Unmatched_links_go_to_the_chooser()
    {
        var (backend, delivered) = Make(Config(("github.com", ChromePlain)), Chrome);
        Assert.Equal(NativeOpenOutcome.Sent, backend.Open(new Uri("https://other.example/"), auto: false));
        Assert.Single(delivered);
    }

    [Fact]
    public void With_routing_off_the_primary_browser_decides()
    {
        var config = Config(("github.com", FirefoxPlain));
        config.Settings.RoutingEnabled = false;
        config.Settings.PrimaryBrowserId = ChromePlain.Id;
        var (backend, delivered) = Make(config, Chrome);
        Assert.Equal(NativeOpenOutcome.Stay, backend.Open(new Uri("https://github.com/"), auto: false));
        Assert.Empty(delivered);
    }

    [Fact]
    public void An_unknown_caller_never_stays()
    {
        var (backend, delivered) = Make(Config(("github.com", ChromePlain)), caller: null);
        Assert.Equal(NativeOpenOutcome.Sent, backend.Open(new Uri("https://github.com/"), auto: false));
        Assert.Single(delivered);
    }

    [Fact]
    public void Automatic_routing_leaves_a_link_NudgeJudge_just_opened()
    {
        var url = new Uri("https://zoom.us/j/1");
        var activity = new ActivityLog();
        activity.Add(new ActivityEntry(DateTimeOffset.UtcNow, url.AbsoluteUri, ActivityOutcome.Routed, "Chrome"));
        var (backend, delivered) = Make(Config(("zoom.us", FirefoxPlain)), Chrome, activity);

        Assert.Equal(NativeOpenOutcome.Stay, backend.Open(url, auto: true));
        // A deliberate gesture still goes through.
        Assert.Equal(NativeOpenOutcome.Sent, backend.Open(url, auto: false));
        Assert.Single(delivered);
    }

    [Theory]
    [InlineData("/opt/google/chrome/chrome", "/usr/bin/google-chrome-stable", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true)]
    [InlineData("/opt/vivaldi/vivaldi-bin", "/usr/bin/vivaldi-stable", true)]
    [InlineData("/usr/lib/firefox/firefox", "/usr/bin/google-chrome-stable", false)]
    [InlineData("/opt/microsoft/msedge/msedge", "/usr/bin/google-chrome-stable", false)]
    [InlineData("/usr/bin/some-app", "/usr/bin/other-app", false)]
    public void Same_browser_means_the_same_family(string callerPath, string targetExe, bool same)
    {
        var target = new BrowserProfile { Name = "x", ExecutablePath = targetExe };
        Assert.Equal(same, NativeHostBackend.IsSameBrowser(target, new SourceApp(Path.GetFileName(callerPath), callerPath)));
    }

    [Fact]
    public void Arguments_beyond_the_url_make_it_a_different_window()
    {
        var target = new BrowserProfile { Name = "x", ExecutablePath = "/usr/bin/google-chrome-stable", Arguments = "--new-window {url}" };
        Assert.False(NativeHostBackend.IsSameBrowser(target, Chrome));
        target.Arguments = "{url}";
        Assert.True(NativeHostBackend.IsSameBrowser(target, Chrome));
    }

    [Fact]
    public void Hosts_come_from_positive_host_based_link_conditions()
    {
        var rule = new Rule
        {
            Conditions =
            [
                RuleCondition.Link(MatchKind.Domain, "Zoom.US", " "),
                RuleCondition.Link(MatchKind.Wildcard, "*.acme.io/docs/*"),
                RuleCondition.Link(MatchKind.PathPrefix, "https://github.com/acme"),
                new RuleCondition { Field = ConditionField.Link, MatchKind = MatchKind.Domain, Negate = true, Values = ["skip.example"] },
                RuleCondition.Source("slack"),
            ],
        };
        Assert.Equal(["zoom.us", "*.acme.io", "github.com"], NativeHostBackend.HostsOf(rule));
    }

    [Theory]
    [InlineData(MatchKind.Regex, "^https://zoom")]
    [InlineData(MatchKind.Wildcard, "*")]
    [InlineData(MatchKind.Wildcard, "*/meeting")]
    public void A_rule_that_could_match_any_host_gives_no_hosts(MatchKind kind, string pattern)
    {
        var rule = new Rule { Conditions = [RuleCondition.Link(MatchKind.Domain, "zoom.us"), RuleCondition.Link(kind, pattern)] };
        Assert.Empty(NativeHostBackend.HostsOf(rule));
    }

    [Fact]
    public void A_rule_without_link_conditions_gives_no_hosts()
    {
        Assert.Empty(NativeHostBackend.HostsOf(new Rule { Conditions = [RuleCondition.Source("slack")] }));
    }

    [Fact]
    public void Auto_route_hosts_only_cover_enabled_live_rules_marked_to_catch()
    {
        var config = Config(("zoom.us", FirefoxPlain), ("meet.google.com", FirefoxPlain), ("off.example", FirefoxPlain), ("old.example", FirefoxPlain));
        config.Rules[0].CatchInBrowser = true;
        config.Rules[2].CatchInBrowser = true;
        config.Rules[2].Enabled = false;
        config.Rules[3].CatchInBrowser = true;
        config.Rules[3].ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        var (backend, _) = Make(config, Chrome);
        Assert.Equal(["zoom.us"], backend.AutoRouteHosts());
    }
}

public sealed class NativeHostRegistrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nj-native-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Home => Path.Combine(_root, "home");
    private string ConfigHome => Path.Combine(Home, ".config");
    private string AppDir => Path.Combine(ConfigHome, "nudgejudge");

    private LinuxNativeHostRegistration Make(IReadOnlyList<string>? command) =>
        new(AppDir, Home, ConfigHome, command);

    [LinuxFact]
    public void Enabling_writes_the_launcher_and_a_manifest_for_each_installed_browser()
    {
        Directory.CreateDirectory(Path.Combine(ConfigHome, "google-chrome"));
        Directory.CreateDirectory(Path.Combine(ConfigHome, "BraveSoftware", "Brave-Browser"));
        var reg = Make(["/opt/NudgeJudge/NudgeJudge.Desktop"]);

        reg.SetEnabled(true);

        Assert.True(reg.IsEnabled());
        Assert.True(OperatingSystem.IsWindows() || (File.GetUnixFileMode(reg.LauncherPath) & UnixFileMode.UserExecute) != 0);
        var chrome = Read(Path.Combine(ConfigHome, "google-chrome", "NativeMessagingHosts", "com.nudgejudge.host.json"));
        Assert.Equal("com.nudgejudge.host", chrome.GetProperty("name").GetString());
        Assert.Equal(reg.LauncherPath, chrome.GetProperty("path").GetString());
        Assert.Equal("stdio", chrome.GetProperty("type").GetString());
        Assert.Contains(chrome.GetProperty("allowed_origins").EnumerateArray(),
            o => o.GetString() == $"chrome-extension://{NativeHostRegistration.ChromiumExtensionIds[0]}/");
        Assert.True(File.Exists(Path.Combine(ConfigHome, "BraveSoftware", "Brave-Browser", "NativeMessagingHosts", "com.nudgejudge.host.json")));
        var firefox = Read(Path.Combine(Home, ".mozilla", "native-messaging-hosts", "com.nudgejudge.host.json"));
        Assert.Equal(NativeHostRegistration.FirefoxExtensionId, firefox.GetProperty("allowed_extensions")[0].GetString());

        // Browsers that aren't installed get nothing.
        Assert.False(Directory.Exists(Path.Combine(ConfigHome, "chromium")));
        Assert.False(Directory.Exists(Path.Combine(ConfigHome, "microsoft-edge")));
    }

    [LinuxFact]
    public void Disabling_removes_everything_it_wrote()
    {
        Directory.CreateDirectory(Path.Combine(ConfigHome, "chromium"));
        var reg = Make(["/opt/NudgeJudge/NudgeJudge.Desktop"]);
        reg.SetEnabled(true);

        reg.SetEnabled(false);

        Assert.False(reg.IsEnabled());
        Assert.False(File.Exists(Path.Combine(ConfigHome, "chromium", "NativeMessagingHosts", "com.nudgejudge.host.json")));
        Assert.False(File.Exists(Path.Combine(Home, ".mozilla", "native-messaging-hosts", "com.nudgejudge.host.json")));
    }

    [LinuxFact]
    public void Refresh_follows_a_moved_app_and_picks_up_new_browsers_but_only_when_on()
    {
        var off = Make(["/new/NudgeJudge.Desktop"]);
        off.Refresh();
        Assert.False(off.IsEnabled());

        Make(["/old/NudgeJudge.Desktop"]).SetEnabled(true);
        Directory.CreateDirectory(Path.Combine(ConfigHome, "microsoft-edge"));
        var moved = Make(["/new/NudgeJudge.Desktop"]);
        moved.Refresh();

        Assert.Contains("'/new/NudgeJudge.Desktop'", File.ReadAllText(moved.LauncherPath));
        Assert.True(File.Exists(Path.Combine(ConfigHome, "microsoft-edge", "NativeMessagingHosts", "com.nudgejudge.host.json")));
    }

    [Fact]
    public void The_launcher_quotes_every_argument_and_passes_the_browsers_on()
    {
        var script = Make(["/home/me/.dotnet/dotnet", "/home/me/it's here/NudgeJudge.Desktop.dll"]).Script!;
        Assert.Contains("""exec '/home/me/.dotnet/dotnet' '/home/me/it'\''s here/NudgeJudge.Desktop.dll' --native-host "$@" """.TrimEnd(), script);
        Assert.StartsWith("#!/bin/sh\n", script);
    }

    [Fact]
    public void No_launch_command_means_unsupported()
    {
        Assert.False(Make(null).IsSupported);
        Assert.False(Make(["/bin/x\nrm -rf"]).IsSupported);
    }

    [Fact]
    public void The_Windows_launcher_escapes_percent_signs_and_refuses_quotes()
    {
        var batch = NativeHostRegistration.WindowsBatch([@"C:\Users\a%b\NudgeJudge.Desktop.exe"])!;
        Assert.Contains("\"C:\\Users\\a%%b\\NudgeJudge.Desktop.exe\" --native-host %*", batch);
        Assert.Null(NativeHostRegistration.WindowsBatch([@"C:\a""b.exe"]));
    }

    private static JsonElement Read(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
}

public sealed class LaunchSourceTests
{
    [Fact]
    public void A_relayed_source_round_trips_through_the_command_line()
    {
        var source = new SourceApp("chrome", "/opt/google/chrome/chrome", "Google Chrome");
        var launch = LaunchArguments.Parse([LaunchArguments.EncodeSource(source), "https://example.com/"]);
        Assert.Equal(source, launch.Source);
        Assert.Equal(["https://example.com/"], launch.Urls);
    }

    [Theory]
    [InlineData("--source=!!!")]
    [InlineData("--source=bnVsbA==")] // "null"
    public void A_bad_source_is_ignored(string arg)
    {
        Assert.Null(LaunchArguments.Parse([arg, "https://example.com/"]).Source);
    }
}

public sealed class TiedLauncherActivityTests
{
    private sealed class Boot : ISystemBoot
    {
        public string CurrentBootId => "b";
        public bool IsCurrentBoot(string bootId) => bootId == "b";
    }

    private sealed class Launcher : Browsers.IBrowserLauncher
    {
        public void Launch(BrowserProfile browser, Uri url) { }
    }

    private static (LinkRouter, ActivityLog) Router(bool tied)
    {
        var chrome = new BrowserProfile { Name = "Chrome", ExecutablePath = "/usr/bin/chrome" };
        var config = new JsonConfigStore(Path.Combine(Directory.CreateTempSubdirectory("nj-tied-").FullName, "config.json"));
        config.Update(c =>
        {
            c.Browsers.Add(chrome);
            c.Rules.Add(new Rule { Name = "a", BrowserId = chrome.Id, Conditions = [RuleCondition.Link(MatchKind.Domain, "a.example")] });
        });
        var log = new ActivityLog();
        return (new LinkRouter(config, new RuleEngine(new Boot()), new Launcher(), log) { TiedToLauncher = tied }, log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_entry_says_whether_a_tied_NudgeJudge_handled_it(bool tied)
    {
        var (router, log) = Router(tied);
        router.Route("https://a.example/");
        router.Route("https://b.example/");
        router.Route("not a link");
        Assert.All(log.Snapshot(), e => Assert.Equal(tied, e.TiedToLauncher));
    }

    [Fact]
    public void The_flag_survives_the_log_file_and_is_left_out_when_false()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("nj-tied-log-").FullName, "activity.log");
        var file = new ActivityLogFile(path);
        var log = new ActivityLog(file);
        log.Add(new ActivityEntry(DateTimeOffset.UtcNow, "https://a.example/", ActivityOutcome.Routed, TiedToLauncher: true));
        log.Add(new ActivityEntry(DateTimeOffset.UtcNow, "https://b.example/", ActivityOutcome.Routed));

        var reread = new ActivityLog(new ActivityLogFile(path)).Snapshot();
        Assert.Equal([true, false], reread.Select(e => e.TiedToLauncher));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), "tiedToLauncher", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
}
