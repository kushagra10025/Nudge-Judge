using System.Text;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Ipc;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core.Tests;

public class CompoundRuleTests
{
    private sealed class Boot : ISystemBoot
    {
        public string CurrentBootId => "b";
        public bool IsCurrentBoot(string bootId) => bootId == "b";
    }

    private static readonly SourceApp Teams = new("ms-teams.exe", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", "Microsoft Teams");
    private static readonly SourceApp Slack = new("slack.exe", null, "Slack");

    private static readonly Guid Chrome = Guid.NewGuid();
    private static readonly Guid Firefox = Guid.NewGuid();

    /// <summary>YouTube from Teams opens in Chrome; YouTube from anywhere else in Firefox.</summary>
    private static List<Rule> YouTubeRules() =>
    [
        new()
        {
            Name = "YouTube from Teams",
            Join = RuleJoin.All,
            Conditions = [RuleCondition.Link(MatchKind.Domain, "www.youtube.com", "youtu.be"), RuleCondition.Source("teams.exe", "ms-teams.exe")],
            BrowserId = Chrome,
        },
        new()
        {
            Name = "YouTube",
            Conditions = [RuleCondition.Link(MatchKind.Domain, "www.youtube.com", "youtu.be")],
            BrowserId = Firefox,
        },
    ];

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", "teams", "YouTube from Teams")]
    [InlineData("https://youtu.be/abc", "teams", "YouTube from Teams")]
    [InlineData("https://youtu.be/abc", "slack", "YouTube")]
    [InlineData("https://youtu.be/abc", null, "YouTube")]
    [InlineData("https://example.com/", "teams", null)]
    public void Link_alternatives_are_ORed_and_conditions_ANDed(string url, string? from, string? expected)
    {
        var source = from switch { "teams" => Teams, "slack" => Slack, _ => null };
        var result = new RuleEngine(new Boot()).Match(new Uri(url), YouTubeRules(), source);
        Assert.Equal(expected, result.Rule?.Name);
    }

    [Fact]
    public void Any_join_matches_when_one_condition_holds()
    {
        var rule = new Rule
        {
            Name = "Chat or docs",
            Join = RuleJoin.Any,
            Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.notion.so"), RuleCondition.Source("slack")],
        };
        var engine = new RuleEngine(new Boot());

        Assert.True(engine.Match(new Uri("https://example.com/"), [rule], Slack).IsMatch);
        Assert.True(engine.Match(new Uri("https://acme.notion.so/x"), [rule], null).IsMatch);
        Assert.False(engine.Match(new Uri("https://example.com/"), [rule], Teams).IsMatch);
    }

    [Fact]
    public void Negated_conditions_hold_when_no_value_matches()
    {
        var rule = new Rule
        {
            Name = "Not Teams, not YouTube",
            Conditions =
            [
                new RuleCondition { Field = ConditionField.SourceApp, Negate = true, Values = ["ms-teams"] },
                new RuleCondition { Field = ConditionField.Link, MatchKind = MatchKind.Domain, Negate = true, Values = ["youtu.be"] },
            ],
        };
        var engine = new RuleEngine(new Boot());

        Assert.True(engine.Match(new Uri("https://example.com/"), [rule], Slack).IsMatch);
        Assert.True(engine.Match(new Uri("https://example.com/"), [rule], null).IsMatch); // unknown source isn't Teams
        Assert.False(engine.Match(new Uri("https://example.com/"), [rule], Teams).IsMatch);
        Assert.False(engine.Match(new Uri("https://youtu.be/x"), [rule], Slack).IsMatch);
    }

    [Theory]
    [InlineData("ms-teams.exe", true)]
    [InlineData("MS-TEAMS", true)]                 // .exe optional, case ignored
    [InlineData("Microsoft Teams", true)]          // the friendly title
    [InlineData("*teams*", true)]                  // glob over name, title and path
    [InlineData(@"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", true)]
    [InlineData("teams", false)]                   // a different program name
    [InlineData("ms-teams.exe.evil", false)]
    [InlineData(@"C:\Other\ms-teams.exe", false)]
    public void Source_app_values(string value, bool expected)
    {
        var compiled = SourceAppPattern.Compile(value);
        Assert.True(compiled.Success, compiled.Error);
        Assert.Equal(expected, compiled.Predicate!(Teams));
        Assert.False(compiled.Predicate!(null)); // unknown source never matches a positive condition
    }

    [Fact]
    public void Linux_program_names_match_without_extension() =>
        Assert.True(SourceAppPattern.Compile("teams-for-linux").Predicate!(new SourceApp("teams-for-linux", "/opt/teams-for-linux/teams-for-linux")));

    [Fact]
    public void Compile_errors_name_the_condition_and_value()
    {
        var engine = new RuleEngine(new Boot());

        Assert.Equal("Add a condition.", engine.Compile(new Rule { Name = "x" }).Error);

        var empty = engine.Compile(new Rule { Name = "x", Conditions = [RuleCondition.Source(" ")] });
        Assert.Equal("Enter an app, e.g. ms-teams.exe.", empty.Error);

        var bad = engine.Compile(new Rule
        {
            Name = "x",
            Conditions = [RuleCondition.Source("slack"), RuleCondition.Link(MatchKind.Domain, "youtube.com", "*.youtu.be")],
        });
        Assert.False(bad.Success);
        Assert.StartsWith("Condition 2: “*.youtu.be”: Domain rules don't take wildcards", bad.Error);
        Assert.Equal([null, bad.Conditions[1].ValueErrors[1]], bad.Conditions[1].ValueErrors);
        Assert.NotNull(bad.Conditions[1].ValueErrors[1]);
        Assert.True(bad.Conditions[0].Success);
    }

    [Fact]
    public void Blank_alternatives_are_ignored()
    {
        var rule = new Rule { Name = "x", Conditions = [RuleCondition.Link(MatchKind.Domain, "", "youtu.be", "  ")] };
        var engine = new RuleEngine(new Boot());
        Assert.True(engine.Compile(rule).Success);
        Assert.True(engine.Match(new Uri("https://youtu.be/"), [rule]).IsMatch);
    }

    // ---- Older configs ----

    [Fact]
    public void Single_pattern_configs_upgrade_to_a_named_link_condition()
    {
        var dir = Directory.CreateTempSubdirectory("nj-upgrade-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json");
            var id = Guid.NewGuid();
            File.WriteAllText(path, $$"""
                {
                  "version": 1,
                  "rules": [
                    { "id": "{{id}}", "name": "", "matchKind": "PathPrefix", "pattern": "github.com/acme", "caseSensitive": true, "browserId": "{{Chrome}}" },
                    { "name": "Work", "matchKind": "Wildcard", "pattern": "*.acme.io", "browserId": "{{Chrome}}" }
                  ]
                }
                """);

            var store = new JsonConfigStore(path);
            var unnamed = store.Current.Rules[0];
            Assert.Equal(id, unnamed.Id);
            Assert.Equal("github.com/acme", unnamed.Name); // names are required now: the pattern stands in
            var condition = Assert.Single(unnamed.Conditions);
            Assert.Equal(ConditionField.Link, condition.Field);
            Assert.Equal(MatchKind.PathPrefix, condition.MatchKind);
            Assert.True(condition.CaseSensitive);
            Assert.Equal(["github.com/acme"], condition.Values);
            Assert.Equal("Work", store.Current.Rules[1].Name);

            // It still routes the same way.
            Assert.True(new RuleEngine(new Boot()).Match(new Uri("https://github.com/acme/x"), store.Current.Rules).IsMatch);

            // The next save writes the new shape only.
            store.Update(_ => { });
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("\"pattern\"", json);
            Assert.Contains("\"conditions\"", json);
            Assert.Contains($"\"version\": {AppConfig.CurrentVersion}", json);

            var reread = new JsonConfigStore(path).Current.Rules[0];
            Assert.Equal(["github.com/acme"], reread.Conditions.Single().Values);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Routing and Activity ----

    private sealed class Launcher : IBrowserLauncher
    {
        public List<(BrowserProfile, Uri)> Launched { get; } = [];
        public void Launch(BrowserProfile browser, Uri url) => Launched.Add((browser, url));
    }

    private sealed class Store(AppConfig config) : IConfigStore
    {
        public AppConfig Current { get; } = config;
        public string? LoadError => null;
        public void Update(Action<AppConfig> mutate) => mutate(Current);
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class BrokenLauncher : IBrowserLauncher
    {
        public void Launch(BrowserProfile browser, Uri url) => throw new System.ComponentModel.Win32Exception(2);
    }

    [Fact]
    public void Router_asks_when_the_rules_browser_wont_start()
    {
        var config = new AppConfig
        {
            Browsers = [new() { Id = Chrome, Name = "Chrome", ExecutablePath = "chrome" }],
            Rules = [new() { Name = "All", Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")], BrowserId = Chrome }],
        };
        var log = new ActivityLog();
        var router = new LinkRouter(new Store(config), new RuleEngine(new Boot()), new BrokenLauncher(), log);
        IncomingLink? asked = null;
        router.ChoiceRequested += (_, link) => asked = link;

        Assert.Equal(RouteKind.NeedsChoice, router.Route("https://example.com/").Kind);
        Assert.NotNull(asked);
        Assert.Equal([ActivityOutcome.Failed, ActivityOutcome.Asked], log.Snapshot().Select(e => e.Outcome));
    }

    [Fact]
    public void Router_never_opens_a_browser_that_needs_review()
    {
        var config = new AppConfig
        {
            Browsers = [new() { Id = Chrome, Name = "Imported", ExecutablePath = "/tmp/evil", NeedsReview = true }],
            Rules = [new() { Name = "All", Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")], BrowserId = Chrome }],
        };
        config.Settings.PrimaryBrowserId = Chrome;
        var log = new ActivityLog();
        var launcher = new Launcher();
        var router = new LinkRouter(new Store(config), new RuleEngine(new Boot()), launcher, log);

        Assert.Equal(RouteKind.NeedsChoice, router.Route("https://example.com/").Kind);
        Assert.Contains("needs review", log.Snapshot()[0].Detail);

        config.Settings.RoutingEnabled = false; // the primary browser is skipped too
        Assert.Equal(RouteKind.NeedsChoice, router.Route("https://example.com/").Kind);

        Assert.Equal(RouteKind.Failed, router.OpenChosen(new Uri("https://example.com/"), config.Browsers[0]).Kind);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void Saving_a_chooser_rule_removes_expired_ones()
    {
        var expired = new Rule { Name = "Old", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var oldBoot = new Rule { Name = "Last boot", ExpiresAfterBootId = "previous" };
        var kept = new Rule { Name = "Kept" };
        var config = new AppConfig { Rules = [expired, oldBoot, kept] };
        var router = new LinkRouter(new Store(config), new RuleEngine(new Boot()), new Launcher(), new ActivityLog());

        router.AddRule(new Rule { Name = "New" }, TimeSpan.FromHours(8), untilReboot: false, new Boot());

        Assert.Equal(["New", "Kept"], config.Rules.Select(r => r.Name));
    }

    [Fact]
    public void Router_routes_on_the_source_and_logs_it_with_the_rule_name()
    {
        var config = new AppConfig
        {
            Browsers = [new() { Id = Chrome, Name = "Chrome", ExecutablePath = "chrome" }, new() { Id = Firefox, Name = "Firefox", ExecutablePath = "firefox" }],
            Rules = YouTubeRules(),
        };
        var log = new ActivityLog();
        var launcher = new Launcher();
        var router = new LinkRouter(new Store(config), new RuleEngine(new Boot()), launcher, log);

        router.Route("https://youtu.be/a", Teams);
        router.Route("https://youtu.be/b", Slack);
        IncomingLink? asked = null;
        router.ChoiceRequested += (_, link) => asked = link;
        router.Route("https://example.com/", Slack);

        Assert.Equal(["Chrome", "Firefox"], launcher.Launched.Select(l => l.Item1.Name));
        var entries = log.Snapshot();
        Assert.Equal("YouTube from Teams", entries[0].RuleName);
        Assert.Equal(Teams, entries[0].Source);
        Assert.Null(entries[0].RulePattern);
        Assert.Equal("YouTube", entries[1].RuleName);
        Assert.Equal(ActivityOutcome.Asked, entries[2].Outcome);
        Assert.Equal(Slack, entries[2].Source);
        Assert.Equal(Slack, asked?.Source);
    }

    [Fact]
    public void Activity_file_keeps_the_source_and_reads_old_entries()
    {
        var dir = Directory.CreateTempSubdirectory("nj-activity-").FullName;
        try
        {
            var path = Path.Combine(dir, "activity.log");
            File.WriteAllText(path, """{"timestamp":"2026-01-01T00:00:00+00:00","url":"https://old.example/","outcome":"Routed","ruleNumber":1,"rulePattern":"*.example"}""" + "\n");
            var file = new ActivityLogFile(path);
            file.Update(l => l.Add(new ActivityEntry(DateTimeOffset.UtcNow, "https://youtu.be/", ActivityOutcome.Routed, "Chrome", 1,
                RuleName: "YouTube from Teams", Source: Teams)));

            var entries = new ActivityLogFile(path).Read();
            Assert.Equal("*.example", entries[0].RulePattern);
            Assert.Null(entries[0].Source);
            Assert.Equal(Teams, entries[1].Source);
            Assert.Equal("YouTube from Teams", entries[1].RuleName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sources_that_crossed_a_process_boundary_are_sanitized()
    {
        Assert.Null(SourceApp.Sanitize(new SourceApp("  ")));
        Assert.Null(SourceApp.Sanitize(new SourceApp("evil\nname")));
        Assert.Null(SourceApp.Sanitize(new SourceApp(new string('a', 300))));
        // A bad optional part is dropped, not the whole source.
        Assert.Equal(new SourceApp("slack.exe", null, null), SourceApp.Sanitize(new SourceApp(" slack.exe ", "bad\0path", "\t")));
    }

    [Fact]
    public async Task Instance_requests_carry_the_source_and_old_requests_still_read()
    {
        using var stream = new MemoryStream();
        await InstanceProtocol.WriteRequestAsync(stream,
            new InstanceRequest(InstanceProtocol.Version, "t", InstanceCommand.Open, ["https://youtu.be/"], Teams), default);
        stream.Position = 0;
        var read = await InstanceProtocol.ReadRequestAsync(stream, default);
        Assert.Equal(Teams, read?.Source);

        // A request from a version that didn't send a source.
        var json = Encoding.UTF8.GetBytes("""{"version":1,"token":"t","command":"open","urls":["https://a.example/"]}""");
        using var old = new MemoryStream();
        await InstanceProtocol.WriteFrameAsync(old, json, default);
        old.Position = 0;
        var legacy = await InstanceProtocol.ReadRequestAsync(old, default);
        Assert.NotNull(legacy);
        Assert.Null(legacy.Source);
    }
}

public class SourceAppDetectorTests
{
    private sealed class Table : IProcessTable
    {
        public Dictionary<int, ProcessInfo> Processes { get; } = [];
        public int? ForegroundProcessId { get; set; }
        public SourceApp? EnvironmentHint { get; set; }
        public Dictionary<string, string> Titles { get; } = [];

        public ProcessInfo? Get(int pid) => Processes.GetValueOrDefault(pid);
        public string? Describe(ProcessInfo process) => Titles.GetValueOrDefault(process.Name);

        public Table Add(int pid, int parent, string name, string? path = null, long? started = null)
        {
            Processes[pid] = new ProcessInfo(pid, parent, name, path, started ?? pid);
            return this;
        }
    }

    [Fact]
    public void Skips_openers_and_shells_to_find_the_app()
    {
        var table = new Table()
            .Add(1, 0, "systemd")
            .Add(100, 1, "teams-for-linux", "/opt/teams-for-linux/teams-for-linux")
            .Add(200, 100, "sh")
            .Add(300, 200, "xdg-open")
            .Add(400, 300, "gio")
            .Add(500, 400, "NudgeJudge.Desktop");
        table.Titles["teams-for-linux"] = "Teams for Linux";

        var app = new SourceAppDetector(table).Detect(500);

        Assert.Equal(new SourceApp("teams-for-linux", "/opt/teams-for-linux/teams-for-linux", "Teams for Linux"), app);
    }

    [Fact]
    public void A_link_opened_from_our_own_window_is_not_from_the_tray_host()
    {
        // Settings → About → Releases, with NudgeJudge as the default browser: the window's parent is the tray host.
        var table = new Table()
            .Add(1, 0, "systemd")
            .Add(100, 1, "NudgeJudge.Tray", "/opt/nudgejudge/NudgeJudge.Tray")
            .Add(200, 100, "NudgeJudge.Desktop", "/opt/nudgejudge/NudgeJudge.Desktop")
            .Add(300, 200, "xdg-open")
            .Add(500, 300, "NudgeJudge.Desktop");

        Assert.Null(new SourceAppDetector(table).Detect(500));
    }

    [Fact]
    public void Windows_names_and_brokers()
    {
        var table = new Table()
            .Add(4, 0, "System")
            .Add(50, 4, "ms-teams.exe", @"C:\Apps\ms-teams.exe")
            .Add(60, 50, "OpenWith.exe")
            .Add(70, 60, "NudgeJudge.exe");

        Assert.Equal("ms-teams.exe", new SourceAppDetector(table).Detect(70)?.Name);
    }

    [Fact]
    public void A_parent_that_started_after_us_is_a_recycled_pid()
    {
        var table = new Table()
            .Add(42, 1, "unrelated-app", started: 9999)
            .Add(500, 42, "NudgeJudge", started: 500);

        Assert.Null(new SourceAppDetector(table).Detect(500));
    }

    [Fact]
    public void Falls_back_to_the_foreground_app_when_the_chain_dead_ends()
    {
        // The opener exited and we were re-parented to the session manager.
        var table = new Table()
            .Add(1, 0, "systemd")
            .Add(500, 1, "NudgeJudge")
            .Add(77, 1, "slack", "/usr/lib/slack/slack");
        table.ForegroundProcessId = 77;

        Assert.Equal("slack", new SourceAppDetector(table).Detect(500)?.Name);
    }

    [Fact]
    public void Prefers_the_foreground_app_over_explorer_then_settles_for_explorer()
    {
        var table = new Table()
            .Add(4, 0, "System")
            .Add(10, 4, "explorer.exe", @"C:\Windows\explorer.exe")
            .Add(500, 10, "NudgeJudge.exe")
            .Add(77, 10, "Outlook.exe", @"C:\Office\Outlook.exe");
        table.ForegroundProcessId = 77;
        Assert.Equal("Outlook.exe", new SourceAppDetector(table).Detect(500)?.Name);

        table.ForegroundProcessId = 500; // our own window: no help
        Assert.Equal("explorer.exe", new SourceAppDetector(table).Detect(500)?.Name);
    }

    [Fact]
    public void Uses_the_environment_hint_last()
    {
        var table = new Table().Add(1, 0, "systemd").Add(500, 1, "NudgeJudge");
        table.EnvironmentHint = new SourceApp("code", null, "Visual Studio Code");
        Assert.Equal("code", new SourceAppDetector(table).Detect(500)?.Name);
    }

    [Fact]
    public void Never_throws()
    {
        Assert.Null(new SourceAppDetector(new Throwing()).Detect(1));
    }

    private sealed class Throwing : IProcessTable
    {
        public ProcessInfo? Get(int pid) => throw new InvalidOperationException("boom");
        public int? ForegroundProcessId => throw new InvalidOperationException("boom");
        public string? Describe(ProcessInfo process) => null;
        public SourceApp? EnvironmentHint => null;
    }

    [LinuxTheory]
    [InlineData("1234 (teams-for-linux) S 99 1234 1234 0 -1 4194560 1 0 0 0 0 0 0 0 20 0 1 0 5551 0 0", "teams-for-linux", 99, 5551L)]
    [InlineData("77 (Web Content (x)) R 12 77 0 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 42 0 0", "Web Content (x)", 12, 42L)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Parses_proc_stat_with_awkward_names(string stat, string name, int parent, long started)
    {
        var info = LinuxProcessTable.ParseStat(1, stat, null);
        Assert.NotNull(info);
        Assert.Equal(name, info.Name);
        Assert.Equal(parent, info.ParentPid);
        Assert.Equal(started, info.Started);
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Reads_a_proc_tree_and_desktop_names_from_disk()
    {
        var root = Directory.CreateTempSubdirectory("nj-proc-").FullName;
        try
        {
            void Proc(int pid, string comm, int ppid, long start, string? exe)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, "proc", pid.ToString())).FullName;
                File.WriteAllText(Path.Combine(dir, "stat"), $"{pid} ({comm}) S {ppid} 0 0 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 {start} 0 0");
                if (exe is not null) File.CreateSymbolicLink(Path.Combine(dir, "exe"), exe);
            }
            Proc(1, "systemd", 0, 1, null);
            Proc(100, "teams-for-linu", 1, 10, "/opt/teams-for-linux/teams-for-linux"); // comm is cut at 15 bytes
            Proc(200, "xdg-open", 100, 20, "/usr/bin/dash");
            Proc(300, "NudgeJudge.Desk", 200, 30, "/opt/nudgejudge/NudgeJudge.Desktop");

            var apps = Directory.CreateDirectory(Path.Combine(root, "share", "applications")).FullName;
            File.WriteAllText(Path.Combine(apps, "teams-for-linux.desktop"), "[Desktop Entry]\nName=Teams for Linux\nExec=teams-for-linux %U\n");

            var table = new LinuxProcessTable(Path.Combine(root, "proc"), [Path.Combine(root, "share")]);
            var app = new SourceAppDetector(table).Detect(300);

            Assert.Equal(new SourceApp("teams-for-linux", "/opt/teams-for-linux/teams-for-linux", "Teams for Linux"), app);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Real_process_tree_does_not_throw()
    {
        // Whatever launched the test run: just prove the platform table reads without failing.
        _ = SourceAppDetector.ForCurrentPlatform().Detect();
    }
}
