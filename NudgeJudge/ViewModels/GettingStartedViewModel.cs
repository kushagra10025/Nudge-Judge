using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NudgeJudge.ViewModels;

/// <summary>
/// The Get started page: first run's "how would you like to start?", then the steps that get links routing.
/// It owns no state of its own; everything comes from the Rules and Settings pages it guides the user through.
/// </summary>
public sealed partial class GettingStartedViewModel : ViewModelBase
{
    private static readonly string[] Derived =
    [
        nameof(ShowSteps), nameof(NeedsRule), nameof(IsComplete), nameof(ShowDone), nameof(HasWorkLeft),
        nameof(ShowRoutingStep), nameof(ShowRoutingNote), nameof(StepsIntro), nameof(RoutingStepDetail),
        nameof(NextStep), nameof(BrowsersNext), nameof(RuleNext), nameof(DefaultNext), nameof(RoutingNext),
        nameof(ShowRegistrationMessage), nameof(ShowOpenSetup),
    ];

    private static readonly HashSet<string> Inputs =
    [
        nameof(RulesViewModel.HasRules), nameof(RulesViewModel.HasBrowsers), nameof(RulesViewModel.ShowWelcome),
        nameof(SettingsViewModel.IsRegistered), nameof(SettingsViewModel.NeedsSetup), nameof(SettingsViewModel.RoutingEnabled),
        nameof(SettingsViewModel.PrimaryBrowser), nameof(SettingsViewModel.RegistrationMessage),
        nameof(SettingsViewModel.CanRegister), nameof(SettingsViewModel.HasTriedRegistering),
    ];

    public GettingStartedViewModel(RulesViewModel rules, SettingsViewModel settings)
    {
        Rules = rules;
        Settings = settings;
        rules.PropertyChanged += Forward;
        settings.PropertyChanged += Forward;
    }

    public RulesViewModel Rules { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The checklist, once the user has chosen how to start.</summary>
    public bool ShowSteps => !Rules.ShowWelcome;

    public bool NeedsRule => !Rules.HasRules;

    /// <summary>Links are actually being routed: there are rules, links reach NudgeJudge, and routing is on.</summary>
    public bool IsComplete => Rules.HasRules && Settings.IsRegistered && Settings.RoutingEnabled;

    public bool ShowDone => ShowSteps && IsComplete;

    /// <summary>Keeps the page in the sidebar while something still stands between the user and routed links.</summary>
    public bool HasWorkLeft => !Rules.HasRules || Settings.NeedsSetup;

    /// <summary>Routing is on out of the box, so it's only a step when someone has switched it off.</summary>
    public bool ShowRoutingStep => !Settings.RoutingEnabled;

    /// <summary>With routing on, one line under the steps says what the sidebar switch is for.</summary>
    public bool ShowRoutingNote => Settings.RoutingEnabled && !IsComplete;

    public string StepsIntro => (ShowRoutingStep ? "Four" : "Three") +
                                " steps. After that, links from mail, chat and other apps go straight to the right browser.";

    public string RoutingStepDetail =>
        "Routing is off, so your rules are paused and " +
        (Settings.PrimaryBrowser is { } b ? $"every link opens in {b.Name}." : "you're asked which browser to use for every link.");

    /// <summary>The first step still to do (1–4), or 0 when nothing is left. Only its button is the accent.</summary>
    public int NextStep =>
        !Rules.HasBrowsers ? 1
        : !Rules.HasRules ? 2
        : !Settings.IsRegistered ? 3
        : !Settings.RoutingEnabled ? 4
        : 0;

    public bool BrowsersNext => NextStep == 1;
    public bool RuleNext => NextStep == 2;
    public bool DefaultNext => NextStep == 3;
    public bool RoutingNext => NextStep == 4;

    /// <summary>What the last registration attempt said, under the step, while there's still something to do.</summary>
    public bool ShowRegistrationMessage =>
        Settings.NeedsSetup && Settings.HasTriedRegistering && !string.IsNullOrEmpty(Settings.RegistrationMessage);

    /// <summary>This platform can't make NudgeJudge the default from here, so the step points at Settings instead.</summary>
    public bool ShowOpenSetup => Settings.NeedsSetup && !Settings.CanRegister;

    /// <summary>Raised to open the rule editor, which lives on the Rules page.</summary>
    public event EventHandler? AddRuleRequested;

    /// <summary>Raised to leave for the Rules page.</summary>
    public event EventHandler? OpenRulesRequested;

    /// <summary>Raised to open Settings, for platforms where NudgeJudge can't make itself the default.</summary>
    public event EventHandler? OpenSetupRequested;

    [RelayCommand] private void AddRule() => AddRuleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenRules() => OpenRulesRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenSetup() => OpenSetupRequested?.Invoke(this, EventArgs.Empty);

    private void Forward(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } name || !Inputs.Contains(name)) return;
        foreach (var derived in Derived) OnPropertyChanged(derived);
    }
}
