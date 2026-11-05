using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;

namespace NudgeJudge.ViewModels;

/// <summary>
/// Shows what an import would do before it does it: each browser with the exact program and arguments it runs
/// (old and new side by side when one of yours would change, which needs ticking), warnings about suspicious
/// browsers, and each rule with where it will send links. Nothing changes until Import is pressed.
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
    public ObservableCollection<BrowserReviewRow> Browsers { get; } = [];
    public ObservableCollection<RuleReviewRow> Rules { get; } = [];
    public bool HasBrowsers => Browsers.Count > 0;
    public bool HasRules => Rules.Count > 0;
    public bool IsEmpty => !HasBrowsers && !HasRules;
    public string Contents => $"{ImportResult.Count(Rules.Count, "rule")} and {ImportResult.Count(Browsers.Count, "browser")}";

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

    public bool HasAttention => Browsers.Any(b => b.NeedsAttention) || Rules.Any(r => r.IsRedirect);

    partial void OnIsRestoreChanged(bool value)
    {
        if (_plan is not null) Build();
    }

    private void Build()
    {
        var current = _config.Current;
        _plan = ImportPlan.Create(current, _export, IsRestore ? ImportMode.Restore : ImportMode.AddAsNew, _programExists);
        Browsers.Clear();
        foreach (var b in _plan.Browsers) Browsers.Add(new BrowserReviewRow(b, this));
        Rules.Clear();
        foreach (var r in _plan.Rules) Rules.Add(new RuleReviewRow(r, _plan, current));
        OnPropertyChanged(nameof(HasBrowsers));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Contents));
        OnPropertyChanged(nameof(HasAttention));
    }

    /// <summary>A browser choice changed: rules' destinations and the rows' wording follow it.</summary>
    internal void Refresh()
    {
        foreach (var b in Browsers) b.Refresh();
        Rules.Clear();
        foreach (var r in _plan.Rules) Rules.Add(new RuleReviewRow(r, _plan, _config.Current));
        OnPropertyChanged(nameof(HasAttention));
    }

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

    public IReadOnlyList<string> Warnings { get; } = browser.Assessment.Describe().ToList();
    public bool HasWarnings => Warnings.Count > 0;
    public bool WillNeedReview => browser.WillNeedReview;

    /// <summary>Something here wants a careful look: a program change, or a suspicious program.</summary>
    public bool NeedsAttention => ChangesProgram || HasWarnings;

    public bool AllowProgramChange
    {
        get => browser.AllowProgramChange;
        set
        {
            if (browser.AllowProgramChange == value) return;
            browser.AllowProgramChange = value;
            OnPropertyChanged();
            owner.Refresh();
        }
    }

    public string ActionText => browser.Action switch
    {
        BrowserImportAction.Add => "New",
        BrowserImportAction.Update => $"Updates “{browser.SameId!.Name}”",
        BrowserImportAction.ReplaceProgram => $"Replaces “{browser.SameId!.Name}”, program too",
        BrowserImportAction.KeepSeparate => "Added separately, yours unchanged",
        BrowserImportAction.UseExisting => $"Uses your “{browser.SameProgram!.Name}”",
        _ => "",
    };

    public string? ReviewNote => WillNeedReview
        ? "Links won't open in it until you review it on the Browsers page."
        : null;
    public bool HasReviewNote => ReviewNote is not null;

    internal void Refresh() => OnPropertyChanged(string.Empty);

    private static string Describe(string exe, string? args) => string.IsNullOrWhiteSpace(args) ? exe : $"{exe}  {args}";
}

public sealed class RuleReviewRow
{
    public RuleReviewRow(RuleImport rule, ImportPlan plan, AppConfig current)
    {
        Name = rule.Incoming.Name;
        Summary = RuleSummary.DescribeText(rule.Incoming);
        ActionText = rule.SameId is { } mine ? $"Replaces “{mine.Name}”" : "New";

        var destination = plan.Destination(rule.Incoming.BrowserId, current);
        Destination = destination switch
        {
            null => "No browser (pick one after importing)",
            { NeedsReview: true } d => $"{d.Name} (needs review)",
            { } d => d.Name,
        };
        DestinationProblem = destination is null or { NeedsReview: true };

        // Restoring over one of your rules and sending its links somewhere else: easy to miss, so say it.
        if (rule.SameId is { } existing && destination is { } to && to.Id != existing.BrowserId)
        {
            var from = current.FindBrowser(existing.BrowserId)?.Name ?? "a missing browser";
            Redirect = $"Now opens in {to.Name} instead of {from}.";
        }
    }

    public string Name { get; }
    public string Summary { get; }
    public string ActionText { get; }
    public string Destination { get; }
    public bool DestinationProblem { get; }
    public string? Redirect { get; }
    public bool IsRedirect => Redirect is not null;
}
