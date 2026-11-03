using System.ComponentModel;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;
using NudgeJudge.Core.Security;

namespace NudgeJudge.Core.Routing;

public enum RouteKind { Launched, NeedsChoice, Rejected, Failed }

public sealed record RouteDecision(RouteKind Kind, Uri? Url, string? Message = null);

/// <summary>
/// The single entry point for incoming links (command line today, IPC later).
/// Validate, then pass through, match or ask.
/// </summary>
public sealed class LinkRouter(
    IConfigStore config,
    RuleEngine engine,
    IBrowserLauncher launcher,
    ActivityLog activity,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Raised (on the caller's thread) when no rule matches and the user must choose.</summary>
    public event EventHandler<Uri>? ChoiceRequested;

    public RouteDecision Route(string? rawUrl)
    {
        var validation = UrlValidator.Validate(rawUrl);
        if (!validation.IsValid)
        {
            Log(Truncate(rawUrl), ActivityOutcome.Rejected, detail: validation.Error);
            return new RouteDecision(RouteKind.Rejected, null, validation.Error);
        }

        var url = validation.Url!;
        var cfg = config.Current;

        if (!cfg.Settings.RoutingEnabled)
        {
            var primary = cfg.FindBrowser(cfg.Settings.PrimaryBrowserId);
            if (primary is not null)
                return Launch(primary, url, ActivityOutcome.PassedThrough);
            // No primary browser configured: fall through to asking rather than dropping the link.
        }
        else
        {
            var result = engine.Match(url, cfg.Rules);
            if (result.Rule is { } rule)
            {
                var browser = cfg.FindBrowser(rule.BrowserId);
                if (browser is not null)
                    return Launch(browser, url, ActivityOutcome.Routed, result.RuleIndex + 1, rule.Pattern);
                Log(url.AbsoluteUri, ActivityOutcome.Failed, ruleNumber: result.RuleIndex + 1, pattern: rule.Pattern,
                    detail: "The rule's browser no longer exists. Asking instead.");
            }
        }

        Log(url.AbsoluteUri, ActivityOutcome.Asked);
        ChoiceRequested?.Invoke(this, url);
        return new RouteDecision(RouteKind.NeedsChoice, url);
    }

    /// <summary>Opens a URL the user explicitly chose a browser for (chooser window).</summary>
    public RouteDecision OpenChosen(Uri url, BrowserProfile browser) => Launch(browser, url, ActivityOutcome.Chosen);

    /// <summary>Creates a rule from a chooser decision. <paramref name="lifetime"/> null means permanent.</summary>
    public Rule AddRule(MatchKind type, string pattern, Guid browserId, TimeSpan? lifetime, bool untilReboot, ISystemBoot boot)
    {
        var rule = new Rule
        {
            MatchKind = type,
            Pattern = pattern.Trim(),
            BrowserId = browserId,
            CreatedAt = _time.GetUtcNow(),
            ExpiresAt = lifetime is { } l ? _time.GetUtcNow() + l : null,
            ExpiresAfterBootId = untilReboot ? boot.CurrentBootId : null,
        };
        // Temporary rules go first so they can override permanent ones while they live.
        config.Update(c =>
        {
            if (rule.IsTemporary) c.Rules.Insert(0, rule);
            else c.Rules.Add(rule);
        });
        return rule;
    }

    private RouteDecision Launch(BrowserProfile browser, Uri url, ActivityOutcome outcome, int? ruleNumber = null, string? pattern = null)
    {
        try
        {
            launcher.Launch(browser, url);
            Log(url.AbsoluteUri, outcome, browser.Name, ruleNumber, pattern, browserId: browser.Id);
            return new RouteDecision(RouteKind.Launched, url);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            var message = $"Couldn't start {browser.Name}: {ex.Message}";
            Log(url.AbsoluteUri, ActivityOutcome.Failed, browser.Name, ruleNumber, pattern, message, browser.Id);
            return new RouteDecision(RouteKind.Failed, url, message);
        }
    }

    private void Log(string url, ActivityOutcome outcome, string? browser = null, int? ruleNumber = null, string? pattern = null, string? detail = null,
        Guid? browserId = null) =>
        activity.Add(new ActivityEntry(_time.GetUtcNow(), url, outcome, browser, ruleNumber, pattern, detail, browserId));

    private static string Truncate(string? s) =>
        s is null ? "(empty)" : s.Length <= 200 ? s : s[..200] + "…";
}
