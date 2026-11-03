using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(RulesViewModel rules, BrowsersViewModel browsers, ActivityViewModel activity,
        SettingsViewModel settings, IConfigStore config)
    {
        Rules = rules;
        Browsers = browsers;
        Activity = activity;
        Settings = settings;
        ConfigProblem = config.LoadError;

        rules.FindBrowsersRequested += (_, _) =>
        {
            Navigate(AppPage.Browsers);
            if (browsers.ScanCommand.CanExecute(null)) browsers.ScanCommand.Execute(null);
        };
    }

    public RulesViewModel Rules { get; }
    public BrowsersViewModel Browsers { get; }
    public ActivityViewModel Activity { get; }
    public SettingsViewModel Settings { get; }
    public string? ConfigProblem { get; }
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
}
