using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Config;

/// <summary>How an import treats ids that match what's already here.</summary>
public enum ImportMode
{
    /// <summary>Your own backup: a rule or browser with the same id replaces yours (restoring it in place).</summary>
    Restore,

    /// <summary>Someone else's file: everything gets a new id and is added, so nothing of yours can be overwritten.</summary>
    AddAsNew,
}

public enum BrowserImportAction
{
    /// <summary>Added as a new browser.</summary>
    Add,

    /// <summary>Replaces the browser with the same id; its program and arguments don't change.</summary>
    Update,

    /// <summary>Replaces the browser with the same id, including a different program or arguments the user allowed.</summary>
    ReplaceProgram,

    /// <summary>Same id as one of yours but a different program, not allowed: added alongside instead, yours untouched.</summary>
    KeepSeparate,

    /// <summary>Runs exactly what one of yours does (same program and arguments): rules use yours, nothing is added.</summary>
    UseExisting,
}

/// <summary>One browser in an import, what will happen to it, and why it might need a look.</summary>
public sealed class BrowserImport
{
    internal BrowserImport(BrowserProfile incoming, BrowserProfile? sameId, BrowserProfile? sameProgram, BrowserAssessment assessment)
    {
        Incoming = incoming;
        SameId = sameId;
        SameProgram = sameProgram;
        Assessment = assessment;
    }

    public BrowserProfile Incoming { get; }

    /// <summary>Restore only: your browser with the same id.</summary>
    public BrowserProfile? SameId { get; }

    /// <summary>One of your browsers that runs exactly the same program and arguments, if any.</summary>
    public BrowserProfile? SameProgram { get; }

    public BrowserAssessment Assessment { get; }

    /// <summary>Restore would change the program or arguments behind one of your browsers.</summary>
    public bool ChangesProgram => SameId is { } mine && !RunsSame(mine, Incoming);

    /// <summary>The user allowed <see cref="ChangesProgram"/>. Off by default: never changed silently.</summary>
    public bool AllowProgramChange { get; set; }

    public BrowserImportAction Action =>
        SameId is not null
            ? !ChangesProgram ? BrowserImportAction.Update
            : AllowProgramChange ? BrowserImportAction.ReplaceProgram
            : SameProgram is not null ? BrowserImportAction.UseExisting
            : BrowserImportAction.KeepSeparate
            : SameProgram is not null ? BrowserImportAction.UseExisting
            : BrowserImportAction.Add;

    /// <summary>
    /// Imported with a warning, so links won't open in it until reviewed. A browser whose program doesn't change
    /// keeps your trust in it; one that reuses yours brings nothing new.
    /// </summary>
    public bool WillNeedReview => Action switch
    {
        BrowserImportAction.Add or BrowserImportAction.KeepSeparate or BrowserImportAction.ReplaceProgram => !Assessment.IsClean,
        BrowserImportAction.Update => SameId!.NeedsReview,
        _ => false,
    };

    internal static bool RunsSame(BrowserProfile a, BrowserProfile b) =>
        string.Equals(a.ExecutablePath.Trim(), b.ExecutablePath.Trim(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        string.Equals((a.Arguments ?? "").Trim(), (b.Arguments ?? "").Trim(), StringComparison.Ordinal);
}

/// <summary>One rule in an import and what it replaces, if anything.</summary>
public sealed class RuleImport
{
    internal RuleImport(Rule incoming, Rule? sameId)
    {
        Incoming = incoming;
        SameId = sameId;
    }

    public Rule Incoming { get; }

    /// <summary>Restore only: your rule with the same id, which this replaces.</summary>
    public Rule? SameId { get; }
}

public sealed record ImportResult(int BrowsersAdded, int BrowsersReplaced, int BrowsersReused, int RulesAdded, int RulesReplaced, int NeedReview)
{
    public static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    public string Describe()
    {
        static string Part(int added, int replaced, string what) =>
            replaced == 0 ? Count(added, what) : $"{Count(added + replaced, what)} ({replaced} replaced)";
        var text = $"Imported {Part(RulesAdded, RulesReplaced, "rule")} and {Part(BrowsersAdded, BrowsersReplaced, "browser")}.";
        if (BrowsersReused > 0) text += $" {Count(BrowsersReused, "browser")} matched ones you already had.";
        if (NeedReview > 0)
            text += NeedReview == 1
                ? " 1 browser needs review on the Browsers page before links open in it."
                : $" {NeedReview} browsers need review on the Browsers page before links open in them.";
        return text;
    }
}

/// <summary>
/// What importing a file would do, worked out before anything changes so the user can review it. Browsers
/// are never given a different program silently, a suspicious browser is imported needing review, and in
/// <see cref="ImportMode.AddAsNew"/> nothing of yours is replaced at all.
/// </summary>
public sealed class ImportPlan
{
    private ImportPlan(ImportMode mode, IReadOnlyList<BrowserImport> browsers, IReadOnlyList<RuleImport> rules)
    {
        Mode = mode;
        Browsers = browsers;
        Rules = rules;
    }

    public ImportMode Mode { get; }
    public IReadOnlyList<BrowserImport> Browsers { get; }
    public IReadOnlyList<RuleImport> Rules { get; }

    /// <summary>Restore is the natural choice when the file shares ids with this setup (it's likely your backup).</summary>
    public static ImportMode SuggestMode(AppConfig current, ConfigExport export) =>
        export.Browsers.Any(b => current.Browsers.Any(m => m.Id == b.Id)) || export.Rules.Any(r => current.Rules.Any(m => m.Id == r.Id))
            ? ImportMode.Restore
            : ImportMode.AddAsNew;

    /// <param name="programExists">For tests; defaults to looking on disk and on PATH.</param>
    public static ImportPlan Create(AppConfig current, ConfigExport export, ImportMode mode, Func<string, bool>? programExists = null)
    {
        var restore = mode == ImportMode.Restore;
        var browsers = export.Browsers.Select(b => new BrowserImport(
            b,
            restore ? current.Browsers.Find(m => m.Id == b.Id) : null,
            current.Browsers.Find(m => BrowserImport.RunsSame(m, b) && !m.NeedsReview),
            BrowserRisk.Assess(b, programExists))).ToList();
        var rules = export.Rules.Select(r => new RuleImport(r, restore ? current.Rules.Find(m => m.Id == r.Id) : null)).ToList();
        return new ImportPlan(mode, browsers, rules);
    }

    /// <summary>
    /// Where a rule that points at <paramref name="importedBrowserId"/> will send links after the import, by the
    /// current choices. Null when the browser is in neither the file nor this setup.
    /// </summary>
    public (Guid Id, string Name, bool NeedsReview)? Destination(Guid importedBrowserId, AppConfig current)
    {
        if (Browsers.FirstOrDefault(b => b.Incoming.Id == importedBrowserId) is { } b)
        {
            return b.Action switch
            {
                BrowserImportAction.UseExisting => (b.SameProgram!.Id, b.SameProgram.Name, false),
                BrowserImportAction.KeepSeparate => (Guid.Empty, SeparateName(b.Incoming), b.WillNeedReview),
                _ => (b.Incoming.Id, b.Incoming.Name, b.WillNeedReview),
            };
        }
        // AddAsNew can't point at your browsers by id; Restore can (the rule came from this setup).
        if (Mode == ImportMode.Restore && current.FindBrowser(importedBrowserId) is { } mine) return (mine.Id, mine.Name, mine.NeedsReview);
        return null;
    }

    private static string SeparateName(BrowserProfile b) => ConfigTransfer.CleanName($"{b.Name} (imported)", "Imported browser");

    /// <summary>Applies the plan to <paramref name="config"/> (inside a config update). Rechecks ids against it.</summary>
    public ImportResult Apply(AppConfig config)
    {
        var restore = Mode == ImportMode.Restore;
        var map = new Dictionary<Guid, Guid>();
        int added = 0, replaced = 0, reused = 0, needReview = 0;

        foreach (var b in Browsers)
        {
            var copy = b.Incoming.Clone();
            copy.NeedsReview = b.WillNeedReview;
            var at = restore ? config.Browsers.FindIndex(m => m.Id == copy.Id) : -1;
            switch (b.Action)
            {
                case BrowserImportAction.UseExisting when config.FindBrowser(b.SameProgram!.Id) is { } existing:
                    map[copy.Id] = existing.Id;
                    reused++;
                    continue;
                case BrowserImportAction.Update or BrowserImportAction.ReplaceProgram when at >= 0:
                    if (b.Action == BrowserImportAction.Update)
                    {
                        // Same program: only the name and labels can change. Keep exactly what runs.
                        copy.ExecutablePath = config.Browsers[at].ExecutablePath;
                        copy.Arguments = config.Browsers[at].Arguments;
                    }
                    config.Browsers[at] = copy;
                    map[copy.Id] = copy.Id;
                    replaced++;
                    break;
                default:
                    if (b.Action == BrowserImportAction.KeepSeparate) copy.Name = SeparateName(copy);
                    // A new id unless restoring a browser that isn't here: an id must never land on one of yours.
                    if (!restore || b.Action == BrowserImportAction.KeepSeparate || config.FindBrowser(copy.Id) is not null)
                        copy.Id = Guid.NewGuid();
                    copy.Source = BrowserSource.Manual;
                    config.Browsers.Add(copy);
                    map[b.Incoming.Id] = copy.Id;
                    added++;
                    break;
            }
            if (copy.NeedsReview) needReview++;
        }

        int rulesAdded = 0, rulesReplaced = 0;
        foreach (var r in Rules)
        {
            var copy = r.Incoming.Clone();
            copy.Compiled = null;
            copy.IsSample = false;
            if (map.TryGetValue(copy.BrowserId, out var browserId)) copy.BrowserId = browserId;
            else if (!restore) copy.BrowserId = Guid.Empty; // not in the file: never guess one of yours

            var at = restore ? config.Rules.FindIndex(m => m.Id == copy.Id) : -1;
            if (at >= 0)
            {
                config.Rules[at] = copy;
                rulesReplaced++;
                continue;
            }
            if (!restore || config.Rules.Any(m => m.Id == copy.Id)) copy.Id = Guid.NewGuid();
            if (!restore) copy.ClonedFromId = null;
            config.Rules.Add(copy);
            rulesAdded++;
        }

        // Someone set up enough to share: no need to offer the sample setup any more.
        if (Browsers.Count + Rules.Count > 0) config.Settings.OfferSamples = false;
        return new ImportResult(added, replaced, reused, rulesAdded, rulesReplaced, needReview);
    }
}
