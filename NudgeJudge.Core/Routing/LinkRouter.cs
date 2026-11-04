using System.ComponentModel;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;
using NudgeJudge.Core.Security;

namespace NudgeJudge.Core.Routing;

public enum RouteKind { Launched, NeedsChoice, Rejected, Failed }

public sealed record RouteDecision(RouteKind Kind, Uri? Url, string? Message = null);

/// <summary>A link waiting for the user to choose, and the app it came from.</summary>
public sealed record IncomingLink(Uri Url, SourceApp? Source = null);

/// <summary>
/// The single entry point for incoming links (this launch's command line, or one forwarded by a later launch).
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
    public event EventHandler<IncomingLink>? ChoiceRequested;

    /// <param name="source">The app that opened the link, when known. Rules can match on it.</param>
    public RouteDecision Route(string? rawUrl, SourceApp? source = null)
    {
        source = SourceApp.Sanitize(source);
        var validation = UrlValidator.Validate(rawUrl);
        if (!validation.IsValid)
        {
            Log(new(Now, Truncate(rawUrl), ActivityOutcome.Rejected, Detail: validation.Error, Source: source));
            return new RouteDecision(RouteKind.Rejected, null, validation.Error);
        }

        var url = validation.Url!;
        var cfg = config.Current;

        if (!cfg.Settings.RoutingEnabled)
        {
            var primary = cfg.FindBrowser(cfg.Settings.PrimaryBrowserId);
            // No primary browser, or it wouldn't start: fall through to asking rather than dropping the link.
            if (primary is not null && Launch(primary, url, source, ActivityOutcome.PassedThrough) is { Kind: not RouteKind.Failed } passed)
                return passed;
        }
        else
        {
            var result = engine.Match(url, cfg.Rules, source);
            if (result.Rule is { } rule)
            {
                var browser = cfg.FindBrowser(rule.BrowserId);
                if (browser is null)
                    Log(new(Now, url.AbsoluteUri, ActivityOutcome.Failed, RuleNumber: result.RuleIndex + 1, RuleName: rule.Name,
                        Detail: "The rule's browser no longer exists. Asking instead.", Source: source));
                else if (Launch(browser, url, source, ActivityOutcome.Routed, result.RuleIndex + 1, rule.Name) is { Kind: not RouteKind.Failed } routed)
                    return routed;
            }
        }

        Log(new(Now, url.AbsoluteUri, ActivityOutcome.Asked, Source: source));
        ChoiceRequested?.Invoke(this, new IncomingLink(url, source));
        return new RouteDecision(RouteKind.NeedsChoice, url);
    }

    /// <summary>Opens a URL the user explicitly chose a browser for (chooser window).</summary>
    public RouteDecision OpenChosen(Uri url, BrowserProfile browser, SourceApp? source = null) =>
        Launch(browser, url, SourceApp.Sanitize(source), ActivityOutcome.Chosen);

    /// <summary>
    /// Saves a rule made from a chooser decision. <paramref name="lifetime"/> null means permanent.
    /// Temporary rules go first so they can override permanent ones while they live.
    /// </summary>
    public Rule AddRule(Rule rule, TimeSpan? lifetime, bool untilReboot, ISystemBoot boot)
    {
        var now = _time.GetUtcNow();
        rule.CreatedAt = now;
        rule.ExpiresAt = lifetime is { } l ? now + l : null;
        rule.ExpiresAfterBootId = untilReboot ? boot.CurrentBootId : null;
        config.Update(c =>
        {
            // Temporary rules pile up at the top otherwise; this is where they're made, so clean up here.
            c.Rules.RemoveAll(r => r.IsExpired(now, boot));
            if (rule.IsTemporary) c.Rules.Insert(0, rule);
            else c.Rules.Add(rule);
        });
        return rule;
    }

    private RouteDecision Launch(BrowserProfile browser, Uri url, SourceApp? source, ActivityOutcome outcome, int? ruleNumber = null,
        string? ruleName = null)
    {
        try
        {
            launcher.Launch(browser, url);
            Log(new(Now, url.AbsoluteUri, outcome, browser.Name, ruleNumber, BrowserId: browser.Id, RuleName: ruleName, Source: source));
            return new RouteDecision(RouteKind.Launched, url);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            var message = $"Couldn't start {browser.Name}: {ex.Message}";
            Log(new(Now, url.AbsoluteUri, ActivityOutcome.Failed, browser.Name, ruleNumber, Detail: message, BrowserId: browser.Id,
                RuleName: ruleName, Source: source));
            return new RouteDecision(RouteKind.Failed, url, message);
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Log(ActivityEntry entry) => activity.Add(entry);

    private static string Truncate(string? s) =>
        s is null ? "(empty)" : s.Length <= 200 ? s : s[..200] + "…";
}
