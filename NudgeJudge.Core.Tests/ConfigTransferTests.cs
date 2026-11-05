using System.Text;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Tests;

public class ConfigTransferTests
{
    private static AppConfig Sample()
    {
        var chrome = new BrowserProfile { Name = "Chrome", ExecutablePath = "/usr/bin/chrome", Arguments = "--profile-directory=Work" };
        var firefox = new BrowserProfile { Name = "Firefox", ExecutablePath = "/usr/bin/firefox" };
        var config = new AppConfig { Browsers = [chrome, firefox] };
        config.Rules.Add(new Rule
        {
            Name = "GitHub at work",
            Conditions = [RuleCondition.Link(MatchKind.Domain, "github.com"), RuleCondition.Time("Mon-Fri 09:00-17:00")],
            BrowserId = chrome.Id,
        });
        config.Rules.Add(new Rule { Name = "GitHub", Conditions = [RuleCondition.Link(MatchKind.Domain, "github.com")], BrowserId = firefox.Id });
        config.Rules.Add(new Rule
        {
            Name = "Temporary",
            Conditions = [RuleCondition.Link(MatchKind.Domain, "example.com")],
            BrowserId = firefox.Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        return config;
    }

    [Fact]
    public void Export_then_import_into_an_empty_config_copies_rules_and_browsers()
    {
        var source = Sample();
        var export = ConfigTransfer.Read(ConfigTransfer.Export(source));

        var target = new AppConfig();
        target.Settings.OfferSamples = true;
        var result = ConfigTransfer.Apply(target, export);

        Assert.Equal(new ImportResult(2, 0, 2, 0), result);
        Assert.Equal(["GitHub at work", "GitHub"], target.Rules.Select(r => r.Name)); // temporary rule left out
        Assert.Equal(source.Browsers.Select(b => (b.Id, b.Name, b.Arguments)), target.Browsers.Select(b => (b.Id, b.Name, b.Arguments)));
        Assert.True(target.Rules[0].RoutesLike(source.Rules[0]));
        Assert.False(target.Settings.OfferSamples);
        Assert.Equal("Imported 2 rules and 2 browsers.", result.Describe());
    }

    [Fact]
    public void Importing_a_backup_replaces_matching_ids_and_keeps_the_rest()
    {
        var config = Sample();
        var backup = ConfigTransfer.Read(ConfigTransfer.Export(config));

        config.Rules[0].Name = "Renamed since";
        config.Browsers[1].Name = "Firefox Nightly";
        config.Rules.Add(new Rule { Name = "Added since", Conditions = [RuleCondition.Link(MatchKind.Domain, "a.io")] });

        var result = ConfigTransfer.Apply(config, backup);

        Assert.Equal(new ImportResult(0, 2, 0, 2), result);
        Assert.Equal(["GitHub at work", "GitHub", "Temporary", "Added since"], config.Rules.Select(r => r.Name));
        Assert.Equal("Firefox", config.Browsers[1].Name);
        Assert.Equal("Imported 2 rules (2 replaced) and 2 browsers (2 replaced).", result.Describe());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"format":"something-else","rules":[]}""")]
    [InlineData("""{"format":"nudgejudge-export","version":999}""")]
    public void Files_that_arent_exports_are_refused(string text) =>
        Assert.Throws<InvalidDataException>(() => ConfigTransfer.Read(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Old_single_pattern_rules_are_upgraded_on_import()
    {
        var json = """{"format":"nudgejudge-export","version":1,"rules":[{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","pattern":"*.acme.io","matchKind":"Wildcard"}]}""";
        var rule = Assert.Single(ConfigTransfer.Read(Encoding.UTF8.GetBytes(json)).Rules);
        Assert.Equal("*.acme.io", rule.Name);
        Assert.Equal(["*.acme.io"], Assert.Single(rule.Conditions).Values);
    }
}
