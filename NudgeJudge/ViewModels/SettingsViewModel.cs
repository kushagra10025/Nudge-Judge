using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

/// <summary>Owns the app-wide settings, including the routing switch shared by the sidebar and tray.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly IDefaultBrowserRegistrar _registrar;
    private readonly IThemeService _theme;
    private readonly ILaunchAtStartup _startup;
    private bool _loading;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, Action<Core.Models.AppConfig>> _pending = [];
    private int _pendingGeneration;

    /// <summary>How long a number field has to stay put before it's saved, so spinning through values writes once.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private bool _refreshing;

    public SettingsViewModel(IConfigStore config, IDefaultBrowserRegistrar registrar, IThemeService theme, IUiDispatcher dispatcher,
        ILaunchAtStartup startup)
    {
        _config = config;
        _startup = startup;
        _registrar = registrar;
        _theme = theme;
        _dispatcher = dispatcher;
        ConfigPath = AppPaths.ConfigFile;
        ActivityLogPath = AppPaths.ActivityLogFile;
        Theme = ThemeOption.All[0];
        config.Changed += (_, _) => dispatcher.Post(Load);
        Load();
    }

    public ObservableCollection<BrowserOption> Browsers { get; } = [];
    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;
    public string ConfigPath { get; }
    public string ActivityLogPath { get; }
    public int MinActivityLogLimit => ActivityLog.MinCapacity;
    public int MaxActivityLogLimit => ActivityLog.MaxCapacity;

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
    [ObservableProperty] public partial bool ActivityLogEnabled { get; set; }
    [ObservableProperty] public partial decimal? ActivityLogLimit { get; set; }
    [ObservableProperty] public partial ThemeOption Theme { get; set; }

    /// <summary>Mirrors the OS startup entry; changing it adds or removes that entry.</summary>
    [ObservableProperty] public partial bool LaunchAtStartup { get; set; }

    /// <summary>Why the last change to <see cref="LaunchAtStartup"/> didn't take, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupMessage))]
    public partial string StartupMessage { get; set; } = "";

    public bool HasStartupMessage => StartupMessage.Length > 0;
    public bool CanLaunchAtStartup => _startup.IsSupported;

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
    partial void OnChooserGroupingSecondsChanged(decimal? value) => SaveSoon(nameof(ChooserGroupingSeconds), c => c.Settings.ChooserGroupingSeconds = (int)Math.Clamp(value ?? 5, 0, 120));
    partial void OnTemporaryRuleHoursChanged(decimal? value) => SaveSoon(nameof(TemporaryRuleHours), c => c.Settings.TemporaryRuleHours = (int)Math.Clamp(value ?? 8, 1, 168));
    partial void OnRunInBackgroundChanged(bool value) => Save(c => c.Settings.RunInBackground = value);
    partial void OnActivityLogEnabledChanged(bool value) => Save(c => c.Settings.ActivityLogEnabled = value);

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_loading) return;
        try
        {
            _startup.SetEnabled(value);
            StartupMessage = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StartupMessage = (value ? "Couldn't add NudgeJudge to startup: " : "Couldn't remove NudgeJudge from startup: ") + e.Message;
        }
        // Show what the OS actually has, whether or not the change took.
        LoadStartup();
    }

    private void LoadStartup()
    {
        var was = _loading;
        _loading = true;
        try
        {
            LaunchAtStartup = _startup.IsSupported && _startup.IsEnabled();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LaunchAtStartup = false;
        }
        finally
        {
            _loading = was;
        }
    }

    partial void OnActivityLogLimitChanged(decimal? value) =>
        // Debounced: lowering it trims the log, and spinning 500 → 50 → 500 mustn't drop 450 entries on the way.
        SaveSoon(nameof(ActivityLogLimit), c => c.Settings.ActivityLogLimit =
            (int)Math.Clamp(value ?? ActivityLog.DefaultCapacity, ActivityLog.MinCapacity, ActivityLog.MaxCapacity));

    partial void OnThemeChanged(ThemeOption value)
    {
        _theme.Apply(value.Value);
        Save(c => c.Settings.Theme = value.Value);
    }

    private void Save(Action<Core.Models.AppConfig> mutate)
    {
        if (!_loading) _config.Update(mutate);
    }

    /// <summary>Saves after <see cref="SaveDelay"/> without further changes to <paramref name="field"/> (or any other debounced field).</summary>
    private void SaveSoon(string field, Action<Core.Models.AppConfig> mutate)
    {
        if (_loading) return;
        _pending[field] = mutate;
        var generation = ++_pendingGeneration;
        _ = Task.Delay(SaveDelay).ContinueWith(_ => _dispatcher.Post(() =>
        {
            if (generation == _pendingGeneration) FlushPendingSaves();
        }), TaskScheduler.Default);
    }

    /// <summary>Writes any debounced changes now, in one update. Call before the app exits.</summary>
    public void FlushPendingSaves()
    {
        if (_pending.Count == 0) return;
        var changes = _pending.Values.ToList();
        _pending.Clear();
        _pendingGeneration++;
        _config.Update(c =>
        {
            foreach (var change in changes) change(c);
        });
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
            // A field still waiting out its debounce keeps what the user typed rather than snapping back.
            if (!_pending.ContainsKey(nameof(ChooserGroupingSeconds))) ChooserGroupingSeconds = s.ChooserGroupingSeconds;
            if (!_pending.ContainsKey(nameof(TemporaryRuleHours))) TemporaryRuleHours = s.TemporaryRuleHours;
            RunInBackground = s.RunInBackground;
            ActivityLogEnabled = s.ActivityLogEnabled;
            if (!_pending.ContainsKey(nameof(ActivityLogLimit))) ActivityLogLimit = ActivityLog.ClampCapacity(s.ActivityLogLimit);
            Theme = ThemeOption.All.First(t => t.Value == s.Theme);
            _theme.Apply(s.Theme);
            LoadStartup();
        }
        finally
        {
            _loading = false;
        }
    }
}
