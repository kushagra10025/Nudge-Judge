using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Config;

/// <summary>
/// First-run sample configuration, so the UI has something to show before the first browser scan.
/// Browsers are marked <see cref="BrowserSource.Sample"/> and rules <see cref="Rule.IsSample"/>;
/// <see cref="RemoveSamples"/> clears them once real browsers are found.
/// </summary>
public static class SampleData
{
    private static readonly (string Name, MatchKind Kind, string Pattern)[] RuleTemplates =
    [
        ("Work tracker", MatchKind.Wildcard, "*.atlassian.net"),
        ("Company repos", MatchKind.PathPrefix, "github.com/acme"),
        ("Banking", MatchKind.Regex, @"^https://([a-z0-9-]+\.)*mybank\.example/"),
        ("Video", MatchKind.Domain, "www.youtube.com"),
    ];

    public static AppConfig Create()
    {
        var win = OperatingSystem.IsWindows();
        var firefox = new BrowserProfile
        {
            Name = "Firefox",
            ExecutablePath = win ? @"C:\Program Files\Mozilla Firefox\firefox.exe" : "firefox",
            Source = BrowserSource.Sample,
        };
        var firefoxPrivate = new BrowserProfile
        {
            Name = "Firefox Private",
            ExecutablePath = firefox.ExecutablePath,
            Arguments = "-private-window {url}",
            IsPrivate = true,
            Source = BrowserSource.Sample,
        };
        var chrome = new BrowserProfile
        {
            Name = "Chrome",
            ExecutablePath = win ? @"C:\Program Files\Google\Chrome\Application\chrome.exe" : "google-chrome",
            Source = BrowserSource.Sample,
        };
        var chromeWork = new BrowserProfile
        {
            Name = "Chrome Work",
            ExecutablePath = chrome.ExecutablePath,
            Arguments = "--profile-directory=\"Profile 1\" {url}",
            ProfileLabel = "Profile 1",
            Source = BrowserSource.Sample,
        };

        Guid[] targets = [chromeWork.Id, chromeWork.Id, firefoxPrivate.Id, firefox.Id];
        return new AppConfig
        {
            Settings = new AppSettings { PrimaryBrowserId = firefox.Id },
            Browsers = [firefox, chrome, chromeWork, firefoxPrivate],
            Rules = RuleTemplates.Select((t, i) => new Rule
            {
                Name = t.Name,
                MatchKind = t.Kind,
                Pattern = t.Pattern,
                BrowserId = targets[i],
                IsSample = true,
            }).ToList(),
        };
    }

    /// <summary>
    /// Deletes the sample browsers and the sample rules. Sample rules the user has saved since are kept:
    /// they move to the closest real browser (same program, same private mode), or are left pointing at
    /// the removed browser so the Rules page flags them. Returns true when anything was removed.
    /// </summary>
    public static bool RemoveSamples(AppConfig config)
    {
        var samples = config.Browsers.Where(b => b.Source == BrowserSource.Sample).ToList();
        var sampleIds = samples.Select(b => b.Id).ToHashSet();
        var removedRules = config.Rules.RemoveAll(r => IsSampleRule(r, sampleIds));
        if (samples.Count == 0) return removedRules > 0;

        var real = config.Browsers.Where(b => b.Source != BrowserSource.Sample).ToList();
        var replacements = samples.ToDictionary(s => s.Id, s => ClosestReal(s, real)?.Id);

        foreach (var rule in config.Rules)
        {
            if (replacements.TryGetValue(rule.BrowserId, out var to) && to is { } id)
                rule.BrowserId = id;
        }

        if (config.Settings.PrimaryBrowserId is { } primary && sampleIds.Contains(primary))
        {
            config.Settings.PrimaryBrowserId = replacements[primary]
                ?? real.FirstOrDefault(b => !b.IsPrivate)?.Id
                ?? real.FirstOrDefault()?.Id;
        }

        config.Browsers.RemoveAll(b => sampleIds.Contains(b.Id));
        return true;
    }

    /// <summary>
    /// Flagged sample, or (for configs written before the flag existed) an unchanged copy of a
    /// template that still targets a sample browser.
    /// </summary>
    private static bool IsSampleRule(Rule rule, HashSet<Guid> sampleBrowserIds) =>
        rule.IsSample ??
        (sampleBrowserIds.Contains(rule.BrowserId) &&
         RuleTemplates.Any(t => t.Name == rule.Name && t.Kind == rule.MatchKind && t.Pattern == rule.Pattern));

    private static BrowserProfile? ClosestReal(BrowserProfile sample, List<BrowserProfile> real)
    {
        // Never swap a private sample for a normal window or the reverse: that's a privacy change.
        var candidates = real.Where(b => b.IsPrivate == sample.IsPrivate && SameProgram(b, sample)).ToList();
        return candidates.FirstOrDefault(b => string.Equals(b.ProfileLabel, sample.ProfileLabel, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault(b => b.ProfileLabel is null)
               ?? candidates.FirstOrDefault();
    }

    private static bool SameProgram(BrowserProfile a, BrowserProfile b) =>
        BrowserFamily.Of(a.ExecutablePath) is { } fa
            ? fa == BrowserFamily.Of(b.ExecutablePath)
            : BrowserFamily.ExecutableName(a.ExecutablePath) == BrowserFamily.ExecutableName(b.ExecutablePath);
}
