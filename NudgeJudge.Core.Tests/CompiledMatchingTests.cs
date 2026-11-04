using System.Text;
using System.Text.RegularExpressions;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core.Tests;

/// <summary>
/// Wildcards compile to plain string matching and rules are compiled once. These check that both give the
/// same answers as before: globs against the regex translation they replaced, and cached rules against edits.
/// </summary>
public class CompiledMatchingTests
{
    private sealed class Boot : ISystemBoot
    {
        public string CurrentBootId => "b";
        public bool IsCurrentBoot(string bootId) => bootId == "b";
    }

    private static readonly string[] Patterns =
    [
        "*.acme.io", "acme.io", "*acme*", "docs.*.com", "*.a*b.io", "a*b*c.com", "*", "*.*.io", "ab*ba.com", "aba*aba.io",
        "github.com/acme/*", "github.com/*/pulls", "*.acme.io/a*b/*c", "x.com/A*", "x.com/*a*a*", "*.acme.io/*",
    ];

    private static readonly string[] Urls =
    [
        "https://acme.io/", "https://jira.acme.io/x", "https://a.b.acme.io/", "https://evilacme.io/", "https://acme.io.evil.com/",
        "https://ACME.IO/", "https://acme.io./", "https://docs.example.com/", "https://docs.com/", "https://x.ab.io/",
        "https://x.ayyb.io/", "https://abc.com/", "https://abbc.com/", "https://acb.com/", "https://aba.com/", "https://abba.com/",
        "https://ababa.com/", "https://abaaba.io/", "https://aba.io/", "https://abaxaba.io/", "https://github.com/acme/repo",
        "https://github.com/acme", "https://github.com/x/pulls", "https://github.com/x/y/pulls", "https://jira.acme.io/aXb/yc",
        "https://jira.acme.io/ab/c", "https://jira.acme.io/ab/cd", "https://x.com/a", "https://x.com/ABC", "https://x.com/banana",
        "https://x.com/ba", "https://x.com/aa", "https://acme.io/anything?q=1", "https://www.münchen.de/",
    ];

    public static TheoryData<string, string> Grid()
    {
        var data = new TheoryData<string, string>();
        foreach (var p in Patterns)
            foreach (var u in Urls)
                data.Add(p, u);
        return data;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Wildcards_match_exactly_what_their_regex_translation_did(string pattern, string url)
    {
        var compiled = PatternCompiler.Compile(MatchKind.Wildcard, pattern);
        Assert.True(compiled.Success, compiled.Error);
        Assert.Equal(ReferenceWildcard(pattern, new Uri(url)), compiled.Pattern!.IsMatch(new Uri(url)));
    }

    /// <summary>How wildcards used to be matched: translated to an anchored, case-insensitive regex.</summary>
    private static bool ReferenceWildcard(string pattern, Uri url)
    {
        var slash = pattern.IndexOf('/');
        var host = (slash >= 0 ? pattern[..slash] : pattern).ToLowerInvariant();
        var path = slash >= 0 ? pattern[slash..] : "";
        var hostRegex = host.StartsWith("*.", StringComparison.Ordinal)
            ? "^(?:[^/]*\\.)?" + Glob(host[2..], "[^/]*") + "$"
            : "^" + Glob(host, "[^/]*") + "$";
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var hostMatches = Regex.IsMatch(url.IdnHost.TrimEnd('.'), hostRegex, options) ||
                          (url.Host != url.IdnHost && Regex.IsMatch(url.Host.TrimEnd('.'), hostRegex, options));
        return hostMatches && (path.Length == 0 || Regex.IsMatch(url.AbsolutePath, "^" + Glob(path, ".*") + "$", options));

        static string Glob(string glob, string star)
        {
            var sb = new StringBuilder();
            foreach (var c in glob) sb.Append(c == '*' ? star : Regex.Escape(c.ToString()));
            return sb.ToString();
        }
    }

    [Theory]
    [InlineData("*teams*", true)]
    [InlineData("ms-team?", true)]       // '?' goes through the regex fallback, tried without .exe too
    [InlineData("ms-team?.exe", true)]
    [InlineData("*-teams", true)]        // the name without .exe
    [InlineData("microsoft*", true)]     // the title
    [InlineData("*\\MSTeams\\*", true)]  // the path
    [InlineData("*slack*", false)]
    [InlineData("ms-team?x", false)]
    public void Source_app_globs(string pattern, bool expected)
    {
        var teams = new SourceApp("ms-teams.exe", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", "Microsoft Teams");
        Assert.Equal(expected, SourceAppPattern.Compile(pattern).Predicate!(teams));
    }

    [Fact]
    public void An_unchanged_rule_is_compiled_once()
    {
        var engine = new RuleEngine(new Boot());
        var rule = new Rule { Name = "r", Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.acme.io")] };

        Assert.Same(engine.Compile(rule), engine.Compile(rule));
        // A copy compiles the same, so it may share the result too.
        Assert.Same(engine.Compile(rule), engine.Compile(rule.Clone()));
    }

    [Fact]
    public void Rules_edited_in_place_are_recompiled()
    {
        var engine = new RuleEngine(new Boot());
        var inner = RuleCondition.Source("slack");
        var rule = new Rule
        {
            Name = "r",
            Conditions = [RuleCondition.Link(MatchKind.Domain, "a.com"), RuleCondition.Group(RuleJoin.Any, inner)],
        };
        var slack = new SourceApp("slack");
        bool Matches(string url, SourceApp? source = null) => engine.Match(new Uri(url), [rule], source).IsMatch;

        Assert.True(Matches("https://a.com/", slack));

        rule.Conditions[0].Values[0] = "b.com";
        Assert.False(Matches("https://a.com/", slack));
        Assert.True(Matches("https://b.com/", slack));

        inner.Values[0] = "teams";
        Assert.False(Matches("https://b.com/", slack));

        inner.Negate = true;
        Assert.True(Matches("https://b.com/", slack));

        rule.Join = RuleJoin.Any;
        inner.Negate = false;
        Assert.True(Matches("https://c.com/", new SourceApp("teams")));

        rule.Conditions.RemoveAt(1);
        Assert.False(Matches("https://c.com/", new SourceApp("teams")));
    }

    [Fact]
    public void FindMatch_agrees_with_Match()
    {
        var engine = new RuleEngine(new Boot());
        var rules = new List<Rule>
        {
            new() { Name = "off", Enabled = false, Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")] },
            new() { Name = "broken", Conditions = [RuleCondition.Link(MatchKind.Regex, "(")] },
            new() { Name = "acme", Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.acme.io")] },
            new() { Name = "all", Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")] },
        };
        var prepared = engine.Prepare(rules);

        foreach (var url in new[] { "https://jira.acme.io/", "https://example.com/" })
        {
            var full = engine.Match(new Uri(url), rules);
            Assert.Equal((full.Rule, full.RuleIndex), engine.FindMatch(new Uri(url), rules));
            Assert.Equal((full.Rule, full.RuleIndex), engine.FindMatch(new Uri(url), prepared));
        }
    }

    private sealed class Store(AppConfig config) : IConfigStore
    {
        public AppConfig Current { get; } = config;
        public string? LoadError => null;

        public void Update(Action<AppConfig> mutate)
        {
            mutate(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? Changed;
    }

    private sealed class Launcher : IBrowserLauncher
    {
        public List<string> Launched { get; } = [];
        public void Launch(BrowserProfile browser, Uri url) => Launched.Add(browser.Name);
    }

    [Fact]
    public void Router_follows_rule_changes()
    {
        var firefox = new BrowserProfile { Name = "Firefox", ExecutablePath = "firefox" };
        var chrome = new BrowserProfile { Name = "Chrome", ExecutablePath = "chrome" };
        var config = new AppConfig { Browsers = [firefox, chrome] };
        var store = new Store(config);
        var launcher = new Launcher();
        var router = new LinkRouter(store, new RuleEngine(new Boot()), launcher, new ActivityLog());

        Assert.Equal(RouteKind.NeedsChoice, router.Route("https://jira.acme.io/").Kind);

        store.Update(c => c.Rules.Add(new Rule
        {
            Name = "Acme", BrowserId = firefox.Id, Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.acme.io")],
        }));
        router.Route("https://jira.acme.io/");

        store.Update(c => c.Rules[0].BrowserId = chrome.Id);
        router.Route("https://jira.acme.io/");

        store.Update(c => c.Rules[0].Conditions[0].Values[0] = "*.other.io");
        Assert.Equal(RouteKind.NeedsChoice, router.Route("https://jira.acme.io/").Kind);

        Assert.Equal(["Firefox", "Chrome"], launcher.Launched);
    }
}
