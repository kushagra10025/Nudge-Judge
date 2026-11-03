using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core.Tests;

/// <summary>Condition groups inside rules, up to <see cref="RuleCondition.MaxDepth"/> levels.</summary>
public class NestedGroupTests
{
    private sealed class Boot : ISystemBoot
    {
        public string CurrentBootId => "b";
        public bool IsCurrentBoot(string bootId) => bootId == "b";
    }

    private static readonly SourceApp Teams = new("ms-teams.exe", null, "Microsoft Teams");
    private static readonly SourceApp Slack = new("slack", null, "Slack");
    private static readonly SourceApp Mail = new("thunderbird", null, "Thunderbird");

    /// <summary>
    /// acme.io links AND (from Teams OR (from Slack AND under /docs)): Rule → group → group, three levels.
    /// </summary>
    private static Rule Acme() => new()
    {
        Name = "Acme",
        Join = RuleJoin.All,
        Conditions =
        [
            RuleCondition.Link(MatchKind.Wildcard, "*.acme.io"),
            RuleCondition.Group(RuleJoin.Any,
                RuleCondition.Source("ms-teams.exe"),
                RuleCondition.Group(RuleJoin.All,
                    RuleCondition.Source("slack"),
                    RuleCondition.Link(MatchKind.Wildcard, "*.acme.io/docs/*"))),
        ],
    };

    [Theory]
    [InlineData("https://app.acme.io/x", "teams", true)]
    [InlineData("https://app.acme.io/docs/a", "slack", true)]
    [InlineData("https://app.acme.io/x", "slack", false)] // Slack, but not under /docs
    [InlineData("https://app.acme.io/docs/a", "mail", false)]
    [InlineData("https://app.acme.io/docs/a", null, false)]
    [InlineData("https://example.com/docs/a", "teams", false)] // the top level still needs acme.io
    public void Groups_evaluate_with_their_own_join(string url, string? from, bool matches)
    {
        var source = from switch { "teams" => Teams, "slack" => Slack, "mail" => Mail, _ => null };
        var result = new RuleEngine(new Boot()).Match(new Uri(url), [Acme()], source);
        Assert.Equal(matches, result.IsMatch);
    }

    [Fact]
    public void Errors_name_the_condition_by_its_outline_number()
    {
        var rule = Acme();
        rule.Conditions[1].Conditions![1].Conditions![1] = RuleCondition.Link(MatchKind.Regex, "(?<=x)a");
        var compiled = new RuleEngine(new Boot()).Compile(rule);
        Assert.False(compiled.Success);
        Assert.StartsWith("Condition 2.2.2: ", compiled.Error);

        // The per-value error lands on the right field, three levels down.
        var deep = compiled.Conditions[1].Children![1].Children![1];
        Assert.NotNull(deep.ValueErrors[0]);
        Assert.True(compiled.Conditions[0].Success);
    }

    [Fact]
    public void Empty_groups_and_groups_past_the_limit_do_not_compile()
    {
        var engine = new RuleEngine(new Boot());

        var empty = new Rule { Name = "e", Conditions = [RuleCondition.Link(MatchKind.Domain, "a.com"), RuleCondition.Group(RuleJoin.Any)] };
        Assert.Equal("Condition 2: Add a condition to this group.", engine.Compile(empty).Error);

        var tooDeep = new Rule
        {
            Name = "d",
            Conditions = [RuleCondition.Group(RuleJoin.Any, RuleCondition.Group(RuleJoin.All, RuleCondition.Group(RuleJoin.Any, RuleCondition.Link(MatchKind.Domain, "a.com"))))],
        };
        var result = engine.Compile(tooDeep);
        Assert.False(result.Success);
        Assert.Contains("two levels deep", result.Error);
    }

    [Fact]
    public void Groups_round_trip_through_the_config_file_and_old_rules_are_untouched()
    {
        var dir = Directory.CreateTempSubdirectory("nj-groups-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new JsonConfigStore(path);
            var flat = new Rule { Name = "Flat", Conditions = [RuleCondition.Link(MatchKind.Domain, "a.com")] };
            store.Update(c => c.Rules.AddRange([Acme(), flat]));

            var json = File.ReadAllText(path);
            Assert.Contains("\"field\": \"Group\"", json);

            var reread = new JsonConfigStore(path).Current.Rules;
            var inner = reread[0].Conditions[1];
            Assert.True(inner.IsGroup);
            Assert.Equal(RuleJoin.Any, inner.Join);
            Assert.Equal(RuleJoin.All, inner.Conditions![1].Join);
            Assert.Equal(["*.acme.io/docs/*"], inner.Conditions[1].Conditions![1].Values);

            // A plain condition carries no group fields.
            Assert.Null(reread[1].Conditions[0].Conditions);
            Assert.True(new RuleEngine(new Boot()).Match(new Uri("https://app.acme.io/docs/a"), reread, Slack).IsMatch);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clones_are_deep_and_leaves_reach_inside_groups()
    {
        var rule = Acme();
        var copy = rule.Clone();
        copy.Conditions[1].Conditions![0].Values[0] = "changed";
        Assert.Equal("ms-teams.exe", rule.Conditions[1].Conditions![0].Values[0]);

        Assert.Equal(4, rule.Leaves().Count());
        Assert.Equal(2, rule.Leaves().Count(c => c.Field == ConditionField.SourceApp));
    }
}
