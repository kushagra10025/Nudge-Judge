using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.ViewModels;

/// <summary>
/// The rule editor: a required name, conditions joined by all/any, and the browser to open in.
/// Validates as you type and, while the tester is open, says whether the rule catches its link.
/// </summary>
public sealed partial class RuleEditorViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly RuleEngine _engine;
    private readonly Func<LinkContext?> _testLink;
    private DateTimeOffset? _expiresAt;
    private string? _expiresAfterBootId;
    private bool _autoName;
    private bool _settingName;
    private bool _loading;

    public RuleEditorViewModel(IConfigStore config, RuleEngine engine, ObservableCollection<BrowserOption> browsers, Func<LinkContext?> testLink)
    {
        _config = config;
        _engine = engine;
        _testLink = testLink;
        Browsers = browsers;
        Join = JoinOption.All[0];
        Conditions.CollectionChanged += (_, _) => OnConditionsChanged();
    }

    public IReadOnlyList<JoinOption> Joins => JoinOption.All;
    public ObservableCollection<BrowserOption> Browsers { get; }
    public ObservableCollection<ConditionEditorViewModel> Conditions { get; } = [];

    /// <summary>Program names seen in Activity, offered when typing a source app.</summary>
    public ObservableCollection<string> KnownApps { get; } = [];

    public Guid? EditingId { get; private set; }

    /// <summary>Where the editor gets known apps from (Activity). Set by the Rules page.</summary>
    public Func<IEnumerable<SourceApp>>? KnownSources { get; set; }

    /// <summary>The app to prefill when adding a source condition (from an Activity entry or the tester).</summary>
    public SourceApp? SuggestedSource { get; set; }

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsNew { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    public partial string? NameError { get; set; }

    [ObservableProperty] public partial JoinOption Join { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial BrowserOption? Browser { get; set; }

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(HasRuleError))]
    public partial string? RuleError { get; set; }

    [ObservableProperty] public partial string? TestVerdict { get; set; }
    [ObservableProperty] public partial bool TestVerdictMatches { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTemporary))]
    public partial string? ExpiryText { get; set; }

    public string Title => IsNew ? "New rule" : "Edit rule";
    public bool HasNameError => NameError is not null;
    public bool HasRuleError => RuleError is not null;
    public bool IsTemporary => ExpiryText is not null;
    public bool HasNoBrowsers => Browsers.Count == 0;

    /// <summary>The all/any choice only means something with two or more conditions.</summary>
    public bool ShowJoin => Conditions.Count > 1;
    public bool HasNoConditions => Conditions.Count == 0;
    public bool HasSourceCondition => Conditions.Any(c => c.IsSource);

    partial void OnJoinChanged(JoinOption value)
    {
        foreach (var c in Conditions) c.UpdateJoin(value.Join);
        Validate();
    }

    partial void OnNameChanged(string value)
    {
        if (_settingName || _loading) return;
        _autoName = false;
        NameError = string.IsNullOrWhiteSpace(value) ? "Give the rule a name. It's how the rule shows in the list and in Activity." : null;
    }

    public void OpenNew(string pattern, Guid? browserId)
    {
        _loading = true;
        EditingId = null;
        IsNew = true;
        Join = JoinOption.All[0];
        Conditions.Clear();
        Conditions.Add(NewLinkCondition(MatchKind.Wildcard, pattern));
        Browser = Browsers.FirstOrDefault(b => b.Id == browserId) ?? Browsers.FirstOrDefault();
        IsEnabled = true;
        _expiresAt = null;
        _expiresAfterBootId = null;
        ExpiryText = null;
        NameError = null;
        _autoName = true;
        _loading = false;
        RefreshKnownApps();
        Validate();
        IsOpen = true;
    }

    public void OpenExisting(Rule rule)
    {
        _loading = true;
        EditingId = rule.Id;
        IsNew = false;
        Name = rule.Name;
        Join = JoinOption.For(rule.Join);
        Conditions.Clear();
        foreach (var c in rule.Conditions) Conditions.Add(ConditionEditorViewModel.From(this, c));
        Browser = Browsers.FirstOrDefault(b => b.Id == rule.BrowserId);
        IsEnabled = rule.Enabled;
        _expiresAt = rule.ExpiresAt;
        _expiresAfterBootId = rule.ExpiresAfterBootId;
        ExpiryText = RulesViewModel.ExpiryText(rule, DateTimeOffset.UtcNow);
        NameError = null;
        _autoName = false;
        _loading = false;
        RefreshKnownApps();
        Validate();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        EditingId = null;
        SuggestedSource = null;
    }

    /// <summary>Re-resolves the selected browser after the browser list was rebuilt.</summary>
    public void SyncBrowsers()
    {
        if (Browser is { } b) Browser = Browsers.FirstOrDefault(o => o.Id == b.Id);
        OnPropertyChanged(nameof(HasNoBrowsers));
    }

    public void RefreshTestVerdict() => Validate();

    [RelayCommand]
    private void AddLinkCondition() => Conditions.Add(NewLinkCondition(MatchKind.Wildcard, ""));

    [RelayCommand]
    private void AddSourceCondition()
    {
        RefreshKnownApps();
        var condition = new ConditionEditorViewModel(this, ConditionField.SourceApp);
        condition.Values.Add(new ConditionValueViewModel(condition, SuggestedSource?.Name ?? ""));
        Conditions.Add(condition);
    }

    [RelayCommand]
    private void MakePermanent()
    {
        _expiresAt = null;
        _expiresAfterBootId = null;
        ExpiryText = null;
    }

    [RelayCommand]
    private void Cancel() => Close();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Validate();
        if (!CanSave()) return;

        var id = EditingId;
        var conditions = Conditions.Select(c => c.ToModel()).ToList();
        _config.Update(c =>
        {
            var rule = id is { } existing ? c.Rules.Find(r => r.Id == existing) : null;
            if (rule is null)
            {
                rule = new Rule();
                c.Rules.Add(rule);
            }
            rule.Name = Name.Trim();
            rule.Join = Join.Join;
            rule.Conditions = conditions;
            rule.BrowserId = Browser!.Id;
            rule.Enabled = IsEnabled;
            rule.ExpiresAt = _expiresAt;
            rule.ExpiresAfterBootId = _expiresAfterBootId;
            // Saving a sample makes it the user's own, so cleaning up samples leaves it alone.
            rule.IsSample = false;
        });
        Close();
    }

    private bool CanSave() =>
        Browser is not null && !string.IsNullOrWhiteSpace(Name) && RuleError is null &&
        Conditions.Count > 0 && Conditions.All(c => c.HasAnyValue && !c.HasValueErrors);

    internal void Remove(ConditionEditorViewModel condition) => Conditions.Remove(condition);

    /// <summary>Something in a condition changed.</summary>
    internal void ConditionChanged()
    {
        if (!_loading) Validate();
    }

    private ConditionEditorViewModel NewLinkCondition(MatchKind kind, string pattern)
    {
        var condition = new ConditionEditorViewModel(this, ConditionField.Link) { Kind = MatchKindOption.For(kind) };
        condition.Values.Add(new ConditionValueViewModel(condition, pattern));
        return condition;
    }

    private void OnConditionsChanged()
    {
        for (var i = 0; i < Conditions.Count; i++)
        {
            Conditions[i].IsFirst = i == 0;
            Conditions[i].IsLast = i == Conditions.Count - 1;
            Conditions[i].UpdateJoin(Join.Join);
        }
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(HasNoConditions));
        OnPropertyChanged(nameof(HasSourceCondition));
        if (!_loading) Validate();
    }

    private void RefreshKnownApps()
    {
        var names = new List<string>();
        if (SuggestedSource is { } s) names.Add(s.Name);
        if (KnownSources?.Invoke() is { } known) names.AddRange(known.Select(a => a.Name));
        KnownApps.Clear();
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase)) KnownApps.Add(n);
    }

    private Rule Draft() => new()
    {
        Name = Name,
        Join = Join.Join,
        Conditions = Conditions.Select(c => c.ToModel()).ToList(),
    };

    private void Validate()
    {
        var draft = Draft();
        var compiled = _engine.Compile(draft);

        for (var i = 0; i < Conditions.Count && i < compiled.Conditions.Count; i++)
            Conditions[i].ApplyErrors(compiled.Conditions[i]);

        // Blank conditions are "not yet", not errors: Save stays disabled until they're filled in.
        RuleError = Conditions.Count == 0 ? "Add a condition: a link pattern, the app it was opened from, or both." : null;
        SaveCommand.NotifyCanExecuteChanged();

        if (_autoName) SuggestName();

        if (!compiled.Success || _testLink() is not { } link)
        {
            TestVerdict = null;
            return;
        }

        try
        {
            TestVerdictMatches = compiled.Rule!.IsMatch(link);
            var from = link.Source is { } app ? $" from {app.DisplayName}" : "";
            TestVerdict = TestVerdictMatches ? $"Matches the test link{from}" : $"Doesn't match the test link{from}";
        }
        catch (RegexMatchTimeoutException)
        {
            TestVerdictMatches = false;
            TestVerdict = "Took too long to match and would be skipped";
        }
    }

    private void SuggestName()
    {
        var link = Conditions.FirstOrDefault(c => c.IsLink && !c.IsNegated)?.FirstValue;
        var app = Conditions.FirstOrDefault(c => c.IsSource && !c.IsNegated)?.FirstValue;
        var known = app is null ? null : KnownSources?.Invoke().FirstOrDefault(a => string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase));
        _settingName = true;
        Name = RuleNaming.Suggest(link, known?.DisplayName ?? app);
        _settingName = false;
    }
}

/// <summary>One condition in the editor: what it looks at, how, and its OR'd values.</summary>
public sealed partial class ConditionEditorViewModel : ViewModelBase
{
    private readonly RuleEditorViewModel _owner;

    public ConditionEditorViewModel(RuleEditorViewModel owner, ConditionField field)
    {
        _owner = owner;
        Field = field;
        Kind = MatchKindOption.All[0];
        Operators = field == ConditionField.SourceApp ? OperatorOption.Source : OperatorOption.Link;
        Operator = Operators[0];
        Values.CollectionChanged += (_, _) =>
        {
            for (var i = 0; i < Values.Count; i++)
            {
                Values[i].CanRemove = Values.Count > 1;
                Values[i].IsFirst = i == 0;
            }
            _owner.ConditionChanged();
        };
    }

    public static ConditionEditorViewModel From(RuleEditorViewModel owner, RuleCondition model)
    {
        var c = new ConditionEditorViewModel(owner, model.Field)
        {
            Kind = MatchKindOption.For(model.MatchKind),
            CaseSensitive = model.CaseSensitive,
        };
        c.Operator = c.Operators.First(o => o.Negate == model.Negate);
        foreach (var v in model.Values.DefaultIfEmpty("")) c.Values.Add(new ConditionValueViewModel(c, v));
        return c;
    }

    public RuleEditorViewModel Owner => _owner;
    public ConditionField Field { get; }
    public bool IsLink => Field == ConditionField.Link;
    public bool IsSource => Field == ConditionField.SourceApp;
    public string Heading => IsLink ? "Link" : "Opened from";
    public IReadOnlyList<MatchKindOption> Kinds => MatchKindOption.All;
    public IReadOnlyList<OperatorOption> Operators { get; }
    public ObservableCollection<ConditionValueViewModel> Values { get; } = [];
    public ObservableCollection<string> KnownApps => _owner.KnownApps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaseSensitive), nameof(Placeholder), nameof(Hint))]
    public partial MatchKindOption Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNegated))]
    public partial OperatorOption Operator { get; set; }

    [ObservableProperty] public partial bool CaseSensitive { get; set; }

    /// <summary>"and" / "or" shown above every condition but the first.</summary>
    [ObservableProperty] public partial string JoinLabel { get; set; } = "and";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJoinLabel))]
    public partial bool IsFirst { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>The track runs on from this condition's glyph down to the next one.</summary>
    [ObservableProperty] public partial bool IsLast { get; set; } = true;

    public bool ShowJoinLabel => !IsFirst;
    public bool IsNegated => Operator.Negate;
    public bool ShowCaseSensitive => IsLink && Kind.Kind is MatchKind.Regex or MatchKind.PathPrefix;
    public bool HasError => Error is not null;
    public bool HasAnyValue => Values.Any(v => !string.IsNullOrWhiteSpace(v.Text));
    public bool HasValueErrors => Values.Any(v => v.HasError);
    public string? FirstValue => Values.Select(v => v.Text.Trim()).FirstOrDefault(t => t.Length > 0);

    public string Placeholder => IsLink ? Kind.Example : "ms-teams.exe";

    public string Hint => IsLink
        ? Kind.Hint
        : "The program's name, with or without .exe, its title, a pattern like *teams*, or a full path.";

    /// <summary>"or…" adds another alternative.</summary>
    public string AddValueLabel => IsLink ? "Or another pattern" : "Or another app";

    partial void OnKindChanged(MatchKindOption value) => _owner.ConditionChanged();
    partial void OnOperatorChanged(OperatorOption value) => _owner.ConditionChanged();
    partial void OnCaseSensitiveChanged(bool value) => _owner.ConditionChanged();

    internal void UpdateJoin(RuleJoin join) => JoinLabel = join == RuleJoin.Any ? "or" : "and";

    internal void ValueChanged() => _owner.ConditionChanged();

    [RelayCommand]
    private void AddValue() => Values.Add(new ConditionValueViewModel(this, ""));

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    internal void RemoveValue(ConditionValueViewModel value)
    {
        if (Values.Count > 1) Values.Remove(value);
    }

    internal void ApplyErrors(ConditionCompileResult result)
    {
        for (var i = 0; i < Values.Count; i++)
            Values[i].Error = i < result.ValueErrors.Count ? result.ValueErrors[i] : null;
    }

    public RuleCondition ToModel() => new()
    {
        Field = Field,
        MatchKind = Kind.Kind,
        CaseSensitive = ShowCaseSensitive && CaseSensitive,
        Negate = Operator.Negate,
        Values = Values.Select(v => v.Text.Trim()).Where(v => v.Length > 0).ToList(),
    };
}

/// <summary>One alternative in a condition (a pattern, or an app name).</summary>
public sealed partial class ConditionValueViewModel : ViewModelBase
{
    private readonly ConditionEditorViewModel _owner;

    public ConditionValueViewModel(ConditionEditorViewModel owner, string text)
    {
        _owner = owner;
        Text = text;
    }

    public ConditionEditorViewModel Condition => _owner;

    [ObservableProperty] public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    [ObservableProperty] public partial bool CanRemove { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOr))]
    public partial bool IsFirst { get; set; } = true;

    public bool HasError => Error is not null;
    public bool ShowOr => !IsFirst;

    partial void OnTextChanged(string value) => _owner.ValueChanged();

    [RelayCommand]
    private void Remove() => _owner.RemoveValue(this);
}
