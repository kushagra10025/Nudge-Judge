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
    public string WaitingText => Tabs.Count switch { 0 => "", 1 => "1 link waiting", var n => $"{n} links waiting" };

    /// <summary>Adds an unmatched link. Links arriving in a burst queue behind the current tab instead of stealing focus.</summary>
    public void Enqueue(Uri url)
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

        var tab = new ChooserTabViewModel(this, url, now);
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

        var result = _router.OpenChosen(tab.Url, browser);
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
            _router.AddRule(tab.Kind.Kind, tab.Pattern, browser.Id, lifetime, Remember.Kind == RememberKind.UntilRestart, _boot);
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
        for (var i = 0; i < cfg.Browsers.Count; i++)
            Browsers.Add(new BrowserChoiceViewModel(cfg.Browsers[i], i + 1));
        OnPropertyChanged(nameof(HasBrowsers));

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

    public ChooserTabViewModel(ChooserViewModel owner, Uri url, DateTimeOffset arrived)
    {
        _owner = owner;
        Url = url;
        Arrived = arrived;
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
