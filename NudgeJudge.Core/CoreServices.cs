using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core;

/// <summary>
/// The half of the app that routes links: config, rules, activity log and router, with no UI. The entry point
/// builds it before the UI so a launch that only brings links can open them without starting the UI at all.
/// </summary>
public sealed class CoreServices
{
    private CoreServices(ISystemBoot boot, JsonConfigStore config, RuleEngine engine, ActivityLog activity, LinkRouter router)
    {
        Boot = boot;
        Config = config;
        Engine = engine;
        Activity = activity;
        Router = router;
    }

    public ISystemBoot Boot { get; }
    public JsonConfigStore Config { get; }
    public RuleEngine Engine { get; }
    public ActivityLog Activity { get; }
    public LinkRouter Router { get; }

    /// <summary>Loads the user's config and activity log from their usual places.</summary>
    public static CoreServices Load()
    {
        var boot = new SystemBoot();
        var config = new JsonConfigStore(AppPaths.ConfigFile, SampleData.FirstRun);
        // Temporary rules that ran out while the app was closed (every "until restart" rule, after a reboot).
        var now = DateTimeOffset.UtcNow;
        if (config.Current.Rules.Any(r => r.IsExpired(now, boot)))
            config.Update(c => c.Rules.RemoveAll(r => r.IsExpired(now, boot)));
        var engine = new RuleEngine(boot);
        var activity = ActivityLog.FollowingSettings(config, new ActivityLogFile(AppPaths.ActivityLogFile));
        var router = new LinkRouter(config, engine, new BrowserLauncher(), activity);
        return new CoreServices(boot, config, engine, activity, router);
    }

    /// <summary>
    /// Routes <paramref name="urls"/> and returns the ones still waiting for the user to pick a browser
    /// (no rule matched, or the rule's browser wouldn't start).
    /// </summary>
    public IReadOnlyList<IncomingLink> Route(IEnumerable<string> urls, SourceApp? source)
    {
        var pending = new List<IncomingLink>();
        void Collect(object? sender, IncomingLink link) => pending.Add(link);
        Router.ChoiceRequested += Collect;
        try
        {
            foreach (var url in urls)
                Router.Route(url, source);
        }
        finally
        {
            Router.ChoiceRequested -= Collect;
        }
        return pending;
    }
}
