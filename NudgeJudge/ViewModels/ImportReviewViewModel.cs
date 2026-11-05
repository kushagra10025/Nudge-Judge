using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;

namespace NudgeJudge.ViewModels;

/// <summary>
/// Shows what an import would do before it does it. What needs a look comes first and in full: a browser whose
/// program would change (old and new side by side, off until ticked), a suspicious browser, a rule that would
/// open somewhere else. Everything else folds into one "Show" line, so a risky entry can't hide among harmless
/// ones. Nothing changes until Import is pressed.
/// </summary>
public sealed partial class ImportReviewViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly ConfigExport _export;
    private readonly Action<ImportResult?> _done;
    private readonly Func<string, bool>? _programExists;
    private ImportPlan _plan = null!;

    public ImportReviewViewModel(IConfigStore config, ConfigExport export, string fileName, Action<ImportResult?> done,
        Func<string, bool>? programExists = null)
    {
        _config = config;
        _export = export;
        _done = done;
        _programExists = programExists;
        FileName = fileName;
        ExportedText = $"Exported {export.ExportedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)}";
        IsRestore = ImportPlan.SuggestMode(config.Current, export) == ImportMode.Restore;
        Build();
    }

    public string FileName { get; }
    public string ExportedText { get; }

    /// <summary>Every browser and rule in the file, those needing a look first.</summary>
    public ObservableCollection<BrowserReviewRow> Browsers { get; } = [];
    public ObservableCollection<RuleReviewRow> Rules { get; } = [];

    public ObservableCollection<BrowserReviewRow> AttentionBrowsers { get; } = [];
    public ObservableCollection<BrowserReviewRow> OtherBrowsers { get; } = [];
    public ObservableCollection<RuleReviewRow> AttentionRules { get; } = [];
    public ObservableCollection<RuleReviewRow> OtherRules { get; } = [];

    public bool HasAttentionBrowsers => AttentionBrowsers.Count > 0;
    public bool HasOtherBrowsers => OtherBrowsers.Count > 0;
    public bool HasAttentionRules => AttentionRules.Count > 0;
    public bool HasOtherRules => OtherRules.Count > 0;
    public bool HasBrowsers => Browsers.Count > 0;
    public bool HasRules => Rules.Count > 0;
    public bool IsEmpty => !HasBrowsers && !HasRules;
    public string Contents => $"{ImportResult.Count(Rules.Count, "rule")} and {ImportResult.Count(Browsers.Count, "browser")}";

    /// <summary>The folded rows, opened on request.</summary>
    [ObservableProperty] public partial bool ShowOtherBrowsers { get; set; }
    [ObservableProperty] public partial bool ShowOtherRules { get; set; }

    public string OtherBrowsersLabel => Folded(OtherBrowsers.Count, "browser", HasAttentionBrowsers);
    public string OtherRulesLabel => Folded(OtherRules.Count, "rule", HasAttentionRules);

    /// <summary>Restore replaces matching ids; otherwise everything is added as new.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddAsNew), nameof(ModeHint))]
    public partial bool IsRestore { get; set; }

    public bool IsAddAsNew
    {
        get => !IsRestore;
        set => IsRestore = !value;
    }

    public string ModeHint => IsRestore
        ? "Rules and browsers with the same id as yours replace them. A browser's program never changes unless you allow it below."
        : "Everything is added as new, below your own rules. Nothing you have is changed.";

    public bool HasAttention => AttentionText is not null;

    /// <summary>What needs a look, counted: "2 browsers need review · 1 rule will open somewhere else".</summary>
    public string? AttentionText
    {
        get
        {
            var parts = new List<string>();
            var changes = Browsers.Count(b => b.ChangesProgram && !b.AllowProgramChange);
            if (changes > 0) parts.Add(changes == 1 ? "1 browser would run a different program" : $"{changes} browsers would run a different program");
            var review = Browsers.Count(b => b.WillNeedReview);
            if (review > 0) parts.Add(review == 1 ? "1 browser needs review" : $"{review} browsers need review");
            var redirects = Rules.Count(r => r.IsRedirect);
            if (redirects > 0) parts.Add(redirects == 1 ? "1 rule will open somewhere else" : $"{redirects} rules will open somewhere else");
            var changed = Rules.Count(r => r.ChangesYours && !r.IsRedirect);
            if (changed > 0) parts.Add(changed == 1 ? "1 of your rules changes" : $"{changed} of your rules change");
            var homeless = Rules.Count(r => r.HasNoBrowser);
            if (homeless > 0) parts.Add(homeless == 1 ? "1 rule has no browser" : $"{homeless} rules have no browser");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    partial void OnIsRestoreChanged(bool value)
    {
        if (_plan is not null) Build();
    }

    private void Build()
    {
        var current = _config.Current;
        _plan = ImportPlan.Create(current, _export, IsRestore ? ImportMode.Restore : ImportMode.AddAsNew, _programExists);

        // Grouped once per plan: ticking "allow" updates rows in place rather than moving them under the pointer.
        var browsers = _plan.Browsers.Select(b => new BrowserReviewRow(b, this)).ToList();
        var rules = _plan.Rules.Select(r => new RuleReviewRow(r, _plan, current)).ToList();
        Fill(Browsers, browsers.OrderByDescending(b => b.NeedsAttention));
        Fill(AttentionBrowsers, browsers.Where(b => b.NeedsAttention));
        Fill(OtherBrowsers, browsers.Where(b => !b.NeedsAttention));
        Fill(Rules, rules.OrderByDescending(r => r.NeedsAttention));
        Fill(AttentionRules, rules.Where(r => r.NeedsAttention));
        Fill(OtherRules, rules.Where(r => !r.NeedsAttention));
        ShowOtherBrowsers = ShowOtherRules = false;
        OnPropertyChanged(string.Empty);
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static string Folded(int count, string what, bool afterAttention)
    {
        var items = ImportResult.Count(count, what);
        return afterAttention ? $"{items} more, nothing to check" : $"{items}, nothing to check";
    }

    /// <summary>A browser choice changed: rules' destinations and the counts follow it.</summary>
    internal void Refresh()
    {
        foreach (var r in Rules) r.Update(_plan, _config.Current);
        OnPropertyChanged(nameof(AttentionText));
        OnPropertyChanged(nameof(HasAttention));
    }

    [RelayCommand] private void ToggleOtherBrowsers() => ShowOtherBrowsers = !ShowOtherBrowsers;
    [RelayCommand] private void ToggleOtherRules() => ShowOtherRules = !ShowOtherRules;

    [RelayCommand]
    private void Confirm()
    {
        ImportResult? result = null;
        _config.Update(c => result = _plan.Apply(c));
        _done(result);
    }

    [RelayCommand]
    private void Cancel() => _done(null);
}

public sealed partial class BrowserReviewRow(BrowserImport browser, ImportReviewViewModel owner) : ViewModelBase
{
    public string Name => browser.Incoming.Name;
    public string Program => Describe(browser.Incoming.ExecutablePath, browser.Incoming.Arguments);

    /// <summary>What your browser with the same id runs now, when the import would change it.</summary>
    public string? CurrentProgram => browser.ChangesProgram ? Describe(browser.SameId!.ExecutablePath, browser.SameId.Arguments) : null;
    public bool ChangesProgram => browser.ChangesProgram;
    public string AllowLabel => $"Change what “{browser.SameId?.Name}” runs";

    /// <summary>The program's problems as sentences; risky arguments are listed apart, in mono.</summary>
    public IReadOnlyList<string> Warnings { get; } = browser.Assessment.ProgramReasons.ToList();
    public bool HasWarnings => Warnings.Count > 0 || HasRiskyArguments;
    public string RiskyArguments { get; } = string.Join("  ", browser.Assessment.RiskyArguments);
    public bool HasRiskyArguments => RiskyArguments.Length > 0;
    public bool WillNeedReview => browser.WillNeedReview;

    /// <summary>Something here wants a careful look: a program change, or a suspicious program. Fixed per plan.</summary>
    public bool NeedsAttention { get; } = browser.ChangesProgram || !browser.Assessment.IsClean;

    public bool AllowProgramChange
    {
        get => browser.AllowProgramChange;
        set
        {
            if (browser.AllowProgramChange == value) return;
            browser.AllowProgramChange = value;
            OnPropertyChanged(string.Empty);
            owner.Refresh();
        }
    }

    public string ActionText => browser.Action switch
    {
        BrowserImportAction.Add => "New",
        BrowserImportAction.Update => SameDetails(browser.SameId!, browser.Incoming) ? "Unchanged" : $"Updates your {browser.SameId!.Name}",
        BrowserImportAction.ReplaceProgram => $"Replaces your {browser.SameId!.Name}, program too",
        BrowserImportAction.KeepSeparate => "Added beside yours, yours unchanged",
        BrowserImportAction.UseExisting => $"Same as your {browser.SameProgram!.Name}",
        _ => "",
    };

    public string? ReviewNote => WillNeedReview
        ? "Links won't open in it until you review it on the Browsers page."
        : null;
    public bool HasReviewNote => ReviewNote is not null;

    private static bool SameDetails(BrowserProfile a, BrowserProfile b) =>
        a.Name == b.Name && a.ProfileLabel == b.ProfileLabel && a.IsPrivate == b.IsPrivate;

    private static string Describe(string exe, string? args) => string.IsNullOrWhiteSpace(args) ? exe : $"{exe}  {args}";
}

public sealed partial class RuleReviewRow : ViewModelBase
{
    private readonly RuleImport _rule;

    public RuleReviewRow(RuleImport rule, ImportPlan plan, AppConfig current)
    {
        _rule = rule;
        Name = rule.Incoming.Name;
        Summary = RuleSummary.DescribeText(rule.Incoming);
        Parts = RuleSummary.Describe(rule.Incoming);
        ChangesYours = rule.SameId is { } mine && !rule.Incoming.RoutesLike(mine);
        Update(plan, current);
        // Fixed per plan, like the browsers', so rows don't jump when a choice above changes a destination.
        NeedsAttention = IsRedirect || DestinationNeedsReview || HasNoBrowser || ChangesYours;
    }

    public string Name { get; }
    public string Summary { get; }

    /// <summary>The conditions drawn as on the Rules page: glyphs, patterns in mono.</summary>
    public IReadOnlyList<SummaryPart> Parts { get; }

    /// <summary>Restore over one of your rules with different conditions or browser.</summary>
    public bool ChangesYours { get; }
    public bool NeedsAttention { get; }

    /// <summary>What happens to your rule. Never "Unchanged" when its links would go somewhere else.</summary>
    public string ActionText => _rule.SameId switch
    {
        null => "New",
        { } when IsRedirect && !ChangesYours => "Opens somewhere else",
        { } mine when !ChangesYours && mine.Name == Name => "Unchanged",
        { } mine when !ChangesYours => $"Renames “{mine.Name}”",
        { } mine => mine.Name == Name ? "Changes your version" : $"Replaces “{mine.Name}”",
    };

    [ObservableProperty] public partial string Destination { get; private set; } = "";
    [ObservableProperty] public partial bool DestinationNeedsReview { get; private set; }
    [ObservableProperty] public partial bool HasNoBrowser { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRedirect), nameof(ActionText))]
    public partial string? Redirect { get; private set; }

    public bool IsRedirect => Redirect is not null;

    /// <summary>Where the rule will send links, by the current choices above.</summary>
    internal void Update(ImportPlan plan, AppConfig current)
    {
        var destination = plan.Destination(_rule.Incoming.BrowserId, current);
        Destination = destination?.Name ?? "No browser, pick one after importing";
        HasNoBrowser = destination is null;
        DestinationNeedsReview = destination is { NeedsReview: true };

        // Restoring over one of your rules and sending its links somewhere else: easy to miss, so say it.
        Redirect = _rule.SameId is { } existing && destination is { } to && to.Id != existing.BrowserId
            ? $"Now opens in {to.Name} instead of {current.FindBrowser(existing.BrowserId)?.Name ?? "a missing browser"}."
            : null;
    }
}
