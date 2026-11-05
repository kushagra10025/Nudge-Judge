using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

/// <summary>
/// The fallback chooser. Unmatched links queue up as tabs; picking a browser opens the
/// current tab's link and, depending on <see cref="Remember"/>, turns the choice into a rule.
/// </summary>
public sealed partial class ChooserViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly LinkRouter _router;
    private readonly ISystemBoot _boot;
    private readonly TimeProvider _time;
    private IWindowService? _windows;
    private DateTimeOffset _lastArrival = DateTimeOffset.MinValue;

    public ChooserViewModel(IConfigStore config, LinkRouter router, ISystemBoot boot, IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        _config = config;
        _router = router;
        _boot = boot;
        _time = time ?? TimeProvider.System;
        config.Changed += (_, _) => dispatcher.Post(Reload);
        Reload();
        Remember = RememberOptions[0];
    }

    /// <summary>Set once by the composition root (the window service depends on this view model).</summary>
    public void Attach(IWindowService windows) => _windows = windows;

    public ObservableCollection<ChooserTabViewModel> Tabs { get; } = [];
    public ObservableCollection<BrowserChoiceViewModel> Browsers { get; } = [];
    public ObservableCollection<RememberOption> RememberOptions { get; } = [];
    public IReadOnlyList<MatchKindOption> Kinds => MatchKindOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ChooserTabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavesRule))]
    public partial RememberOption Remember { get; set; }

    public bool HasSelection => SelectedTab is not null;
    public bool SavesRule => Remember.SavesRule;
    public bool HasMultipleTabs => Tabs.Count > 1;
    public bool HasBrowsers => Browsers.Count > 0;

    /// <summary>Imported browsers left out until reviewed on the Browsers page.</summary>
    public int HiddenForReview { get; private set; }
    public bool HasHiddenForReview => HiddenForReview > 0;
    public string HiddenForReviewText => HiddenForReview == 1
        ? "1 imported browser is hidden until you review it on the Browsers page."
        : $"{HiddenForReview} imported browsers are hidden until you review them on the Browsers page.";
    public string WaitingText => Tabs.Count switch { 0 => "", 1 => "1 link waiting", var n => $"{n} links waiting" };

    /// <summary>Adds an unmatched link. Links arriving in a burst queue behind the current tab instead of stealing focus.</summary>
    public void Enqueue(Uri url, SourceApp? source = null)
    {
        var existing = Tabs.FirstOrDefault(t => t.Url == url);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var now = _time.GetUtcNow();
        var grouping = TimeSpan.FromSeconds(_config.Current.Settings.ChooserGroupingSeconds);
        var inBurst = Tabs.Count > 0 && now - _lastArrival <= grouping;
        _lastArrival = now;

        var tab = new ChooserTabViewModel(this, url, now, source);
        Tabs.Add(tab);
        if (!inBurst || SelectedTab is null) SelectedTab = tab;
        NotifyTabs();
    }

    [RelayCommand]
    private void Choose(BrowserChoiceViewModel? choice)
    {
        if (choice is null || SelectedTab is not { } tab) return;
        var browser = _config.Current.FindBrowser(choice.Id);
        if (browser is null) return;

        if (Remember.SavesRule && !tab.ValidatePattern()) return;

        var result = _router.OpenChosen(tab.Url, browser, tab.Source);
        if (result.Kind != RouteKind.Launched)
        {
            tab.Error = result.Message;
            return;
        }

        if (Remember.SavesRule)
        {
            TimeSpan? lifetime = Remember.Kind == RememberKind.Hours
                ? TimeSpan.FromHours(_config.Current.Settings.TemporaryRuleHours)
                : null;
            _router.AddRule(tab.ToRule(browser.Id), lifetime, Remember.Kind == RememberKind.UntilRestart, _boot);
        }

        Remove(tab);
    }

    [RelayCommand]
    private void ChooseNumber(int number)
    {
        if (number >= 1 && number <= Browsers.Count) Choose(Browsers[number - 1]);
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (SelectedTab is { } tab) Remove(tab);
    }

    [RelayCommand]
    private void DismissAll()
    {
        Tabs.Clear();
        SelectedTab = null;
        NotifyTabs();
        _windows?.HideChooser();
    }

    [RelayCommand]
    private void OpenBrowserSettings() => _windows?.ShowMainWindow(AppPage.Browsers);

    [RelayCommand]
    private void NextTab() => Step(+1);

    [RelayCommand]
    private void PreviousTab() => Step(-1);

    internal void Remove(ChooserTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        NotifyTabs();
        if (Tabs.Count == 0) _windows?.HideChooser();
    }

    private void Step(int delta)
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        var i = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[i];
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(HasMultipleTabs));
        OnPropertyChanged(nameof(WaitingText));
    }

    private void Reload()
    {
        var cfg = _config.Current;
        Browsers.Clear();
        // Imported browsers awaiting review aren't offered: picking one would run a program nobody has looked at.
        var usable = cfg.Browsers.Where(b => !b.NeedsReview).ToList();
        for (var i = 0; i < usable.Count; i++)
            Browsers.Add(new BrowserChoiceViewModel(usable[i], i + 1));
        OnPropertyChanged(nameof(HasBrowsers));
        HiddenForReview = cfg.Browsers.Count - usable.Count;
        OnPropertyChanged(nameof(HasHiddenForReview));
        OnPropertyChanged(nameof(HiddenForReviewText));

        var kind = Remember?.Kind ?? RememberKind.Once;
        RememberOptions.Clear();
        RememberOptions.Add(new(RememberKind.Once, "Just this once"));
        RememberOptions.Add(new(RememberKind.Hours, $"For {cfg.Settings.TemporaryRuleHours} hours"));
        RememberOptions.Add(new(RememberKind.UntilRestart, "Until I restart"));
        RememberOptions.Add(new(RememberKind.Always, "Always (save rule)"));
        Remember = RememberOptions.First(o => o.Kind == kind);
    }
}

public sealed partial class ChooserTabViewModel : ViewModelBase
{
    private readonly ChooserViewModel _owner;

    public ChooserTabViewModel(ChooserViewModel owner, Uri url, DateTimeOffset arrived, SourceApp? source = null)
    {
        _owner = owner;
        Url = url;
        Arrived = arrived;
        Source = source;
        Kind = MatchKindOption.All[0];
        Pattern = PatternCompiler.SuggestWildcard(url);
    }

    public Uri Url { get; }
    public DateTimeOffset Arrived { get; }
    public string Host => Url.Host;
    public string Scheme => Url.Scheme + "://";
    public string Rest => Url.IsDefaultPort ? Url.PathAndQuery + Url.Fragment : $":{Url.Port}{Url.PathAndQuery}{Url.Fragment}";
    public string FullUrl => Url.AbsoluteUri;
    public bool IsInsecure => Url.Scheme == Uri.UriSchemeHttp;

    /// <summary>The app the link came from, when known.</summary>
    public SourceApp? Source { get; }
    public bool HasSource => Source is not null;
    public string OriginText => Source is { } s ? $"No rule matched this link from {s.DisplayName}" : "No rule matched";
    public string OnlyFromSourceLabel => Source is { } s ? $"Only links from {s.DisplayName}" : "";

    /// <summary>Saving a rule: limit it to links from <see cref="Source"/> as well.</summary>
    [ObservableProperty] public partial bool OnlyFromSource { get; set; }

    [ObservableProperty] public partial MatchKindOption Kind { get; set; }
    [ObservableProperty] public partial string Pattern { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatternError))]
    public partial string? PatternError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasPatternError => PatternError is not null;
    public bool HasError => Error is not null;

    partial void OnPatternChanged(string value) => PatternError = null;
    partial void OnKindChanged(MatchKindOption value) => PatternError = null;

    [RelayCommand] private void Close() => _owner.Remove(this);

    /// <summary>The rule the chooser saves: this tab's pattern, plus the source app when asked for.</summary>
    internal Rule ToRule(Guid browserId)
    {
        var fromSource = OnlyFromSource && Source is not null;
        var rule = new Rule
        {
            Name = RuleNaming.Suggest(Pattern, fromSource ? Source!.DisplayName : null),
            Conditions = [RuleCondition.Link(Kind.Kind, Pattern.Trim())],
            BrowserId = browserId,
        };
        if (fromSource) rule.Conditions.Add(RuleCondition.Source(Source!.Name));
        return rule;
    }

    internal bool ValidatePattern()
    {
        var compiled = PatternCompiler.Compile(Kind.Kind, Pattern);
        if (!compiled.Success)
        {
            PatternError = compiled.Error;
            return false;
        }
        if (!compiled.Pattern!.IsMatch(Url))
        {
            PatternError = "This rule wouldn't match the link you're opening.";
            return false;
        }
        PatternError = null;
        return true;
    }
}

public sealed class BrowserChoiceViewModel(BrowserProfile browser, int number) : ViewModelBase
{
    public Guid Id { get; } = browser.Id;
    public int Number { get; } = number;
    public string Key => Number <= 9 ? Number.ToString() : "";
    public bool HasKey => Number <= 9;
    public string Name { get; } = browser.Name;
    public string Detail { get; } = BrowserOption.BrowserDetail(browser);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool IsPrivate { get; } = browser.IsPrivate;
}
