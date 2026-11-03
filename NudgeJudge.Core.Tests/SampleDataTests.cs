using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Tests;

public class SampleDataTests
{
    private static BrowserProfile Real(string name, string exe, string args = "", bool isPrivate = false) =>
        new() { Name = name, ExecutablePath = exe, Arguments = args, IsPrivate = isPrivate, Source = BrowserSource.Discovered };

    private static BrowserProfile Sample(AppConfig c, string name) => c.Browsers.Single(b => b.Name == name);

    [Fact]
    public void Fresh_samples_are_all_flagged()
    {
        var c = SampleData.Create();
        Assert.All(c.Browsers, b => Assert.Equal(BrowserSource.Sample, b.Source));
        Assert.All(c.Rules, r => Assert.True(r.IsSample));
    }

    [Fact]
    public void Removes_sample_browsers_and_rules_and_moves_primary()
    {
        var c = SampleData.Create();
        var firefox = Real("Firefox", "/usr/bin/firefox");
        var priv = Real("Firefox Private", "/usr/bin/firefox", "-private-window", isPrivate: true);
        c.Browsers.AddRange([priv, firefox]);

        Assert.True(SampleData.RemoveSamples(c));

        Assert.Equal([priv, firefox], c.Browsers);
        Assert.Empty(c.Rules);
        Assert.Equal(firefox.Id, c.Settings.PrimaryBrowserId); // same program as the sample primary, not private
    }

    [Fact]
    public void Keeps_user_rules_and_saved_samples_retargeting_by_program_and_privacy()
    {
        var c = SampleData.Create();
        var banking = c.Rules.Single(r => r.Name == "Banking");
        banking.IsSample = false; // the user saved it in the editor
        var video = c.Rules.Single(r => r.Name == "Video");
        video.IsSample = false;
        var work = c.Rules.Single(r => r.Name == "Work tracker");
        work.IsSample = false;
        var own = new Rule { Name = "Mine", Pattern = "example.org", BrowserId = Sample(c, "Chrome").Id };
        c.Rules.Add(own);

        var firefox = Real("Firefox", "firefox");
        var priv = Real("Firefox Private", "firefox", "-private-window", isPrivate: true);
        var chrome = Real("Google Chrome", "/usr/bin/google-chrome-stable");
        c.Browsers.AddRange([firefox, priv, chrome]);
        var chromeWorkId = Sample(c, "Chrome Work").Id;

        SampleData.RemoveSamples(c);

        // "Company repos" was still an untouched sample, so it's gone.
        Assert.Equal(["Banking", "Mine", "Video", "Work tracker"], c.Rules.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(priv.Id, banking.BrowserId);       // private stays private
        Assert.Equal(firefox.Id, video.BrowserId);
        Assert.Equal(chrome.Id, own.BrowserId);         // google-chrome and google-chrome-stable are the same family
        Assert.Equal(chrome.Id, work.BrowserId);        // no "Profile 1" found: falls back to the plain entry
        Assert.NotEqual(chromeWorkId, work.BrowserId);
    }

    [Fact]
    public void Private_sample_is_never_moved_to_a_normal_window()
    {
        var c = SampleData.Create();
        var banking = c.Rules.Single(r => r.Name == "Banking");
        banking.IsSample = false;
        var sampleId = banking.BrowserId;
        c.Browsers.Add(Real("Firefox", "firefox"));

        SampleData.RemoveSamples(c);

        Assert.Equal(sampleId, banking.BrowserId); // left dangling so the Rules page flags it
        Assert.Null(c.FindBrowser(banking.BrowserId));
    }

    [Fact]
    public void Legacy_configs_without_flag_still_lose_untouched_sample_rules()
    {
        var c = SampleData.Create();
        foreach (var r in c.Rules) r.IsSample = null; // written before IsSample existed
        c.Rules[0].Pattern = "*.mycompany.net";       // the user changed this one
        c.Browsers.Add(Real("Firefox", "firefox"));

        SampleData.RemoveSamples(c);

        Assert.Equal("Work tracker", Assert.Single(c.Rules).Name);
    }

    [Fact]
    public void Saved_but_unchanged_sample_rule_is_kept()
    {
        var c = SampleData.Create();
        var video = c.Rules.Single(r => r.Name == "Video");
        video.IsSample = false; // saved in the editor without changing anything
        var firefox = Real("Firefox", "firefox");
        c.Browsers.Add(firefox);

        SampleData.RemoveSamples(c);

        Assert.Same(video, Assert.Single(c.Rules));
        Assert.Equal(firefox.Id, video.BrowserId);
    }

    [Fact]
    public void Nothing_to_remove_returns_false()
    {
        var c = new AppConfig { Browsers = [Real("Firefox", "firefox")] };
        Assert.False(SampleData.RemoveSamples(c));
        Assert.Single(c.Browsers);
    }
}
