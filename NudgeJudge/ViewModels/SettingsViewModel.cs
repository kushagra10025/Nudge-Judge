using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.NativeMessaging;
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
    private readonly INativeHostRegistration _extension;
    private readonly IDialogService? _dialogs;
    private bool _loading;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, Action<Core.Models.AppConfig>> _pending = [];
    private int _pendingGeneration;

    /// <summary>How long a number field has to stay put before it's saved, so spinning through values writes once.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private bool _refreshing;
    private bool _refreshAgain;

    public SettingsViewModel(IConfigStore config, IDefaultBrowserRegistrar registrar, IThemeService theme, IUiDispatcher dispatcher,
        ILaunchAtStartup startup, UpdatesViewModel? updates = null, IDialogService? dialogs = null,
        INativeHostRegistration? extension = null)
    {
        _extension = extension ?? new StubNativeHostRegistration();
        Updates = updates;
        _dialogs = dialogs;
        _config = config;
        _startup = startup;
        _registrar = registrar;
        _theme = theme;
        _dispatcher = dispatcher;
        ConfigPath = AppPaths.ConfigFile;
        ActivityLogPath = AppPaths.ActivityLogFile;
        // Guarded: this placeholder must not be saved over the user's theme before Load reads it.
        _loading = true;
        Theme = ThemeOption.All[0];
        config.Changed += (_, _) => dispatcher.Post(Load);
        // Only re-checks once the OS has been asked (a window was opened), like the activation check.
        registrar.WatchForChanges(() => dispatcher.Post(() =>
        {
            if (RegistrationChecked) _ = RefreshRegistrationCommand.ExecuteAsync(null);
        }));
        Load();
    }

    public ObservableCollection<BrowserOption> Browsers { get; } = [];

    /// <summary>Settings → Updates and the sidebar update toast. Null where updates aren't wired up (tests).</summary>
    public UpdatesViewModel? Updates { get; }

    public string AppVersion => Updates?.CurrentVersion ?? AppInfo.BuildVersion;
    public Uri RepositoryUrl { get; } = new(AppInfo.RepositoryUrl);
    public Uri ReleasesUrl { get; } = new(AppInfo.ReleasesUrl);
    public Uri IssuesUrl { get; } = new(AppInfo.IssuesUrl);
    public Uri LicenseUrl { get; } = new(AppInfo.LicenseUrl);
    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;
    public string ConfigPath { get; }
    public string ActivityLogPath { get; }
    public int MinActivityLogLimit => ActivityLog.MinCapacity;
    public int MaxActivityLogLimit => ActivityLog.MaxCapacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingLabel), nameof(RoutingDetail), nameof(SignalLabel), nameof(SignalDetail),
        nameof(SignalOn), nameof(SignalOff), nameof(RoutingSwitchTip), nameof(RoutingSwitchCaption))]
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

    /// <summary>Mirrors the extension's launcher on disk; changing it writes or removes the launcher and manifests.</summary>
    [ObservableProperty] public partial bool ExtensionConnected { get; set; }

    /// <summary>Why the last change to <see cref="ExtensionConnected"/> didn't take, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtensionMessage))]
    public partial string ExtensionMessage { get; set; } = "";

    public bool HasExtensionMessage => ExtensionMessage.Length > 0;
    public bool CanConnectExtension => _extension.IsSupported;
    public Uri ExtensionHelpUrl { get; } = new(AppInfo.RepositoryUrl + "/blob/main/docs/browser-extension.md");

    /// <summary>What the extension's switch says about it, with the click that's the default on this platform.</summary>
    public string ExtensionDetail { get; } =
        "Lets the NudgeJudge extension send links you click inside Chrome, Edge, Brave, Vivaldi or Firefox here. " +
        (OperatingSystem.IsMacOS() ? "⌥⇧-click" : OperatingSystem.IsWindows() ? "Alt+Shift+click" : "Ctrl+Alt+click") +
        " a link, or right-click it and pick Open with NudgeJudge.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignalDetail))]
    public partial string RegistrationMessage { get; set; } = "";

    /// <summary>
    /// Set once the user asks to register, so the sidebar and Get started show what happened ("Registering…", an
    /// error, or Windows' "choose NudgeJudge there") instead of the plain "not default" line.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignalDetail))]
    public partial bool HasTriedRegistering { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup), nameof(CanRegister), nameof(SignalLabel), nameof(SignalDetail), nameof(SignalOn),
        nameof(SignalOff), nameof(RoutingSwitchTip))]
    public partial bool IsRegistered { get; set; }

    /// <summary>False until the OS has been asked once, so nothing claims "not default" before we know.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup), nameof(CanRegister), nameof(SignalLabel), nameof(SignalDetail), nameof(SignalOn),
        nameof(SignalOff), nameof(RoutingSwitchTip))]
    public partial bool RegistrationChecked { get; set; }

    /// <summary>NudgeJudge isn't the system's default browser, so no links reach it whatever routing says.</summary>
    public bool NeedsSetup => RegistrationChecked && !IsRegistered;

    /// <summary>Shows the "Make default" button: there's something to do and this platform can do it.</summary>
    public bool CanRegister => NeedsSetup && _registrar.IsSupported;

    // The sidebar signal shows the one state that matters most: not receiving links at all, then on/off.
    public bool SignalOn => !NeedsSetup && RoutingEnabled;
    public bool SignalOff => !NeedsSetup && !RoutingEnabled;
    public string SignalLabel => NeedsSetup ? "Not receiving links" : RoutingLabel;

    public string SignalDetail => NeedsSetup
        ? HasTriedRegistering && RegistrationMessage.Length > 0 ? RegistrationMessage : "NudgeJudge isn't your default browser yet"
        : RoutingDetail;

    /// <summary>
    /// Under the sidebar's own "Routing" row while links can't arrive: the switch still works, but it has nothing
    /// to act on yet, so it says when it takes effect rather than implying links are being routed.
    /// </summary>
    public string RoutingSwitchCaption => RoutingEnabled ? "On · takes effect once links arrive" : "Off · rules paused";

    public string RoutingSwitchTip => (NeedsSetup, RoutingEnabled) switch
    {
        (true, true) => "Routing is on, and takes effect once NudgeJudge is your default browser",
        (true, false) => "Routing is off. Turn it on so links follow your rules once NudgeJudge is your default browser",
        (false, true) => "Routing is on: links follow your rules. Turn off to pause them",
        (false, false) => "Routing is off: rules are paused. Turn on to route links by rules",
    };

    public string RoutingLabel => RoutingEnabled ? "Routing on" : "Routing off";

    public string RoutingDetail => RoutingEnabled
        ? "Links follow your rules"
        : PrimaryBrowser is { } b ? $"Everything opens in {b.Name}" : "Everything goes to the chooser";

    [RelayCommand] private void ToggleRouting() => RoutingEnabled = !RoutingEnabled;

    /// <summary>The outcome of the last export or import, shown under the buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransferMessage))]
    public partial string? TransferMessage { get; set; }

    [ObservableProperty] public partial bool TransferFailed { get; set; }
    public bool HasTransferMessage => TransferMessage is not null;

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (_dialogs is null) return;
        var path = await _dialogs.PickSaveJsonAsync("Export rules and browsers", $"nudgejudge-{DateTime.Now:yyyy-MM-dd}.json");
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, ConfigTransfer.Export(_config.Current));
            var c = _config.Current;
            ShowTransfer($"Exported {ImportResult.Count(c.Rules.Count(r => !r.IsTemporary), "rule")} and {ImportResult.Count(c.Browsers.Count, "browser")} to {Path.GetFileName(path)}.", failed: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowTransfer("Couldn't export: " + e.Message, failed: true);
        }
    }

    [RelayCommand]
    private async Task ImportDataAsync()
    {
        if (_dialogs is null) return;
        var path = await _dialogs.PickOpenJsonAsync("Import rules and browsers");
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > ConfigTransfer.MaxFileBytes) throw new InvalidDataException("That file is too big to be a NudgeJudge export.");
            var export = ConfigTransfer.Read(await File.ReadAllBytesAsync(path));
            // Nothing changes yet: the review shows what would, and Import there applies it.
            TransferMessage = null;
            ImportReview = new ImportReviewViewModel(_config, export, Path.GetFileName(path), result =>
            {
                ImportReview = null;
                if (result is not null) ShowTransfer(result.Describe(), failed: false);
            }, ProgramExists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowTransfer(e is InvalidDataException ? e.Message : "Couldn't import: " + e.Message, failed: true);
        }
    }

    /// <summary>The import being reviewed, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewingImport))]
    public partial ImportReviewViewModel? ImportReview { get; set; }

    public bool IsReviewingImport => ImportReview is not null;

    /// <summary>How the import review checks a program is installed. Tests replace it; null looks on disk and PATH.</summary>
    public Func<string, bool>? ProgramExists { get; set; }

    private void ShowTransfer(string message, bool failed)
    {
        TransferFailed = failed;
        TransferMessage = message;
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        HasTriedRegistering = true;
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
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshRegistrationAsync()
    {
        if (RegisterCommand.IsRunning) return;
        // Asked again mid-check (Windows writes http and https as separate changes): check once more after,
        // so the last answer reflects the last change.
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                try
                {
                    Apply(await _registrar.GetStatusAsync());
                }
                catch (Exception e)
                {
                    Apply(new RegistrationStatus(RegistrationState.Unknown, "Couldn't check the default browser: " + e.Message));
                }
            } while (_refreshAgain);
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

    partial void OnExtensionConnectedChanged(bool value)
    {
        if (_loading) return;
        try
        {
            _extension.SetEnabled(value);
            ExtensionMessage = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ExtensionMessage = (value ? "Couldn't connect the extension: " : "Couldn't disconnect the extension: ") + e.Message;
        }
        LoadExtension();
    }

    private void LoadExtension()
    {
        var was = _loading;
        _loading = true;
        try
        {
            ExtensionConnected = _extension.IsSupported && _extension.IsEnabled();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ExtensionConnected = false;
        }
        finally
        {
            _loading = was;
        }
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
            LoadExtension();
        }
        finally
        {
            _loading = false;
        }
    }
}
