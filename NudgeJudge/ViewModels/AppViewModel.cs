using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

/// <summary>Backs the tray icon and its menu.</summary>
public sealed partial class AppViewModel(SettingsViewModel settings, IWindowService windows) : ViewModelBase
{
    public SettingsViewModel Settings { get; } = settings;

    [RelayCommand] private void OpenMain() => windows.ShowMainWindow();
    [RelayCommand] private void OpenActivity() => windows.ShowMainWindow(AppPage.Activity);
    [RelayCommand] private void OpenRules() => windows.ShowMainWindow(AppPage.Rules);
    [RelayCommand] private void Quit() => windows.Quit();
}
