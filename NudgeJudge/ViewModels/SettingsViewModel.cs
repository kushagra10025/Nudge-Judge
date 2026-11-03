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
    private bool _refreshing;

    public SettingsViewModel(IConfigStore config, IDefaultBrowserRegistrar registrar, IThemeService theme, IUiDispatcher dispatcher)
    {
        _config = config;
        _registrar = registrar;
        _theme = theme;
        ConfigPath = AppPaths.ConfigFile;
        Theme = ThemeOption.All[0];
        config.Changed += (_, _) => dispatcher.Post(Load);
        Load();
    }

    public ObservableCollection<BrowserOption> Browsers { get; } = [];
    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;
    public string ConfigPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingLabel), nameof(RoutingDetail), nameof(SignalLabel), nameof(SignalDetail),
        nameof(SignalOn), nameof(SignalOff))]
    public partial bool RoutingEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingDetail), nameof(SignalDetail))]
    public partial BrowserOption? PrimaryBrowser { get; set; }

    [ObservableProperty] public partial decimal? ChooserGroupingSeconds { get; set; }
    [ObservableProperty] public partial decimal? TemporaryRuleHours { get; set; }
    [ObservableProperty] public partial bool RunInBackground { get; set; }
    [ObservableProperty] public partial ThemeOption Theme { get; set; }

    [ObservableProperty] public partial string RegistrationMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup), nameof(CanRegister), nameof(SignalLabel), nameof(SignalDetail), nameof(SignalOn),
        nameof(SignalOff))]
    public partial bool IsRegistered { get; set; }

    /// <summary>False until the OS has been asked once, so nothing claims "not default" before we know.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup), nameof(CanRegister), nameof(SignalLabel), nameof(SignalDetail), nameof(SignalOn),
        nameof(SignalOff))]
    public partial bool RegistrationChecked { get; set; }

    /// <summary>NudgeJudge isn't the system's default browser, so no links reach it whatever routing says.</summary>
    public bool NeedsSetup => RegistrationChecked && !IsRegistered;

    /// <summary>Shows the "Make default" button: there's something to do and this platform can do it.</summary>
    public bool CanRegister => NeedsSetup && _registrar.IsSupported;

    // The sidebar signal shows the one state that matters most: not receiving links at all, then on/off.
    public bool SignalOn => !NeedsSetup && RoutingEnabled;
    public bool SignalOff => !NeedsSetup && !RoutingEnabled;
    public string SignalLabel => NeedsSetup ? "Not default yet" : RoutingLabel;
    public string SignalDetail => NeedsSetup ? "Links can't reach it" : RoutingDetail;

    public string RoutingLabel => RoutingEnabled ? "Routing on" : "Routing off";

    public string RoutingDetail => RoutingEnabled
        ? "Links follow your rules"
        : PrimaryBrowser is { } b ? $"Everything opens in {b.Name}" : "Everything goes to the chooser";

    [RelayCommand] private void ToggleRouting() => RoutingEnabled = !RoutingEnabled;

    [RelayCommand]
    private async Task RegisterAsync()
    {
        RegistrationMessage = "Registering…";
        try
        {
            Apply(await _registrar.RegisterAsync());
        }
        catch (Exception e)
        {
            Apply(new RegistrationStatus(RegistrationState.Unknown, "Registration failed: " + e.Message));
        }
    }

    /// <summary>
    /// Asks the OS again. Run whenever the main window comes forward, since the default can change outside
    /// the app (and on Windows, registering finishes in the system's Default Apps page).
    /// </summary>
    [RelayCommand]
    private async Task RefreshRegistrationAsync()
    {
        if (_refreshing || RegisterCommand.IsRunning) return;
        _refreshing = true;
        try
        {
            Apply(await _registrar.GetStatusAsync());
        }
        catch (Exception e)
        {
            Apply(new RegistrationStatus(RegistrationState.Unknown, "Couldn't check the default browser: " + e.Message));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(RegistrationStatus status)
    {
        RegistrationMessage = status.Message;
        IsRegistered = status.State == RegistrationState.Registered;
        RegistrationChecked = true;
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
}
