using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Routing;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

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
        foreach (var e in log.Snapshot()) _all.Insert(0, new ActivityItemViewModel(e));
        log.Added += (_, e) => dispatcher.Post(() => Add(e));
        log.Cleared += (_, _) => dispatcher.Post(() => { _all.Clear(); Refilter(); });
        Refilter();
    }

    public ObservableCollection<ActivityItemViewModel> Entries { get; } = [];
    public UndoBar Undo { get; }

    /// <summary>Empty-state hint: nothing arrives until NudgeJudge is the default browser.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Most recent few, for the tray and the sidebar.</summary>
    public IEnumerable<ActivityItemViewModel> Recent => _all.Take(5);

    [ObservableProperty] public partial string Filter { get; set; } = "";

    public bool IsEmpty => _all.Count == 0;
    public bool NoFilterResults => _all.Count > 0 && Entries.Count == 0;
    public string CountText => _all.Count == 1 ? "1 link this session" : $"{_all.Count} links this session";

    partial void OnFilterChanged(string value) => Refilter();

    [RelayCommand]
    private void Clear()
    {
        var cleared = _log.Snapshot();
        if (cleared.Count == 0) return;
        _log.Clear();
        Undo.Show(cleared.Count == 1 ? "Cleared 1 link" : $"Cleared {cleared.Count} links", () =>
        {
            // Snapshot is oldest first, so re-adding keeps the order. Links handled since the clear stay newest.
            var since = _log.Snapshot();
            _log.Clear();
            foreach (var e in cleared.Concat(since)) _log.Add(e);
        });
    }

    [RelayCommand] private void OpenSetup() => _windows.ShowMainWindow(AppPage.Settings);
    [RelayCommand] private void ClearFilter() => Filter = "";

    private void Add(ActivityEntry entry)
    {
        var item = new ActivityItemViewModel(entry);
        _all.Insert(0, item);
        if (item.Matches(Filter)) Entries.Insert(0, item);
        NotifyCounts();
    }

    private void Refilter()
    {
        Entries.Clear();
        foreach (var item in _all.Where(i => i.Matches(Filter))) Entries.Add(item);
        NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoFilterResults));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Recent));
    }
}

public sealed class ActivityItemViewModel(ActivityEntry entry) : ViewModelBase
{
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

    // Route line in three runs, so the pattern can be set in mono like everywhere else.
    public string RoutePrefix { get; } = entry.Outcome == ActivityOutcome.Routed ? $"Rule {entry.RuleNumber}  " : "";
    public string RoutePattern { get; } = entry.Outcome == ActivityOutcome.Routed ? entry.RulePattern ?? "" : "";

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
        ActivityOutcome.Routed => $"Rule {entry.RuleNumber}  {entry.RulePattern}  →  {entry.BrowserName}",
        ActivityOutcome.Chosen => $"You chose {entry.BrowserName}",
        ActivityOutcome.PassedThrough => $"Routing off  →  {entry.BrowserName}",
        ActivityOutcome.Asked => "No rule matched, so the chooser opened",
        _ => entry.Detail ?? "",
    };

    // One lamp per outcome: amber only when a rule set the route.
    public bool IsRouted => Outcome is ActivityOutcome.Routed;
    public bool IsChosen => Outcome is ActivityOutcome.Chosen;
    public bool IsAsked => Outcome is ActivityOutcome.Asked or ActivityOutcome.PassedThrough;
    public bool IsProblem => Outcome is ActivityOutcome.Rejected or ActivityOutcome.Failed;

    public string Summary => $"{Time}  {OutcomeLabel}  {Host}";

    internal bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        Url.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        RouteText.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
