using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Platform;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

/// <summary>Owns the app-wide settings, including the routing switch shared by the sidebar and tray.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly IDefaultBrowserRegistrar _registrar;
    private readonly IThemeService _theme;
    private bool _loading;

    public SettingsViewModel(IConfigStore config, IDefaultBrowserRegistrar registrar, IThemeService theme, IUiDispatcher dispatcher)
    {
        _config = config;
        _registrar = registrar;
        _theme = theme;
        ConfigPath = AppPaths.ConfigFile;
        Theme = ThemeOption.All[0];
        config.Changed += (_, _) => dispatcher.Post(Load);
        Load();
        RefreshRegistration();
    }

    public ObservableCollection<BrowserOption> Browsers { get; } = [];
    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;
    public string ConfigPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingLabel), nameof(RoutingDetail))]
    public partial bool RoutingEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingDetail))]
    public partial BrowserOption? PrimaryBrowser { get; set; }

    [ObservableProperty] public partial decimal? ChooserGroupingSeconds { get; set; }
    [ObservableProperty] public partial decimal? TemporaryRuleHours { get; set; }
    [ObservableProperty] public partial bool RunInBackground { get; set; }
    [ObservableProperty] public partial ThemeOption Theme { get; set; }

    [ObservableProperty] public partial string RegistrationMessage { get; set; } = "";
    [ObservableProperty] public partial bool IsRegistered { get; set; }

    public string RoutingLabel => RoutingEnabled ? "Routing on" : "Routing off";

    public string RoutingDetail => RoutingEnabled
        ? "Links follow your rules"
        : PrimaryBrowser is { } b ? $"Everything opens in {b.Name}" : "Everything goes to the chooser";

    [RelayCommand] private void ToggleRouting() => RoutingEnabled = !RoutingEnabled;

    [RelayCommand]
    private async Task RegisterAsync()
    {
        var status = await _registrar.RegisterAsync();
        RegistrationMessage = status.Message;
        IsRegistered = status.State == RegistrationState.Registered;
    }

    partial void OnRoutingEnabledChanged(bool value) => Save(c => c.Settings.RoutingEnabled = value);
    partial void OnPrimaryBrowserChanged(BrowserOption? value) => Save(c => c.Settings.PrimaryBrowserId = value?.Id);
    partial void OnChooserGroupingSecondsChanged(decimal? value) => Save(c => c.Settings.ChooserGroupingSeconds = (int)Math.Clamp(value ?? 5, 0, 120));
    partial void OnTemporaryRuleHoursChanged(decimal? value) => Save(c => c.Settings.TemporaryRuleHours = (int)Math.Clamp(value ?? 8, 1, 168));
    partial void OnRunInBackgroundChanged(bool value) => Save(c => c.Settings.RunInBackground = value);

    partial void OnThemeChanged(ThemeOption value)
    {
        _theme.Apply(value.Value);
        Save(c => c.Settings.Theme = value.Value);
    }

    private void Save(Action<Core.Models.AppConfig> mutate)
    {
        if (!_loading) _config.Update(mutate);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _config.Current.Settings;
            Browsers.Clear();
            foreach (var b in _config.Current.Browsers) Browsers.Add(BrowserOption.From(b));

            RoutingEnabled = s.RoutingEnabled;
            PrimaryBrowser = Browsers.FirstOrDefault(b => b.Id == s.PrimaryBrowserId);
            ChooserGroupingSeconds = s.ChooserGroupingSeconds;
            TemporaryRuleHours = s.TemporaryRuleHours;
            RunInBackground = s.RunInBackground;
            Theme = ThemeOption.All.First(t => t.Value == s.Theme);
            _theme.Apply(s.Theme);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshRegistration()
    {
        var status = _registrar.GetStatus();
        RegistrationMessage = status.Message;
        IsRegistered = status.State == RegistrationState.Registered;
    }
}
