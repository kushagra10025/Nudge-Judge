using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;
using NudgeJudge.Core.Security;

namespace NudgeJudge.ViewModels;

public sealed partial class RuleEditorViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly RuleEngine _engine;
    private readonly Func<string> _testUrl;
    private DateTimeOffset? _expiresAt;
    private string? _expiresAfterBootId;

    public RuleEditorViewModel(IConfigStore config, RuleEngine engine, ObservableCollection<BrowserOption> browsers, Func<string> testUrl)
    {
        _config = config;
        _engine = engine;
        _testUrl = testUrl;
        Browsers = browsers;
        Kind = MatchKindOption.All[0];
    }

    public IReadOnlyList<MatchKindOption> Kinds => MatchKindOption.All;
    public ObservableCollection<BrowserOption> Browsers { get; }
    public Guid? EditingId { get; private set; }

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsNew { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaseSensitive))]
    public partial MatchKindOption Kind { get; set; }

    [ObservableProperty] public partial string Pattern { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial BrowserOption? Browser { get; set; }

    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(HasPatternError))]
    public partial string? PatternError { get; set; }

    [ObservableProperty] public partial string? TestVerdict { get; set; }
    [ObservableProperty] public partial bool TestVerdictMatches { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTemporary))]
    public partial string? ExpiryText { get; set; }

    public string Title => IsNew ? "New rule" : "Edit rule";
    public bool HasPatternError => PatternError is not null;
    public bool ShowCaseSensitive => Kind.Kind is MatchKind.Regex or MatchKind.PathPrefix;
    public bool IsTemporary => ExpiryText is not null;

    partial void OnKindChanged(MatchKindOption value) => Validate();
    partial void OnPatternChanged(string value) => Validate();
    partial void OnCaseSensitiveChanged(bool value) => Validate();

    public void OpenNew(string pattern, Guid? browserId)
    {
        EditingId = null;
        IsNew = true;
        Name = "";
        Kind = MatchKindOption.All[0];
        Pattern = pattern;
        Browser = Browsers.FirstOrDefault(b => b.Id == browserId) ?? Browsers.FirstOrDefault();
        CaseSensitive = false;
        IsEnabled = true;
        _expiresAt = null;
        _expiresAfterBootId = null;
        ExpiryText = null;
        Validate();
        IsOpen = true;
    }

    public void OpenExisting(Rule rule)
    {
        EditingId = rule.Id;
        IsNew = false;
        Name = rule.Name;
        Kind = MatchKindOption.For(rule.MatchKind);
        Pattern = rule.Pattern;
        Browser = Browsers.FirstOrDefault(b => b.Id == rule.BrowserId);
        CaseSensitive = rule.CaseSensitive;
        IsEnabled = rule.Enabled;
        _expiresAt = rule.ExpiresAt;
        _expiresAfterBootId = rule.ExpiresAfterBootId;
        ExpiryText = RulesViewModel.ExpiryText(rule, DateTimeOffset.UtcNow);
        Validate();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        EditingId = null;
    }

    /// <summary>Re-resolves the selected browser after the browser list was rebuilt.</summary>
    public void SyncBrowsers()
    {
        if (Browser is { } b) Browser = Browsers.FirstOrDefault(o => o.Id == b.Id);
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
        _config.Update(c =>
        {
            var rule = id is { } existing ? c.Rules.Find(r => r.Id == existing) : null;
            if (rule is null)
            {
                rule = new Rule();
                c.Rules.Add(rule);
            }
            rule.Name = Name.Trim();
            rule.MatchKind = Kind.Kind;
            rule.Pattern = Pattern.Trim();
            rule.BrowserId = Browser!.Id;
            rule.CaseSensitive = CaseSensitive;
            rule.Enabled = IsEnabled;
            rule.ExpiresAt = _expiresAt;
            rule.ExpiresAfterBootId = _expiresAfterBootId;
        });
        Close();
    }

    private bool CanSave() => Browser is not null && PatternError is null && !string.IsNullOrWhiteSpace(Pattern);

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            PatternError = null; // empty is "not yet", not an error; Save stays disabled
            TestVerdict = null;
            SaveCommand.NotifyCanExecuteChanged();
            return;
        }

        var compiled = _engine.Compile(Kind.Kind, Pattern, CaseSensitive);
        PatternError = compiled.Error;
        SaveCommand.NotifyCanExecuteChanged();

        if (!compiled.Success || UrlValidator.Validate(_testUrl()).Url is not { } url)
        {
            TestVerdict = null;
            return;
        }

        try
        {
            TestVerdictMatches = compiled.Pattern!.IsMatch(url);
            TestVerdict = TestVerdictMatches ? "Matches the test link" : "Doesn't match the test link";
        }
        catch (RegexMatchTimeoutException)
        {
            TestVerdictMatches = false;
            TestVerdict = "Took too long to match and would be skipped";
        }
    }
}
