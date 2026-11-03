using System.Diagnostics;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;
using NudgeJudge.Core.Security;

namespace NudgeJudge.Core.Tests;

public class PatternCompilerTests
{
    private static bool Matches(MatchKind kind, string pattern, string url)
    {
        var compiled = PatternCompiler.Compile(kind, pattern);
        Assert.True(compiled.Success, compiled.Error);
        return compiled.Pattern!.IsMatch(new Uri(url));
    }

    [Theory]
    [InlineData("*.acme.io", "https://jira.acme.io/browse/X-1", true)]
    [InlineData("*.acme.io", "https://acme.io/", true)]
    [InlineData("*.acme.io", "https://a.b.acme.io/", true)]
    [InlineData("*.acme.io", "https://evilacme.io/", false)]
    [InlineData("*.acme.io", "https://acme.io.evil.com/", false)]
    [InlineData("*.acme.io", "https://evil.com/?q=x.acme.io", false)]
    [InlineData("github.com/acme/*", "https://github.com/acme/repo", true)]
    [InlineData("github.com/acme/*", "https://github.com/other/repo", false)]
    [InlineData("docs.*.com", "https://docs.example.com/", true)]
    public void Wildcard(string pattern, string url, bool expected) =>
        Assert.Equal(expected, Matches(MatchKind.Wildcard, pattern, url));

    [Theory]
    [InlineData("github.com/acme", "https://github.com/acme", true)]
    [InlineData("github.com/acme", "https://github.com/acme/repo/pulls", true)]
    [InlineData("github.com/acme", "https://github.com/acmecorp", false)]
    [InlineData("https://github.com/acme/", "https://github.com/ACME/x", true)]
    public void PathPrefix(string pattern, string url, bool expected) =>
        Assert.Equal(expected, Matches(MatchKind.PathPrefix, pattern, url));

    [Theory]
    [InlineData("youtube.com", "https://youtube.com/watch", true)]
    [InlineData("youtube.com", "https://www.youtube.com/watch", false)]
    [InlineData("https://YouTube.com/", "https://youtube.com/", true)]
    public void Domain(string pattern, string url, bool expected) =>
        Assert.Equal(expected, Matches(MatchKind.Domain, pattern, url));

    [Fact]
    public void Regex_matches_full_url() =>
        Assert.True(Matches(MatchKind.Regex, @"^https://([a-z]+\.)*bank\.example/", "https://login.bank.example/x"));

    [Theory]
    [InlineData(@"(?=a)b")]   // lookahead
    [InlineData(@"(a)\1")]    // backreference
    [InlineData(@"(?>a+)")]   // atomic group
    public void Regex_rejects_backtracking_constructs(string pattern) =>
        Assert.False(PatternCompiler.Compile(MatchKind.Regex, pattern).Success);

    [Theory]
    [InlineData("^https://(unclosed", "a ( is never closed (at character 18)")]
    [InlineData("a)b", "there's a ) with no ( before it")]
    [InlineData("[abc", "a [ is never closed")]
    [InlineData("*.example", "has nothing before it to repeat")]
    public void Regex_errors_are_plain_language(string pattern, string expected)
    {
        var error = PatternCompiler.Compile(MatchKind.Regex, pattern).Error!;
        Assert.StartsWith("Not a valid regular expression", error);
        Assert.Contains(expected, error);
        Assert.DoesNotContain("Invalid pattern", error); // .NET's own wording stays out of the UI
    }

    [Fact]
    public void Regex_classic_redos_pattern_runs_in_linear_time()
    {
        var compiled = PatternCompiler.Compile(MatchKind.Regex, "^(a+)+$");
        Assert.True(compiled.Success);
        var url = new Uri("https://x.example/" + new string('a', 5000) + "!");
        var sw = Stopwatch.StartNew();
        Assert.False(compiled.Pattern!.IsMatch(url));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData(MatchKind.Domain, "*.acme.io")]
    [InlineData(MatchKind.PathPrefix, "github.com")]
    [InlineData(MatchKind.Wildcard, "")]
    [InlineData(MatchKind.Regex, "(unclosed")]
    public void Invalid_patterns_report_errors(MatchKind kind, string pattern)
    {
        var result = PatternCompiler.Compile(kind, pattern);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Overlong_pattern_is_rejected() =>
        Assert.False(PatternCompiler.Compile(MatchKind.Regex, new string('a', PatternCompiler.MaxPatternLength + 1)).Success);

    [Theory]
    [InlineData("https://jira.acme.io/x", "*.acme.io")]
    [InlineData("https://acme.io/x", "acme.io")]
    [InlineData("https://www.bbc.co.uk/news", "*.bbc.co.uk")]
    [InlineData("http://127.0.0.1:8080/", "127.0.0.1")]
    public void Suggests_wildcards(string url, string expected) =>
        Assert.Equal(expected, PatternCompiler.SuggestWildcard(new Uri(url)));
}

public class RuleEngineTests
{
    private sealed class FakeBoot(string id) : ISystemBoot
    {
        public string CurrentBootId => id;
        public bool IsCurrentBoot(string bootId) => bootId == id;
    }

    [Fact]
    public void First_active_match_wins_and_reports_trace()
    {
        var engine = new RuleEngine(new FakeBoot("b1"));
        var rules = new List<Rule>
        {
            new() { Conditions = [RuleCondition.Link(MatchKind.Domain, "other.com")] },
            new() { Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.acme.io")], Enabled = false },
            new() { Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.acme.io")] },
            new() { Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")] },
        };

        var result = engine.Match(new Uri("https://jira.acme.io"), rules);

        Assert.Equal(2, result.RuleIndex);
        Assert.Equal(
            [RuleEvaluationState.NoMatch, RuleEvaluationState.Inactive, RuleEvaluationState.Matched, RuleEvaluationState.NotReached],
            result.Evaluations.Select(e => e.State));
    }

    [Fact]
    public void Expired_and_previous_boot_rules_are_skipped()
    {
        var engine = new RuleEngine(new FakeBoot("now"));
        var rules = new List<Rule>
        {
            new() { Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new() { Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*")], ExpiresAfterBootId = "earlier-boot" },
        };
        Assert.False(engine.Match(new Uri("https://a.com"), rules).IsMatch);
    }
}

public class UrlValidatorTests
{
    [Theory]
    [InlineData("https://example.com/a?b=c")]
    [InlineData("http://localhost:3000")]
    [InlineData("  https://example.com  ")]
    public void Accepts_http_urls(string url) => Assert.True(UrlValidator.Validate(url).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("--remote-debugging-port=9222")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://example.com/\nfoo")]
    [InlineData("example.com")]
    public void Rejects_everything_else(string? url) => Assert.False(UrlValidator.Validate(url).IsValid);
}

public class LauncherTests
{
    [Fact]
    public void Url_replaces_placeholder_inside_quoted_args()
    {
        var args = BrowserLauncher.BuildArguments("--profile-directory=\"Profile 1\" --new-window {url}", new Uri("https://a.com/x"));
        Assert.Equal(["--profile-directory=Profile 1", "--new-window", "https://a.com/x"], args);
    }

    [Fact]
    public void Url_is_appended_without_placeholder() =>
        Assert.Equal(["--private-window", "https://a.com/"], BrowserLauncher.BuildArguments("--private-window", new Uri("https://a.com")));
}
