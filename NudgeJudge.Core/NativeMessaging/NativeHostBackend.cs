using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core.NativeMessaging;

/// <summary>
/// The app's side of a native messaging session. It decides whether a link should simply open in the browser
/// that sent it ("stay"), and otherwise hands it to <paramref name="deliver"/>, which gets it to the running
/// instance or starts the app with it.
/// </summary>
/// <param name="caller">The browser that started this host (its process), when it could be told.</param>
/// <param name="deliver">Hands a link over with its source. Throws <see cref="IOException"/> when it can't.</param>
public sealed class NativeHostBackend(
    IConfigStore config,
    RuleEngine engine,
    ActivityLog? activity,
    SourceApp? caller,
    string appVersion,
    Action<Uri, SourceApp?> deliver,
    TimeProvider? time = null) : INativeHostBackend
{
    /// <summary>
    /// Automatic routing ignores a link NudgeJudge opened this recently: it's the browser loading the link NudgeJudge
    /// just sent it, and sending it on again could bounce it between browsers.
    /// </summary>
    public static readonly TimeSpan RecentlyOpenedWindow = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AppVersion => appVersion;

    public NativeOpenOutcome Open(Uri url, bool auto)
    {
        if (WouldOpenInCaller(url)) return NativeOpenOutcome.Stay;
        if (auto && RecentlyOpened(url)) return NativeOpenOutcome.Stay;
        deliver(url, caller);
        return NativeOpenOutcome.Sent;
    }

    public IReadOnlyList<string> AutoRouteHosts()
    {
        var now = _time.GetUtcNow();
        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in config.Current.Rules)
        {
            if (!rule.CatchInBrowser || !rule.Enabled || rule.ExpiresAt is { } at && now >= at) continue;
            foreach (var host in HostsOf(rule)) hosts.Add(host);
        }
        return [.. hosts];
    }

    /// <summary>
    /// The hosts a rule's links can be on: the host part of each positive domain, wildcard or path-prefix value.
    /// Empty when the rule can't be narrowed to hosts (only regular expressions, or no link condition at all),
    /// since catching every page load would be both slow and surprising.
    /// </summary>
    public static IReadOnlyList<string> HostsOf(Rule rule)
    {
        var links = rule.Leaves().Where(c => c.Field == ConditionField.Link && !c.Negate).ToList();
        if (links.Count == 0) return [];
        var hosts = new List<string>();
        foreach (var condition in links)
        {
            foreach (var value in condition.Values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                // One value we can't narrow (a regex) could match any host: then the rule can't be caught by host.
                if (PatternCompiler.HostOf(condition.MatchKind, value) is not { } host || host.Trim('*', '.').Length == 0) return [];
                hosts.Add(host);
            }
        }
        return hosts;
    }

    /// <summary>Where NudgeJudge would open the link, if it's the browser that asked.</summary>
    internal bool WouldOpenInCaller(Uri url)
    {
        if (caller is null) return false;
        var cfg = config.Current;
        BrowserProfile? target;
        if (cfg.Settings.RoutingEnabled)
        {
            var (rule, _) = engine.FindMatch(url, cfg.Rules, caller);
            target = rule is null ? null : cfg.FindBrowser(rule.BrowserId);
        }
        else
        {
            target = cfg.FindBrowser(cfg.Settings.PrimaryBrowserId);
        }
        return target is { NeedsReview: false } && IsSameBrowser(target, caller);
    }

    /// <summary>
    /// The same browser family as the caller, started plainly. A private window never counts, and neither does a
    /// profile or any other argument: nothing tells us which profile the click came from, so opening it explicitly
    /// is the safe side.
    /// </summary>
    internal static bool IsSameBrowser(BrowserProfile target, SourceApp caller)
    {
        if (target.IsPrivate || target.ProfileLabel is not null) return false;
        var args = target.Arguments.Replace(Browsers.BrowserLauncher.UrlPlaceholder, "", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(args)) return false;
        var callerExe = caller.Path ?? caller.Name;
        if (SamePath(target.ExecutablePath, callerExe)) return true;
        var family = BrowserFamily.Of(target.ExecutablePath);
        return family is not null && ReferenceEquals(family, BrowserFamily.Of(callerExe));
    }

    private bool RecentlyOpened(Uri url)
    {
        if (activity is null) return false;
        var since = _time.GetUtcNow() - RecentlyOpenedWindow;
        var target = url.AbsoluteUri;
        return activity.Snapshot().Any(e => e.Timestamp >= since && e.Url == target &&
            e.Outcome is ActivityOutcome.Routed or ActivityOutcome.Chosen or ActivityOutcome.PassedThrough);
    }

    private static bool SamePath(string a, string b) =>
        Path.IsPathRooted(a) && Path.IsPathRooted(b) &&
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
