using System.Text;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Tests;

public class ConfigTransferTests
{
    /// <summary>Every program "exists": tests decide what's missing explicitly.</summary>
    private static readonly Func<string, bool> AllExist = _ => true;

    private static AppConfig Sample()
    {
        var chrome = new BrowserProfile { Name = "Chrome", ExecutablePath = "/usr/bin/google-chrome", Arguments = "--profile-directory=Work" };
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

    private static ConfigExport RoundTrip(AppConfig config) => ConfigTransfer.Read(ConfigTransfer.Export(config));

    private static ConfigExport Read(string json) => ConfigTransfer.Read(Encoding.UTF8.GetBytes(json));

    private static ImportResult Import(AppConfig into, ConfigExport export, ImportMode mode, Action<ImportPlan>? choose = null)
    {
        var plan = ImportPlan.Create(into, export, mode, AllExist);
        choose?.Invoke(plan);
        return plan.Apply(into);
    }

    // ---- Basic round trips ----

    [Fact]
    public void Sharing_into_an_empty_setup_adds_everything_with_new_ids()
    {
        var source = Sample();
        var target = new AppConfig();
        target.Settings.OfferSamples = true;

        var result = Import(target, RoundTrip(source), ImportMode.AddAsNew);

        Assert.Equal(new ImportResult(2, 0, 0, 2, 0, 0), result);
        Assert.Equal(["GitHub at work", "GitHub"], target.Rules.Select(r => r.Name)); // temporary rule left out
        Assert.Equal(["Chrome", "Firefox"], target.Browsers.Select(b => b.Name));
        Assert.DoesNotContain(target.Browsers, b => source.Browsers.Any(s => s.Id == b.Id));
        Assert.DoesNotContain(target.Rules, r => source.Rules.Any(s => s.Id == r.Id));
        // Rules follow their browsers to the new ids.
        Assert.Equal(target.Browsers[0].Id, target.Rules[0].BrowserId);
        Assert.Equal(target.Browsers[1].Id, target.Rules[1].BrowserId);
        Assert.False(target.Settings.OfferSamples);
        Assert.Equal("Imported 2 rules and 2 browsers.", result.Describe());
    }

    [Fact]
    public void Restoring_a_backup_replaces_matching_ids_and_keeps_the_rest()
    {
        var config = Sample();
        var backup = RoundTrip(config);
        config.Rules[0].Name = "Renamed since";
        config.Browsers[1].Name = "Firefox Nightly";
        config.Rules.Add(new Rule { Name = "Added since", Conditions = [RuleCondition.Link(MatchKind.Domain, "a.io")] });

        Assert.Equal(ImportMode.Restore, ImportPlan.SuggestMode(config, backup));
        var result = Import(config, backup, ImportMode.Restore);

        Assert.Equal(new ImportResult(0, 2, 0, 0, 2, 0), result);
        Assert.Equal(["GitHub at work", "GitHub", "Temporary", "Added since"], config.Rules.Select(r => r.Name));
        Assert.Equal("Firefox", config.Browsers[1].Name);
    }

    [Fact]
    public void Someone_elses_file_suggests_adding_as_new()
    {
        Assert.Equal(ImportMode.AddAsNew, ImportPlan.SuggestMode(Sample(), RoundTrip(Sample())));
    }

    // ---- Browser takeover ----

    /// <summary>Your export, edited to keep Chrome's id but run something else: must not change your Chrome silently.</summary>
    private static (AppConfig Mine, ConfigExport Tampered) Tampered()
    {
        var mine = Sample();
        var tampered = RoundTrip(mine);
        tampered.Browsers[0].ExecutablePath = "/tmp/evil";
        tampered.Browsers[0].Arguments = "";
        return (mine, tampered);
    }

    [Fact]
    public void A_changed_program_behind_one_of_your_browsers_is_added_separately_unless_allowed()
    {
        var (mine, tampered) = Tampered();
        var chromeId = mine.Browsers[0].Id;
        var plan = ImportPlan.Create(mine, tampered, ImportMode.Restore, AllExist);

        var chrome = plan.Browsers[0];
        Assert.True(chrome.ChangesProgram);
        Assert.Equal(BrowserImportAction.KeepSeparate, chrome.Action);
        Assert.Contains(BrowserWarning.Unrecognised, chrome.Assessment.Warnings);

        var result = plan.Apply(mine);

        // Yours is untouched; the import's version sits beside it, needing review, and its rule points there.
        Assert.Equal("/usr/bin/google-chrome", mine.FindBrowser(chromeId)!.ExecutablePath);
        var separate = Assert.Single(mine.Browsers, b => b.ExecutablePath == "/tmp/evil");
        Assert.NotEqual(chromeId, separate.Id);
        Assert.Equal("Chrome (imported)", separate.Name);
        Assert.True(separate.NeedsReview);
        Assert.Equal(separate.Id, mine.Rules[0].BrowserId);
        Assert.Equal(1, result.NeedReview);
    }

    [Fact]
    public void Allowing_the_change_replaces_the_program_but_still_needs_review_when_suspicious()
    {
        var (mine, tampered) = Tampered();
        var chromeId = mine.Browsers[0].Id;

        Import(mine, tampered, ImportMode.Restore, plan => plan.Browsers[0].AllowProgramChange = true);

        var chrome = mine.FindBrowser(chromeId)!;
        Assert.Equal("/tmp/evil", chrome.ExecutablePath);
        Assert.True(chrome.NeedsReview);
        Assert.Equal(2, mine.Browsers.Count);
    }

    [Fact]
    public void Adding_as_new_never_touches_your_browsers_even_with_their_ids()
    {
        var (mine, tampered) = Tampered();
        var before = mine.Browsers.Select(b => (b.Id, b.ExecutablePath)).ToList();

        Import(mine, tampered, ImportMode.AddAsNew);

        Assert.Equal(before, mine.Browsers.Take(2).Select(b => (b.Id, b.ExecutablePath)));
        // The untampered Firefox runs exactly what yours does, so it's reused rather than duplicated.
        Assert.Equal(3, mine.Browsers.Count);
        Assert.Equal(4, mine.Rules.Count(r => !r.IsTemporary));
    }

    [Fact]
    public void An_identical_browser_reuses_yours()
    {
        var mine = Sample();
        var plan = ImportPlan.Create(mine, RoundTrip(Sample()), ImportMode.AddAsNew, AllExist);
        // Different ids, but Sample() makes the same programs: both are reused.
        Assert.All(plan.Browsers, b => Assert.Equal(BrowserImportAction.UseExisting, b.Action));
        var result = plan.Apply(mine);
        Assert.Equal(2, mine.Browsers.Count);
        Assert.Equal(2, result.BrowsersReused);
        Assert.Contains(mine.Rules, r => r.Name == "GitHub at work" && r.BrowserId == mine.Browsers[0].Id && r.Id != Sample().Rules[0].Id);
    }

    [Fact]
    public void A_restored_rule_that_now_opens_elsewhere_is_reported()
    {
        var mine = Sample();
        var backup = RoundTrip(mine);
        backup.Rules[1].BrowserId = backup.Browsers[0].Id; // GitHub → Chrome instead of Firefox

        var plan = ImportPlan.Create(mine, backup, ImportMode.Restore, AllExist);
        var to = plan.Destination(plan.Rules[1].Incoming.BrowserId, mine);

        Assert.Equal(mine.Browsers[0].Id, to!.Value.Id);
        Assert.NotEqual(plan.Rules[1].SameId!.BrowserId, to.Value.Id);
    }

    [Fact]
    public void Rules_pointing_outside_a_shared_file_get_no_browser()
    {
        var mine = Sample();
        var shared = RoundTrip(mine);
        shared.Browsers.Clear(); // rules refer to browsers the file doesn't carry

        Import(mine, shared, ImportMode.AddAsNew);

        Assert.All(mine.Rules.Skip(3), r => Assert.Equal(Guid.Empty, r.BrowserId));
    }

    [Fact]
    public void Review_flags_in_the_file_are_ignored_and_worked_out_again()
    {
        var json = """
            {"format":"nudgejudge-export","version":2,"browsers":[
              {"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","name":"Firefox","executablePath":"/usr/bin/firefox","needsReview":true}]}
            """;
        var target = new AppConfig();
        Import(target, Read(json), ImportMode.AddAsNew);
        Assert.False(Assert.Single(target.Browsers).NeedsReview);
    }

    // ---- What makes a browser suspicious ----

    [Theory]
    [InlineData("/usr/bin/firefox", "", true, new BrowserWarning[0])]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "--incognito", true, new BrowserWarning[0])]
    [InlineData("flatpak", "run org.mozilla.firefox", true, new BrowserWarning[0])]
    [InlineData("/usr/bin/firefox", "--profile /home/me/.mozilla/firefox/abc.default", true, new BrowserWarning[0])]
    [InlineData("/opt/thing/run", "", true, new[] { BrowserWarning.Unrecognised })]
    [InlineData("/home/me/open.sh", "", true, new[] { BrowserWarning.Script })]
    [InlineData(@"C:\x\open.bat", "", true, new[] { BrowserWarning.Script })]
    [InlineData("powershell", "-c whatever", true, new[] { BrowserWarning.Script })]
    [InlineData("/usr/bin/firefox", "", false, new[] { BrowserWarning.NotFound })]
    [InlineData("google-chrome", "--load-extension=/tmp/x {url}", true, new[] { BrowserWarning.RiskyArguments })]
    [InlineData("google-chrome", "--remote-debugging-port=9222", true, new[] { BrowserWarning.RiskyArguments })]
    [InlineData("chrome", "--disable-web-security --user-data-dir=/tmp/p", true, new[] { BrowserWarning.RiskyArguments })]
    public void Browsers_are_assessed(string exe, string args, bool exists, BrowserWarning[] expected)
    {
        var assessment = BrowserRisk.Assess(new BrowserProfile { Name = "x", ExecutablePath = exe, Arguments = args }, _ => exists);
        Assert.Equal(expected, assessment.Warnings);
    }

    [Fact]
    public void Risky_arguments_are_named()
    {
        var assessment = BrowserRisk.Assess(
            new BrowserProfile { ExecutablePath = "chrome", Arguments = "--incognito --load-extension=/tmp/x --no-sandbox" }, AllExist);
        Assert.Equal(["--load-extension=/tmp/x", "--no-sandbox"], assessment.RiskyArguments);
        Assert.Contains("Risky arguments: --load-extension=/tmp/x --no-sandbox", assessment.Describe());
    }

    [Fact]
    public void A_missing_program_needs_review_after_import()
    {
        var target = new AppConfig();
        var plan = ImportPlan.Create(target, RoundTrip(Sample()), ImportMode.AddAsNew, _ => false);
        Assert.All(plan.Browsers, b => Assert.True(b.WillNeedReview));
        Assert.Equal(2, plan.Apply(target).NeedReview);
        Assert.All(target.Browsers, b => Assert.True(b.NeedsReview));
    }

    // ---- Refused files and limits ----

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"format":"something-else","rules":[]}""")]
    [InlineData("""{"format":"nudgejudge-export","version":999}""")]
    [InlineData("""{"format":"nudgejudge-export","browsers":[{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","name":"x","executablePath":""}]}""")]
    [InlineData("""{"format":"nudgejudge-export","browsers":[{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","name":"x","executablePath":"/usr/bin/firefox","arguments":"a\nb"}]}""")]
    [InlineData("""{"format":"nudgejudge-export","browsers":[{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","name":"x","executablePath":"/usr/bin/fire\u0000fox"}]}""")]
    public void Files_that_arent_valid_exports_are_refused(string text) =>
        Assert.Throws<InvalidDataException>(() => Read(text));

    [Fact]
    public void Too_many_rules_are_refused()
    {
        var config = new AppConfig();
        for (var i = 0; i <= ConfigTransfer.MaxRules; i++)
            config.Rules.Add(new Rule { Name = $"r{i}", Conditions = [RuleCondition.Link(MatchKind.Domain, "a.io")] });
        var e = Assert.Throws<InvalidDataException>(() => RoundTrip(config));
        Assert.Contains("more than", e.Message);
    }

    [Fact]
    public void Too_many_alternatives_are_refused()
    {
        var config = new AppConfig();
        config.Rules.Add(new Rule
        {
            Name = "big",
            Conditions = [RuleCondition.Link(MatchKind.Domain, [.. Enumerable.Range(0, ConfigTransfer.MaxValuesPerCondition + 1).Select(i => $"a{i}.io")])],
        });
        Assert.Throws<InvalidDataException>(() => RoundTrip(config));
    }

    [Theory]
    [InlineData("Chrome\u202Eexe.txt", "Chromeexe.txt")] // right-to-left override
    [InlineData("\u2066Bank\u2069", "Bank")]
    [InlineData("Line\nbreak", "Linebreak")]
    [InlineData("   ", "fallback")]
    [InlineData("\u202E", "fallback")]
    public void Names_lose_characters_that_disguise_them(string name, string expected) =>
        Assert.Equal(expected, ConfigTransfer.CleanName(name, "fallback"));

    [Fact]
    public void Long_names_are_cut()
    {
        var name = ConfigTransfer.CleanName(new string('a', 1000), "x");
        Assert.Equal(ConfigTransfer.MaxNameLength + 1, name.Length);
        Assert.EndsWith("…", name);
    }

    [Fact]
    public void Old_single_pattern_rules_are_upgraded_on_import()
    {
        var json = """{"format":"nudgejudge-export","version":1,"rules":[{"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","pattern":"*.acme.io","matchKind":"Wildcard"}]}""";
        var rule = Assert.Single(Read(json).Rules);
        Assert.Equal("*.acme.io", rule.Name);
        Assert.Equal(["*.acme.io"], Assert.Single(rule.Conditions).Values);
    }
}
