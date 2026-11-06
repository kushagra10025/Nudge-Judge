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
        nameof(RoutingStepTitle), nameof(RoutingStepDetail),
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

    public string RoutingStepTitle => Settings.RoutingEnabled ? "Routing is on" : "Turn routing on";

    public string RoutingStepDetail =>
        (Settings.RoutingEnabled ? "It's on by default. " : "") +
        "The switch at the bottom left pauses your rules without deleting them; while it's off, " +
        (Settings.PrimaryBrowser is { } b ? $"every link opens in {b.Name}." : "every link goes to the chooser.") +
        " It only has links to work on once NudgeJudge is your default browser.";

    /// <summary>Raised to open the rule editor, which lives on the Rules page.</summary>
    public event EventHandler? AddRuleRequested;

    /// <summary>Raised to leave for the Rules page.</summary>
    public event EventHandler? OpenRulesRequested;

    /// <summary>Raised to open Settings, where NudgeJudge becomes the default browser.</summary>
    public event EventHandler? OpenSetupRequested;

    [RelayCommand] private void AddRule() => AddRuleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenRules() => OpenRulesRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenSetup() => OpenSetupRequested?.Invoke(this, EventArgs.Empty);

    private void Forward(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var name in Derived) OnPropertyChanged(name);
    }
}
