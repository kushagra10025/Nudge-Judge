using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;
using NudgeJudge.Core.Security;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

public sealed partial class RulesViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly RuleEngine _engine;
    private readonly ISystemBoot _boot;
    private readonly ChooserViewModel _chooser;
    private readonly IWindowService _windows;

    public RulesViewModel(IConfigStore config, RuleEngine engine, ISystemBoot boot, ChooserViewModel chooser,
        IWindowService windows, IUiDispatcher dispatcher, SettingsViewModel settings)
    {
        Settings = settings;
        Undo = new UndoBar(dispatcher);
        _config = config;
        _engine = engine;
        _boot = boot;
        _chooser = chooser;
        _windows = windows;
        // The editor only compares against the test link while the tester is showing it.
        Editor = new RuleEditorViewModel(config, engine, Browsers, () => IsTesterOpen ? TestUrl : "");
        config.Changed += (_, _) => dispatcher.Post(Reload);
        Reload();
    }

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];
    public ObservableCollection<BrowserOption> Browsers { get; } = [];
    public RuleEditorViewModel Editor { get; }
    public UndoBar Undo { get; }

    /// <summary>For the setup checklist's last step (is NudgeJudge the default browser?).</summary>
    public SettingsViewModel Settings { get; }

    public bool HasRules => Rules.Count > 0;
    public bool HasBrowsers => Browsers.Count > 0;

    /// <summary>First run: nothing configured yet and the user hasn't picked samples or an empty start.</summary>
    public bool ShowWelcome => _config.Current.Settings.OfferSamples && !HasRules && !HasBrowsers;

    /// <summary>No rules yet: a three-step checklist (browsers, first rule, default browser) instead of a blank page.</summary>
    public bool ShowSetup => !HasRules && !ShowWelcome;

    public bool NeedsBrowsers => !HasBrowsers;
    public string BrowsersStepDetail => Browsers.Count switch
    {
        0 => "Rules send links to one of these, so they come first.",
        1 => "1 browser ready.",
        var n => $"{n} browsers ready.",
    };

    /// <summary>Routing is off but there are rules: say so above the list, which otherwise looks live.</summary>
    public bool ShowRoutingOff => HasRules && RoutingOffNotice is not null;

    public bool HasSampleRules => _config.Current.Rules.Any(r => r.IsSample == true);

    /// <summary>The tester is secondary: hidden until asked for, and only useful once there are rules.</summary>
    public bool ShowTester => IsTesterOpen && HasRules;

    /// <summary>Raised when the user asks to look for installed browsers from the Rules page.</summary>
    public event EventHandler? FindBrowsersRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTester))]
    public partial bool IsTesterOpen { get; set; }

    [ObservableProperty] public partial string TestUrl { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<RouteStop> TestStops { get; set; } = [];
    [ObservableProperty] public partial string? TestDestination { get; set; }
    [ObservableProperty] public partial bool TestIdle { get; set; }
    [ObservableProperty] public partial bool TestMatched { get; set; }
    [ObservableProperty] public partial bool TestInvalid { get; set; }
    [ObservableProperty] public partial string TestHeadline { get; set; } = "";
    [ObservableProperty] public partial string TestDetail { get; set; } = "";

    /// <summary>Set when routing is switched off, so the tester doesn't imply rules are in effect.</summary>
    [ObservableProperty] public partial string? RoutingOffNotice { get; set; }

    partial void OnTestUrlChanged(string value)
    {
        RunTest();
        SendTestToChooserCommand.NotifyCanExecuteChanged();
        Editor.RefreshTestVerdict();
    }

    partial void OnIsTesterOpenChanged(bool value)
    {
        RunTest();
        Editor.RefreshTestVerdict();
    }

    [RelayCommand]
    private void ToggleTester() => IsTesterOpen = !IsTesterOpen;

    [RelayCommand]
    private void NewRule()
    {
        if (_config.Current.Settings.OfferSamples) StartEmpty();
        var url = IsTesterOpen ? UrlValidator.Validate(TestUrl).Url : null;
        var suggestion = url is not null && !TestMatched ? PatternCompiler.SuggestWildcard(url) : "";
        Editor.OpenNew(suggestion, Browsers.FirstOrDefault()?.Id);
        SyncEditingFlags();
    }

    /// <summary>
    /// "Create rule" from an Activity entry: the editor opens with a pattern suggested from the link and the
    /// browser it went to, and the tester shows that link so the editor can say whether the pattern catches it.
    /// </summary>
    public void NewRuleFrom(RuleFromActivity source)
    {
        if (_config.Current.Settings.OfferSamples) StartEmpty();
        TestUrl = source.Url.AbsoluteUri;
        IsTesterOpen = true;
        var browser = Browsers.FirstOrDefault(b => b.Id == source.BrowserId)
                      ?? Browsers.FirstOrDefault(b => b.Name == source.BrowserName);
        Editor.OpenNew(PatternCompiler.SuggestWildcard(source.Url), browser?.Id);
        SyncEditingFlags();
    }

    [RelayCommand]
    private void LoadSamples() => _config.Update(c =>
    {
        c.Settings.OfferSamples = false;
        SampleData.AddTo(c);
    });

    [RelayCommand]
    private void StartEmpty() => _config.Update(c => c.Settings.OfferSamples = false);

    [RelayCommand]
    private void RemoveSamples()
    {
        if (Editor.EditingId is { } id && _config.Current.Rules.Find(r => r.Id == id)?.IsSample == true) Editor.Close();
        _config.Update(c => SampleData.RemoveSamples(c));
    }

    [RelayCommand]
    private void TurnOnRouting() => _config.Update(c => c.Settings.RoutingEnabled = true);

    [RelayCommand]
    private void OpenSetup() => _windows.ShowMainWindow(AppPage.Settings);

    [RelayCommand]
    private void FindBrowsers()
    {
        if (_config.Current.Settings.OfferSamples) StartEmpty();
        FindBrowsersRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanSendTestToChooser))]
    private void SendTestToChooser()
    {
        if (UrlValidator.Validate(TestUrl).Url is not { } url) return;
        _chooser.Enqueue(url);
        _windows.ShowChooser();
    }

    private bool CanSendTestToChooser() => UrlValidator.Validate(TestUrl).IsValid;

    internal void Edit(RuleItemViewModel item)
    {
        var rule = _config.Current.Rules.Find(r => r.Id == item.Id);
        if (rule is null) return;
        Editor.OpenExisting(rule);
        SyncEditingFlags();
    }

    internal void Delete(RuleItemViewModel item)
    {
        if (Editor.EditingId == item.Id) Editor.Close();
        var index = _config.Current.Rules.FindIndex(r => r.Id == item.Id);
        if (index < 0) return;
        var removed = _config.Current.Rules[index].Clone();
        _config.Update(c => c.Rules.RemoveAt(index));
        Undo.Show($"Deleted rule {index + 1}  ·  {removed.Pattern}", () => _config.Update(c =>
        {
            if (c.Rules.Any(r => r.Id == removed.Id)) return;
            c.Rules.Insert(Math.Min(index, c.Rules.Count), removed);
        }));
    }

    internal void Move(RuleItemViewModel item, int delta) => _config.Update(c =>
    {
        var i = c.Rules.FindIndex(r => r.Id == item.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= c.Rules.Count) return;
        (c.Rules[i], c.Rules[j]) = (c.Rules[j], c.Rules[i]);
    });

    internal void SetEnabled(RuleItemViewModel item, bool enabled) => _config.Update(c =>
    {
        if (c.Rules.Find(r => r.Id == item.Id) is { } rule) rule.Enabled = enabled;
    });

    private void Reload()
    {
        var cfg = _config.Current;

        Browsers.Clear();
        foreach (var b in cfg.Browsers) Browsers.Add(BrowserOption.From(b));

        Rules.Clear();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < cfg.Rules.Count; i++)
        {
            var rule = cfg.Rules[i];
            var browser = cfg.FindBrowser(rule.BrowserId);
            var compiled = _engine.Compile(rule);
            Rules.Add(new RuleItemViewModel(this, rule, i + 1, cfg.Rules.Count,
                browser?.Name, compiled.Error, ExpiryText(rule, now), rule.IsExpired(now, _boot)));
        }

        RoutingOffNotice = cfg.Settings.RoutingEnabled
            ? null
            : cfg.FindBrowser(cfg.Settings.PrimaryBrowserId) is { } primary
                ? $"Routing is off, so these rules are paused. Every link opens in {primary.Name}."
                : "Routing is off, so these rules are paused. Every link goes to the chooser.";

        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasBrowsers));
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowSetup));
        OnPropertyChanged(nameof(NeedsBrowsers));
        OnPropertyChanged(nameof(BrowsersStepDetail));
        OnPropertyChanged(nameof(ShowRoutingOff));
        OnPropertyChanged(nameof(HasSampleRules));
        OnPropertyChanged(nameof(ShowTester));
        Editor.SyncBrowsers();
        SyncEditingFlags();
        RunTest();
    }

    private void SyncEditingFlags()
    {
        foreach (var r in Rules) r.IsEditing = Editor.IsOpen && Editor.EditingId == r.Id;
    }

    private void RunTest()
    {
        var rules = _config.Current.Rules;
        var validation = UrlValidator.Validate(TestUrl);
        foreach (var r in Rules) r.IsTestMatch = false;
        // A closed tester shouldn't leave a rule lit up with no explanation.
        if (!IsTesterOpen) return;

        if (!validation.IsValid)
        {
            TestIdle = true;
            TestMatched = false;
            TestInvalid = !string.IsNullOrWhiteSpace(TestUrl);
            TestDestination = null;
            TestStops = rules.Select((_, i) => new RouteStop((i + 1).ToString(), RouteStopState.Ahead)).ToList();
            TestHeadline = TestInvalid ? validation.Error! : "Paste a link to see which rule catches it.";
            TestDetail = TestInvalid ? "Only http and https links are routed." : "";
            return;
        }

        var result = _engine.Match(validation.Url!, rules);
        TestStops = result.Evaluations.Select((e, i) => new RouteStop((i + 1).ToString(), e.State switch
        {
            RuleEvaluationState.Matched => RouteStopState.Matched,
            RuleEvaluationState.NoMatch => RouteStopState.Passed,
            RuleEvaluationState.Inactive => RouteStopState.Inactive,
            RuleEvaluationState.Error => RouteStopState.Error,
            _ => RouteStopState.Ahead,
        })).ToList();

        TestIdle = false;
        TestInvalid = false;
        TestMatched = result.IsMatch;
        var timing = $"{result.Elapsed.TotalMilliseconds:0.00} ms";

        if (result.Rule is { } rule)
        {
            var browser = _config.Current.FindBrowser(rule.BrowserId)?.Name ?? "a missing browser";
            TestDestination = browser;
            TestHeadline = $"Rule {result.RuleIndex + 1} opens this in {browser}";
            TestDetail = $"{MatchKindOption.For(rule.MatchKind).Label}  {rule.Pattern}  ·  matched in {timing}";
            if (result.RuleIndex < Rules.Count) Rules[result.RuleIndex].IsTestMatch = true;
        }
        else
        {
            TestDestination = null;
            TestHeadline = "No rule matches, so NudgeJudge will ask";
            TestDetail = rules.Count == 0 ? "Add a rule to route links like this automatically." : $"Checked {rules.Count} rules in {timing}";
        }
    }

    internal static string? ExpiryText(Rule rule, DateTimeOffset now)
    {
        if (rule.ExpiresAfterBootId is not null) return "Until restart";
        if (rule.ExpiresAt is not { } at) return null;
        var left = at - now;
        if (left <= TimeSpan.Zero) return "Expired";
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} m left" : $"{Math.Max(1, left.Minutes)} m left";
    }
}

public sealed partial class RuleItemViewModel : ViewModelBase
{
    private readonly RulesViewModel _owner;
    private readonly bool _loading = true;

    public RuleItemViewModel(RulesViewModel owner, Rule rule, int number, int count, string? browserName,
        string? patternError, string? expiryText, bool isExpired)
    {
        _owner = owner;
        Id = rule.Id;
        Number = number;
        Kind = rule.MatchKind;
        KindLabel = MatchKindOption.For(rule.MatchKind).Label;
        Pattern = rule.Pattern;
        Name = rule.Name;
        BrowserName = browserName ?? "Missing browser";
        BrowserMissing = browserName is null;
        PatternError = patternError;
        ExpiryText = expiryText;
        IsExpired = isExpired;
        IsSample = rule.IsSample == true;
        CanMoveUp = number > 1;
        CanMoveDown = number < count;
        IsEnabled = rule.Enabled;
        _loading = false;
    }

    public Guid Id { get; }
    public int Number { get; }
    public MatchKind Kind { get; }
    public string KindLabel { get; }
    public string Pattern { get; }
    public string Name { get; }
    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public string NameSuffix => HasName ? "  ·  " + Name : "";
    public string BrowserName { get; }
    public bool BrowserMissing { get; }
    public string? PatternError { get; }
    public bool HasProblem => PatternError is not null || BrowserMissing;
    public string? Problem => PatternError ?? (BrowserMissing ? "Its browser was removed." : null);
    public string? ExpiryText { get; }
    public bool IsTemporary => ExpiryText is not null;
    public bool IsSample { get; }
    public bool HasBadges => IsTemporary || IsSample;
    public bool HasPatternError => PatternError is not null;
    public bool ShowBrowserFix => BrowserMissing && PatternError is null;
    public bool IsExpired { get; }
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }
    public bool IsDimmed => !IsEnabled || IsExpired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty] public partial bool IsTestMatch { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading) _owner.SetEnabled(this, value);
    }

    [RelayCommand] private void Edit() => _owner.Edit(this);
    [RelayCommand] private void Delete() => _owner.Delete(this);
    [RelayCommand] private void MoveUp() => _owner.Move(this, -1);
    [RelayCommand] private void MoveDown() => _owner.Move(this, +1);
}
