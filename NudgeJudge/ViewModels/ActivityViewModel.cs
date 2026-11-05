using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Security;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

/// <summary>What "Create rule" on an Activity entry hands to the Rules page.</summary>
public sealed record RuleFromActivity(Uri Url, Guid? BrowserId, string? BrowserName, SourceApp? Source = null);

public sealed partial class ActivityViewModel : ViewModelBase
{
    private readonly ActivityLog _log;
    private readonly List<ActivityItemViewModel> _all = [];

    private readonly IWindowService _windows;

    public ActivityViewModel(ActivityLog log, IUiDispatcher dispatcher, SettingsViewModel settings, IWindowService windows)
    {
        _log = log;
        _windows = windows;
        Settings = settings;
        Undo = new UndoBar(dispatcher);
        log.Added += (_, e) => dispatcher.Post(() => Add(e));
        log.Reset += (_, _) => dispatcher.Post(Rebuild);
        settings.PropertyChanged += OnSettingsChanged;
        Rebuild();
    }

    public ObservableCollection<ActivityItemViewModel> Entries { get; } = [];
    public UndoBar Undo { get; }

    /// <summary>Empty-state hint (nothing arrives until NudgeJudge is the default browser) and the logging switch.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Most recent few, for the tray and the sidebar.</summary>
    public IEnumerable<ActivityItemViewModel> Recent => _all.Take(5);

    /// <summary>Apps links have come from, newest first, one per program name. Offered when writing rules.</summary>
    public IEnumerable<SourceApp> KnownSources =>
        _log.Snapshot().Reverse().Select(e => e.Source).OfType<SourceApp>().DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the user asks for a rule built from an entry. The main window takes it to the Rules page.</summary>
    public event EventHandler<RuleFromActivity>? CreateRuleRequested;

    /// <summary>Raised from a link that couldn't open because its browser awaits review. The main window opens that browser.</summary>
    public event EventHandler<Guid>? ReviewBrowserRequested;

    /// <summary>Whether a browser is waiting for review. Set by the main window, which knows the config.</summary>
    public Func<Guid, bool>? BrowserNeedsReview { get; set; }

    /// <summary>A browser's review state changed: entries re-ask whether to offer "Review browser".</summary>
    public void RefreshReviewState()
    {
        foreach (var item in _all) item.RefreshReview();
    }

    internal bool NeedsReview(Guid browserId) => BrowserNeedsReview?.Invoke(browserId) == true;

    internal void ReviewBrowser(ActivityItemViewModel item)
    {
        if (item.BrowserId is { } id) ReviewBrowserRequested?.Invoke(this, id);
    }

    [ObservableProperty] public partial string Filter { get; set; } = "";

    public bool IsEmpty => _all.Count == 0;
    public bool NoFilterResults => _all.Count > 0 && Entries.Count == 0;
    public bool IsLoggingOff => !Settings.ActivityLogEnabled;
    public bool ShowEmptyHint => IsEmpty && !IsLoggingOff;
    public bool ShowLoggingOffEmpty => IsEmpty && IsLoggingOff;
    public bool ShowLoggingOffNotice => !IsEmpty && IsLoggingOff;

    public string CountText => IsLoggingOff
        ? (_all.Count == 1 ? "1 link · logging is off" : $"{_all.Count} links · logging is off")
        : (_all.Count == 1 ? "1 link" : $"{_all.Count} links") + $" · keeps the latest {_log.Capacity}";

    partial void OnFilterChanged(string value) => Refilter();

    [RelayCommand]
    private void Clear()
    {
        var cleared = _log.Snapshot();
        if (cleared.Count == 0) return;
        _log.Clear();
        Undo.Show(cleared.Count == 1 ? "Cleared 1 link" : $"Cleared {cleared.Count} links", () =>
            // Snapshot is oldest first, so links handled since the clear stay newest.
            _log.Replace(cleared.Concat(_log.Snapshot())));
    }

    [RelayCommand] private void OpenSetup() => _windows.ShowMainWindow(AppPage.Settings);
    [RelayCommand] private void ClearFilter() => Filter = "";
    [RelayCommand] private void TurnOnLogging() => Settings.ActivityLogEnabled = true;

    /// <summary>Picks up links that other NudgeJudge processes logged while this one wasn't looking.</summary>
    public void Reload() => _log.Reload();

    internal void CreateRule(ActivityItemViewModel item)
    {
        if (item.RuleUrl is { } url)
            CreateRuleRequested?.Invoke(this, new RuleFromActivity(url, item.BrowserId, item.BrowserName, item.Entry.Source));
    }

    private void Add(ActivityEntry entry)
    {
        var item = new ActivityItemViewModel(entry, this);
        _all.Insert(0, item);
        if (item.Matches(Filter)) Entries.Insert(0, item);

        // The log dropped its oldest entry when full; drop it here too.
        while (_all.Count > _log.Capacity)
        {
            var oldest = _all[^1];
            _all.RemoveAt(_all.Count - 1);
            Entries.Remove(oldest);
        }
        NotifyCounts();
    }

    private void Rebuild()
    {
        _all.Clear();
        foreach (var e in _log.Snapshot()) _all.Insert(0, new ActivityItemViewModel(e, this));
        Refilter();
    }

    private void Refilter()
    {
        Entries.Clear();
        foreach (var item in _all.Where(i => i.Matches(Filter))) Entries.Add(item);
        NotifyCounts();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.ActivityLogEnabled) or nameof(SettingsViewModel.ActivityLogLimit))
            NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoFilterResults));
        OnPropertyChanged(nameof(IsLoggingOff));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowLoggingOffEmpty));
        OnPropertyChanged(nameof(ShowLoggingOffNotice));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Recent));
    }
}

public sealed partial class ActivityItemViewModel(ActivityEntry entry, ActivityViewModel? owner = null) : ViewModelBase
{
    public ActivityEntry Entry { get; } = entry;
    public string Url { get; } = entry.Url;
    public string Host { get; } = Uri.TryCreate(entry.Url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "Not a web link";
    public string Time { get; } = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string FullTime { get; } = entry.Timestamp.ToLocalTime().ToString("f");
    public ActivityOutcome Outcome { get; } = entry.Outcome;

    public string OutcomeLabel { get; } = entry.Outcome switch
    {
        ActivityOutcome.Routed => "Routed",
        ActivityOutcome.Asked => "Asked",
        ActivityOutcome.Chosen => "Chosen",
        ActivityOutcome.PassedThrough => "Passed",
        ActivityOutcome.Rejected => "Rejected",
        _ => "Failed",
    };

    // Route line in runs: "Rule 2  YouTube from Teams  →  Chrome". Entries from before rules had names show
    // the pattern instead, set in mono like patterns everywhere else.
    public string RoutePrefix { get; } = entry.Outcome == ActivityOutcome.Routed ? $"Rule {entry.RuleNumber}  " : "";
    public string RouteName { get; } = entry.Outcome == ActivityOutcome.Routed ? entry.RuleName ?? "" : "";
    public string RoutePattern { get; } = entry.Outcome == ActivityOutcome.Routed && entry.RuleName is null ? entry.RulePattern ?? "" : "";

    /// <summary>The app the link was opened from, or null when it couldn't be told.</summary>
    public string? SourceName { get; } = entry.Source?.DisplayName;
    public bool HasSource => SourceName is not null;

    /// <summary>The program behind the name: its file name and, when known, its full path.</summary>
    public string? SourceDetail { get; } = entry.Source is { } s
        ? s.Path is { } p ? $"{s.Name}\n{p}" : s.Name
        : null;

    public string RouteSuffix { get; } = entry.Outcome switch
    {
        ActivityOutcome.Routed => $"  →  {entry.BrowserName}",
        ActivityOutcome.Chosen => $"You chose {entry.BrowserName}",
        ActivityOutcome.PassedThrough => $"Routing off  →  {entry.BrowserName}",
        ActivityOutcome.Asked => "No rule matched, so the chooser opened",
        _ => entry.Detail ?? "",
    };

    public string RouteText { get; } = entry.Outcome switch
    {
        ActivityOutcome.Routed => $"Rule {entry.RuleNumber}  {entry.RuleName ?? entry.RulePattern}  →  {entry.BrowserName}",
        ActivityOutcome.Chosen => $"You chose {entry.BrowserName}",
        ActivityOutcome.PassedThrough => $"Routing off  →  {entry.BrowserName}",
        ActivityOutcome.Asked => "No rule matched, so the chooser opened",
        _ => entry.Detail ?? "",
    };

    /// <summary>
    /// Handled by a NudgeJudge that closes with the program that started it, usually a browser whose extension had to
    /// start NudgeJudge inside its job (see ProcessJob).
    /// </summary>
    public bool TiedToLauncher { get; } = entry.TiedToLauncher;

    // One lamp per outcome: amber only when a rule set the route.
    public bool IsRouted => Outcome is ActivityOutcome.Routed;
    public bool IsChosen => Outcome is ActivityOutcome.Chosen;
    public bool IsAsked => Outcome is ActivityOutcome.Asked or ActivityOutcome.PassedThrough;
    public bool IsProblem => Outcome is ActivityOutcome.Rejected or ActivityOutcome.Failed;

    public string Summary => $"{Time}  {OutcomeLabel}  {Host}";

    public Guid? BrowserId { get; } = entry.BrowserId;
    public string? BrowserName { get; } = entry.BrowserName;

    /// <summary>The link a rule can be made for. Null for rejected links, which aren't web links.</summary>
    public Uri? RuleUrl { get; } = UrlValidator.Validate(entry.Url).Url;
    public bool CanCreateRule => owner is not null && RuleUrl is not null;

    [RelayCommand(CanExecute = nameof(CanCreateRule))]
    private void CreateRule() => owner?.CreateRule(this);

    /// <summary>The link failed because its browser came from an import and still needs review: offer the fix.</summary>
    public bool CanReviewBrowser =>
        Outcome is ActivityOutcome.Failed && BrowserId is { } id && owner?.NeedsReview(id) == true;

    /// <summary>"Create rule" steps aside while the fix is on offer: one action per row.</summary>
    public bool ShowCreateRule => CanCreateRule && !CanReviewBrowser;

    [RelayCommand]
    private void ReviewBrowser() => owner?.ReviewBrowser(this);

    internal void RefreshReview()
    {
        OnPropertyChanged(nameof(CanReviewBrowser));
        OnPropertyChanged(nameof(ShowCreateRule));
    }

    internal bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        Url.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        RouteText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (SourceDetail?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (SourceName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
