using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(RulesViewModel rules, BrowsersViewModel browsers, ActivityViewModel activity,
        SettingsViewModel settings, IConfigStore config, IUiDispatcher? dispatcher = null)
    {
        Rules = rules;
        Browsers = browsers;
        Activity = activity;
        Settings = settings;
        ConfigProblem = config.SaveError ?? config.LoadError;
        // A save can fail (or start working again) at any time, and Changed may come from a background thread.
        config.Changed += (_, _) =>
        {
            void Refresh() => ConfigProblem = config.SaveError ?? config.LoadError;
            if (dispatcher is null) Refresh();
            else dispatcher.Post(Refresh);
        };

        rules.KnownSources = () => activity.KnownSources;
        rules.FindBrowsersRequested += (_, _) =>
        {
            Navigate(AppPage.Browsers);
            if (browsers.ScanCommand.CanExecute(null)) browsers.ScanCommand.Execute(null);
        };
        activity.CreateRuleRequested += (_, source) =>
        {
            Navigate(AppPage.Rules);
            rules.NewRuleFrom(source);
        };
        activity.BrowserNeedsReview = id => config.Current.FindBrowser(id)?.NeedsReview == true;
        activity.ReviewBrowserRequested += (_, id) =>
        {
            Navigate(AppPage.Browsers);
            browsers.Edit(id);
        };
        // Reviewing (or importing) a browser changes which failed links can offer "Review browser".
        config.Changed += (_, _) =>
        {
            if (dispatcher is null) activity.RefreshReviewState();
            else dispatcher.Post(activity.RefreshReviewState);
        };
    }

    public RulesViewModel Rules { get; }
    public BrowsersViewModel Browsers { get; }
    public ActivityViewModel Activity { get; }
    public SettingsViewModel Settings { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigProblem))]
    public partial string? ConfigProblem { get; private set; }

    public bool HasConfigProblem => ConfigProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial int SelectedPageIndex { get; set; }

    public ViewModelBase CurrentPage => (AppPage)SelectedPageIndex switch
    {
        AppPage.Browsers => Browsers,
        AppPage.Activity => Activity,
        AppPage.Settings => Settings,
        _ => Rules,
    };

    public void Navigate(AppPage page) => SelectedPageIndex = (int)page;

    [RelayCommand] private void OpenSetup() => Navigate(AppPage.Settings);

    /// <summary>The main window came forward: re-check anything that can change while the app is in the background.</summary>
    public void OnActivated()
    {
        if (Settings.RefreshRegistrationCommand.CanExecute(null)) Settings.RefreshRegistrationCommand.Execute(null);
        // Picks up links a standalone launch handled (when the single-instance server couldn't be reached).
        Activity.Reload();
    }
}
