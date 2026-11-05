using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.NativeMessaging;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.ViewModels;

/// <summary>
/// The rule editor: a required name, conditions joined by all/any (groups of conditions nest up to
/// <see cref="RuleCondition.MaxDepth"/> levels), and the browser to open in. Validates as you type and,
/// while the tester is open, says whether the rule catches its link.
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
    private bool _compiles;

    public RuleEditorViewModel(IConfigStore config, RuleEngine engine, ObservableCollection<BrowserOption> browsers, Func<LinkContext?> testLink)
    {
        _config = config;
        _engine = engine;
        _testLink = testLink;
        Browsers = browsers;
        _loading = true;
        Root = new ConditionGroupViewModel(this, depth: 1, RuleJoin.All);
        _loading = false;
    }

    public ObservableCollection<BrowserOption> Browsers { get; }

    /// <summary>The rule's own level: its join and its conditions and groups.</summary>
    public ConditionGroupViewModel Root { get; }

    /// <summary>The rule's top-level conditions and groups.</summary>
    public ObservableCollection<ConditionNodeViewModel> Conditions => Root.Items;

    /// <summary>Program names seen in Activity, offered when typing a source app.</summary>
    public ObservableCollection<string> KnownApps { get; } = [];

    public Guid? EditingId { get; private set; }

    /// <summary>Set while the editor holds an unsaved clone: the rule it copies, which it's saved below.</summary>
    public Guid? CloneOfId { get; private set; }

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial BrowserOption? Browser { get; set; }

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    /// <summary>The browser extension also catches this rule's links inside browsers (automatic routing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCatchWarning))]
    public partial bool CatchInBrowser { get; set; }

    /// <summary>The conditions can be narrowed to sites, which automatic routing needs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCatchWarning))]
    public partial bool CanCatchInBrowser { get; set; }

    public bool ShowCatchWarning => CatchInBrowser && !CanCatchInBrowser;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(HasRuleError))]
    public partial string? RuleError { get; set; }

    [ObservableProperty] public partial string? TestVerdict { get; set; }
    [ObservableProperty] public partial bool TestVerdictMatches { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTemporary))]
    public partial string? ExpiryText { get; set; }

    public string Title => CloneOfId is not null ? "Clone rule" : IsNew ? "New rule" : "Edit rule";
    public bool HasNameError => NameError is not null;
    public bool HasRuleError => RuleError is not null;
    public bool IsTemporary => ExpiryText is not null;
    public bool HasNoBrowsers => Browsers.Count == 0;

    /// <summary>How the rule's top level is joined.</summary>
    public JoinOption Join
    {
        get => Root.Join;
        set => Root.Join = value;
    }

    /// <summary>The all/any choice only means something with two or more conditions.</summary>
    public bool ShowJoin => Root.ShowJoin;
    public bool HasNoConditions => Root.IsEmpty;
    public bool HasSourceCondition => Root.Leaves().Any(c => c.IsSource);

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
        CloneOfId = null;
        IsNew = true;
        Root.Load(RuleJoin.All, [RuleCondition.Link(MatchKind.Wildcard, pattern)]);
        Browser = Browsers.FirstOrDefault(b => b.Id == browserId) ?? Browsers.FirstOrDefault();
        IsEnabled = true;
        CatchInBrowser = false;
        _expiresAt = null;
        _expiresAfterBootId = null;
        ExpiryText = null;
        NameError = null;
        _autoName = true;
        _loading = false;
        RefreshKnownApps();
        StructureChanged();
        IsOpen = true;
    }

    /// <summary>
    /// Opens an unsaved copy of <paramref name="original"/>. Nothing is added until Save, which puts the copy
    /// just below the original; Cancel leaves no trace.
    /// </summary>
    public void OpenClone(Rule original, IEnumerable<string> takenNames)
    {
        var copy = original.CloneAsNew(takenNames);
        copy.ExpiresAt = null;
        copy.ExpiresAfterBootId = null;
        OpenExisting(copy);
        EditingId = null;
        CloneOfId = original.Id;
        IsNew = true;
        OnPropertyChanged(nameof(Title));
    }

    public void OpenExisting(Rule rule)
    {
        _loading = true;
        CloneOfId = null;
        EditingId = rule.Id;
        IsNew = false;
        Name = rule.Name;
        Root.Load(rule.Join, rule.Conditions);
        Browser = Browsers.FirstOrDefault(b => b.Id == rule.BrowserId);
        IsEnabled = rule.Enabled;
        CatchInBrowser = rule.CatchInBrowser;
        _expiresAt = rule.ExpiresAt;
        _expiresAfterBootId = rule.ExpiresAfterBootId;
        ExpiryText = RulesViewModel.ExpiryText(rule, DateTimeOffset.UtcNow);
        NameError = null;
        _autoName = false;
        _loading = false;
        RefreshKnownApps();
        StructureChanged();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        EditingId = null;
        CloneOfId = null;
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
        var cloneOf = CloneOfId;
        var join = Root.Join.Join;
        var conditions = Root.Items.Select(c => c.ToModel()).ToList();
        _config.Update(c =>
        {
            var rule = id is { } existing ? c.Rules.Find(r => r.Id == existing) : null;
            if (rule is null)
            {
                rule = new Rule();
                // A clone goes just below its original (or last, if the original went meanwhile).
                var at = cloneOf is { } o ? c.Rules.FindIndex(r => r.Id == o) : -1;
                if (at >= 0)
                {
                    rule.ClonedFromId = cloneOf;
                    c.Rules.Insert(at + 1, rule);
                }
                else c.Rules.Add(rule);
            }
            rule.Name = Name.Trim();
            rule.Join = join;
            rule.Conditions = conditions;
            rule.BrowserId = Browser!.Id;
            rule.Enabled = IsEnabled;
            rule.CatchInBrowser = CatchInBrowser;
            rule.ExpiresAt = _expiresAt;
            rule.ExpiresAfterBootId = _expiresAfterBootId;
            // Saving a sample makes it the user's own, so cleaning up samples leaves it alone.
            rule.IsSample = false;
            // A clone stops being a duplicate for good once it, or its original, does something different.
            if (rule.ClonedFromId is { } from && (c.Rules.Find(r => r.Id == from) is not { } original || !rule.RoutesLike(original)))
                rule.ClonedFromId = null;
            foreach (var clone in c.Rules)
                if (clone.ClonedFromId == rule.Id && !clone.RoutesLike(rule)) clone.ClonedFromId = null;
        });
        Close();
    }

    private bool CanSave() =>
        Browser is not null && !string.IsNullOrWhiteSpace(Name) && RuleError is null && _compiles && Root.IsComplete;

    /// <summary>Something in a condition changed.</summary>
    internal void ConditionChanged()
    {
        if (!_loading) Validate();
    }

    /// <summary>A condition or group was added or removed somewhere.</summary>
    internal void StructureChanged()
    {
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(HasNoConditions));
        OnPropertyChanged(nameof(HasSourceCondition));
        if (!_loading) Validate();
    }

    /// <summary>The app a new source condition starts with, after refreshing the suggestions.</summary>
    internal string SuggestedAppName()
    {
        RefreshKnownApps();
        return SuggestedSource?.Name ?? "";
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
        Join = Root.Join.Join,
        Conditions = Root.Items.Select(c => c.ToModel()).ToList(),
    };

    private void Validate()
    {
        var draft = Draft();
        var compiled = _engine.Compile(draft);
        _compiles = compiled.Success;
        CanCatchInBrowser = compiled.Success && NativeHostBackend.HostsOf(draft).Count > 0;

        for (var i = 0; i < Root.Items.Count && i < compiled.Conditions.Count; i++)
            Root.Items[i].ApplyErrors(compiled.Conditions[i]);

        // Blank conditions are "not yet", not errors: Save stays disabled until they're filled in.
        RuleError = Root.IsEmpty ? "Add a condition: a link pattern, the app it was opened from, or both." : null;
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
        var leaves = NameableLeaves(Root).ToList();
        var link = leaves.FirstOrDefault(c => c.IsLink && !c.IsNegated)?.FirstValue;
        var app = leaves.FirstOrDefault(c => c.IsSource && !c.IsNegated)?.FirstValue;
        var known = app is null ? null : KnownSources?.Invoke().FirstOrDefault(a => string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase));
        _settingName = true;
        Name = RuleNaming.Suggest(link, known?.DisplayName ?? app);
        _settingName = false;
    }

    /// <summary>
    /// The conditions a suggested name may describe: the rule's own, and those in "all of" groups below it.
    /// An "any of" group's conditions are alternatives, so naming the rule after one of them would mislead.
    /// </summary>
    private static IEnumerable<ConditionEditorViewModel> NameableLeaves(ConditionGroupViewModel group)
    {
        foreach (var item in group.Items)
        {
            if (item is ConditionEditorViewModel c) yield return c;
            else if (item is ConditionGroupViewModel { Join.Join: RuleJoin.All } g)
                foreach (var leaf in NameableLeaves(g)) yield return leaf;
        }
    }
}

/// <summary>A row on the editor's track: one condition, or a group of them.</summary>
public abstract partial class ConditionNodeViewModel : ViewModelBase
{
    protected ConditionNodeViewModel(RuleEditorViewModel owner) => Owner = owner;

    public RuleEditorViewModel Owner { get; }

    /// <summary>The group this sits in; null only for the rule's own level.</summary>
    public ConditionGroupViewModel? Parent { get; internal set; }

    public abstract bool IsGroup { get; }

    /// <summary>"and" / "or" shown on the track above every condition but the first.</summary>
    [ObservableProperty] public partial string JoinLabel { get; set; } = "and";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJoinLabel), nameof(CanMoveUp), nameof(IsOnly))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool IsFirst { get; set; }

    /// <summary>The track runs on from this row's glyph down to the next one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveDown), nameof(IsOnly))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool IsLast { get; set; } = true;

    public bool ShowJoinLabel => !IsFirst;
    public bool CanMoveUp => !IsFirst;
    public bool CanMoveDown => !IsLast;

    /// <summary>The only row in its group: nothing to reorder.</summary>
    public bool IsOnly => IsFirst && IsLast;

    /// <summary>Every value filled in and valid, all the way down.</summary>
    public abstract bool IsComplete { get; }

    public abstract RuleCondition ToModel();

    /// <summary>The link and app conditions here, depth first.</summary>
    public abstract IEnumerable<ConditionEditorViewModel> Leaves();

    internal abstract void ApplyErrors(ConditionCompileResult result);

    internal void UpdateJoin(RuleJoin join) => JoinLabel = join == RuleJoin.Any ? "or" : "and";

    [RelayCommand]
    private void Remove() => Parent?.Remove(this);

    /// <summary>Order within a group doesn't change what matches; it's for reading the rule the way you think of it.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Parent?.Move(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Parent?.Move(this, +1);

    internal static ConditionNodeViewModel From(RuleEditorViewModel owner, RuleCondition model, int depth)
    {
        if (!model.IsGroup) return ConditionEditorViewModel.From(owner, model);
        var group = new ConditionGroupViewModel(owner, depth, model.Join);
        group.Load(model.Join, model.Conditions ?? []);
        return group;
    }
}

/// <summary>
/// Conditions joined by all/any: the rule's own level (depth 1) or a group inside it. Groups add
/// conditions and, above the deepest level, further groups.
/// </summary>
public sealed partial class ConditionGroupViewModel : ConditionNodeViewModel
{
    private bool _loading;

    public ConditionGroupViewModel(RuleEditorViewModel owner, int depth, RuleJoin join) : base(owner)
    {
        Depth = depth;
        Join = JoinOption.For(join);
        Items.CollectionChanged += (_, _) => OnItemsChanged();
    }

    /// <summary>1 for the rule itself, 2 for a group in it, and so on.</summary>
    public int Depth { get; }
    public bool IsRoot => Depth == 1;
    public override bool IsGroup => true;
    public IReadOnlyList<JoinOption> Joins => JoinOption.All;
    public ObservableCollection<ConditionNodeViewModel> Items { get; } = [];

    [ObservableProperty] public partial JoinOption Join { get; set; }

    /// <summary>The rule's all/any choice appears only with two or more conditions; a group always shows its own.</summary>
    public bool ShowJoin => Items.Count > 1;
    public bool IsEmpty => Items.Count == 0;
    public bool CanAddGroup => Depth < RuleCondition.MaxDepth;

    public string AddLinkName => IsRoot ? "Add link condition" : "Add link condition to this group";
    public string AddSourceName => IsRoot ? "Add source app condition" : "Add source app condition to this group";
    public string AddTimeName => IsRoot ? "Add time condition" : "Add time condition to this group";
    public string AddGroupName => IsRoot ? "Add condition group" : "Add condition group inside this group";

    public override bool IsComplete => Items.Count > 0 && Items.All(i => i.IsComplete);

    partial void OnJoinChanged(JoinOption value)
    {
        foreach (var item in Items) item.UpdateJoin(value.Join);
        Owner.ConditionChanged();
    }

    [RelayCommand]
    private void AddLinkCondition() => Items.Add(ConditionEditorViewModel.NewLink(Owner, MatchKind.Wildcard, ""));

    [RelayCommand]
    private void AddSourceCondition()
    {
        var condition = new ConditionEditorViewModel(Owner, ConditionField.SourceApp);
        condition.Values.Add(new ConditionValueViewModel(condition, Owner.SuggestedAppName()));
        Items.Add(condition);
    }

    [RelayCommand]
    private void AddTimeCondition()
    {
        var condition = new ConditionEditorViewModel(Owner, ConditionField.Time);
        condition.Values.Add(new ConditionValueViewModel(condition, ""));
        Items.Add(condition);
    }

    /// <summary>
    /// A new group starts with one blank link condition and the opposite join to this one: a group with
    /// the same join as its parent would change nothing.
    /// </summary>
    [RelayCommand]
    private void AddGroup()
    {
        if (!CanAddGroup) return;
        var group = new ConditionGroupViewModel(Owner, Depth + 1, Join.Join == RuleJoin.All ? RuleJoin.Any : RuleJoin.All);
        group.Items.Add(ConditionEditorViewModel.NewLink(Owner, MatchKind.Wildcard, ""));
        Items.Add(group);
    }

    internal void Remove(ConditionNodeViewModel node) => Items.Remove(node);

    internal void Move(ConditionNodeViewModel node, int delta)
    {
        var from = Items.IndexOf(node);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count) return;
        Items.Move(from, to);
    }

    internal void Load(RuleJoin join, IEnumerable<RuleCondition> conditions)
    {
        _loading = true;
        Join = JoinOption.For(join);
        Items.Clear();
        foreach (var c in conditions) Items.Add(From(Owner, c, Depth + 1));
        _loading = false;
        OnItemsChanged();
    }

    public override RuleCondition ToModel() => RuleCondition.Group(Join.Join, [.. Items.Select(i => i.ToModel())]);

    public override IEnumerable<ConditionEditorViewModel> Leaves() => Items.SelectMany(i => i.Leaves());

    internal override void ApplyErrors(ConditionCompileResult result)
    {
        var children = result.Children ?? [];
        for (var i = 0; i < Items.Count && i < children.Count; i++) Items[i].ApplyErrors(children[i]);
    }

    private void OnItemsChanged()
    {
        if (_loading) return;
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Parent = this;
            Items[i].IsFirst = i == 0;
            Items[i].IsLast = i == Items.Count - 1;
            Items[i].UpdateJoin(Join.Join);
        }
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(IsEmpty));
        Owner.StructureChanged();
    }
}

/// <summary>One condition in the editor: what it looks at, how, and its OR'd values.</summary>
public sealed partial class ConditionEditorViewModel : ConditionNodeViewModel
{
    public ConditionEditorViewModel(RuleEditorViewModel owner, ConditionField field) : base(owner)
    {
        Field = field;
        Kind = MatchKindOption.All[0];
        Operators = field switch
        {
            ConditionField.SourceApp => OperatorOption.Source,
            ConditionField.Time => OperatorOption.Time,
            _ => OperatorOption.Link,
        };
        Operator = Operators[0];
        Values.CollectionChanged += (_, _) =>
        {
            for (var i = 0; i < Values.Count; i++)
            {
                Values[i].CanRemove = Values.Count > 1;
                Values[i].IsFirst = i == 0;
            }
            Owner.ConditionChanged();
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

    internal static ConditionEditorViewModel NewLink(RuleEditorViewModel owner, MatchKind kind, string pattern)
    {
        var condition = new ConditionEditorViewModel(owner, ConditionField.Link) { Kind = MatchKindOption.For(kind) };
        condition.Values.Add(new ConditionValueViewModel(condition, pattern));
        return condition;
    }

    public ConditionField Field { get; }
    public override bool IsGroup => false;
    public bool IsLink => Field == ConditionField.Link;
    public bool IsSource => Field == ConditionField.SourceApp;
    public bool IsTime => Field == ConditionField.Time;

    /// <summary>Link and time values are typed into a plain box; apps get suggestions.</summary>
    public bool UsesTextBox => !IsSource;

    public string Heading => Field switch
    {
        ConditionField.Link => "Link",
        ConditionField.Time => "Time",
        _ => "Opened from",
    };
    public IReadOnlyList<MatchKindOption> Kinds => MatchKindOption.All;
    public IReadOnlyList<OperatorOption> Operators { get; }
    public ObservableCollection<ConditionValueViewModel> Values { get; } = [];
    public ObservableCollection<string> KnownApps => Owner.KnownApps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaseSensitive), nameof(Placeholder), nameof(Hint))]
    public partial MatchKindOption Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNegated))]
    public partial OperatorOption Operator { get; set; }

    [ObservableProperty] public partial bool CaseSensitive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool IsNegated => Operator.Negate;
    public bool ShowCaseSensitive => IsLink && Kind.Kind is MatchKind.Regex or MatchKind.PathPrefix;
    public bool HasError => Error is not null;
    public bool HasAnyValue => Values.Any(v => !string.IsNullOrWhiteSpace(v.Text));
    public bool HasValueErrors => Values.Any(v => v.HasError);
    public string? FirstValue => Values.Select(v => v.Text.Trim()).FirstOrDefault(t => t.Length > 0);
    public override bool IsComplete => HasAnyValue && !HasValueErrors;

    public string Placeholder => Field switch
    {
        ConditionField.Link => Kind.Example,
        ConditionField.Time => TimeWindow.Example,
        _ => "ms-teams.exe",
    };

    public string Hint => Field switch
    {
        ConditionField.Link => Kind.Hint,
        ConditionField.Time => "Local time. Hours (09:00-17:00, 9am-5pm), days (Mon-Fri, Sat,Sun, weekdays), or both. Overnight works: 22:00-06:00.",
        _ => "The program's name, with or without .exe, its title, a pattern like *teams*, or a full path.",
    };

    /// <summary>"or…" adds another alternative.</summary>
    public string AddValueLabel => Field switch
    {
        ConditionField.Link => "Or another pattern",
        ConditionField.Time => "Or another time",
        _ => "Or another app",
    };

    partial void OnKindChanged(MatchKindOption value) => Owner.ConditionChanged();
    partial void OnOperatorChanged(OperatorOption value) => Owner.ConditionChanged();
    partial void OnCaseSensitiveChanged(bool value) => Owner.ConditionChanged();

    internal void ValueChanged() => Owner.ConditionChanged();

    [RelayCommand]
    private void AddValue() => Values.Add(new ConditionValueViewModel(this, ""));

    internal void RemoveValue(ConditionValueViewModel value)
    {
        if (Values.Count > 1) Values.Remove(value);
    }

    internal override void ApplyErrors(ConditionCompileResult result)
    {
        for (var i = 0; i < Values.Count; i++)
            Values[i].Error = i < result.ValueErrors.Count ? result.ValueErrors[i] : null;
    }

    public override IEnumerable<ConditionEditorViewModel> Leaves() => [this];

    public override RuleCondition ToModel() => new()
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
        UpdateReading();
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

    /// <summary>
    /// Time values only: how the value was read ("Weekdays, 09:00 to 17:00"), so "9-5" shows up as overnight
    /// before it routes anything. Hidden while there's an error, which says what's wrong instead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReading))]
    public partial string? Reading { get; private set; }

    public bool ShowReading => Reading is not null && !HasError;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(ShowReading));

    partial void OnTextChanged(string value)
    {
        UpdateReading();
        _owner.ValueChanged();
    }

    // _owner is unset while the generated property runs from the constructor's first assignment.
    private void UpdateReading() => Reading = _owner?.IsTime == true ? TimeWindow.Describe(Text) : null;

    [RelayCommand]
    private void Remove() => _owner.RemoveValue(this);
}
