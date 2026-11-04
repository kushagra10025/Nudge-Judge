using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Restarting, Failed }

/// <summary>
/// Looks for new releases on the user's schedule, and backs both the sidebar toast and Settings → Updates.
/// Updating is always the user's click: it downloads, then restarts into the new version.
/// </summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    private readonly IConfigStore _config;
    private readonly IUpdateService _updates;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private bool _loading;
    private bool _checkedThisRun;

    /// <summary>Wait after startup before the first automatic check, so it never competes with opening the app.</summary>
    public static TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a long-running (tray) instance asks itself whether a daily or weekly check is due.</summary>
    public static readonly TimeSpan ScheduleTick = TimeSpan.FromHours(1);

    public UpdatesViewModel(IConfigStore config, IUpdateService updates, IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        _config = config;
        _updates = updates;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        // Guarded: this placeholder must not be saved over the user's setting before Load reads it.
        _loading = true;
        Frequency = UpdateFrequencyOption.All[0];
        config.Changed += (_, _) => dispatcher.Post(Load);
        Load();
    }

    /// <summary>Runs just before the app closes to install an update: save pending edits, release the single-instance server.</summary>
    public Action? PreparingToRestart { get; set; }

    public IReadOnlyList<UpdateFrequencyOption> Frequencies => UpdateFrequencyOption.All;
    public bool IsSupported => _updates.IsSupported;
    public string CurrentVersion => _updates.CurrentVersion;

    [ObservableProperty] public partial bool CheckAutomatically { get; set; }
    [ObservableProperty] public partial UpdateFrequencyOption Frequency { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    public partial DateTimeOffset? LastChecked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsBusy), nameof(IsDownloading), nameof(IsRestarting), nameof(HasUpdate),
        nameof(CanUpdate), nameof(IsToastOpen), nameof(ToastFailed), nameof(CanDismiss), nameof(StatusText), nameof(ToastTitle),
        nameof(ToastDetail))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(UpdateCommand))]
    public partial UpdateStage Stage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersion), nameof(HasUpdate), nameof(CanUpdate), nameof(IsToastOpen), nameof(ToastFailed),
        nameof(StatusText), nameof(ToastTitle), nameof(ToastDetail), nameof(UpdateLabel))]
    public partial AvailableUpdate? Available { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToastDetail))]
    public partial int Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ToastDetail))]
    public partial string Error { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToastOpen))]
    public partial bool ToastDismissed { get; private set; }

    public string AvailableVersion => Available?.Version ?? "";
    public string UpdateLabel => Available is null ? "Update" : $"Update to {Available.Version}";
    public bool IsChecking => Stage == UpdateStage.Checking;
    public bool IsDownloading => Stage == UpdateStage.Downloading;
    public bool IsRestarting => Stage == UpdateStage.Restarting;
    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Restarting;

    /// <summary>A newer release is known (and not already being installed).</summary>
    public bool HasUpdate => Available is not null && Stage is UpdateStage.Available or UpdateStage.Failed;
    public bool CanUpdate => HasUpdate;

    /// <summary>The sidebar toast: shown for a known update until dismissed, and always while installing one.</summary>
    public bool IsToastOpen => Available is not null &&
                               (Stage is UpdateStage.Downloading or UpdateStage.Restarting ||
                                (!ToastDismissed && Stage is UpdateStage.Available or UpdateStage.Failed));

    /// <summary>Installing an update failed; the toast offers to try again.</summary>
    public bool ToastFailed => Available is not null && Stage == UpdateStage.Failed;

    /// <summary>A download in progress can't be hidden, so it's never left running unseen.</summary>
    public bool CanDismiss => Stage is UpdateStage.Available or UpdateStage.Failed;

    public string ToastTitle => Stage switch
    {
        UpdateStage.Downloading => "Downloading update",
        UpdateStage.Restarting => "Restarting…",
        UpdateStage.Failed => "Update didn't finish",
        _ => "Update available",
    };

    public string ToastDetail => Stage switch
    {
        UpdateStage.Downloading => $"Version {AvailableVersion} · {Progress}%",
        UpdateStage.Restarting => $"Installing version {AvailableVersion}",
        UpdateStage.Failed => Error,
        _ => $"Version {AvailableVersion} is ready to install",
    };

    /// <summary>One line for Settings → Updates.</summary>
    public string StatusText => !IsSupported
        ? "Updates work in copies installed with the NudgeJudge installer. This one runs from a build folder."
        : Stage switch
        {
            UpdateStage.Checking => "Checking for updates…",
            UpdateStage.UpToDate => "You're on the latest version.",
            UpdateStage.Available => $"Version {AvailableVersion} is available.",
            UpdateStage.Downloading => $"Downloading version {AvailableVersion}… {Progress}%",
            UpdateStage.Restarting => $"Restarting to install version {AvailableVersion}…",
            UpdateStage.Failed => Error,
            _ => "",
        };

    public string LastCheckedText => LastChecked is { } at ? $"Last checked {at.ToLocalTime():g}" : "Not checked yet";

    /// <summary>
    /// Runs the automatic checks for as long as the app does: once shortly after start (if due), then whenever a
    /// daily or weekly check falls due. Ends when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task RunScheduleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return;
        try
        {
            await Task.Delay(StartupDelay, _time, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (IsDue()) await CheckAsync(manual: false);
                await Task.Delay(ScheduleTick, _time, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Whether an automatic check should run now, given the user's settings and the last check.</summary>
    public bool IsDue()
    {
        if (!IsSupported || !CheckAutomatically || IsBusy || Available is not null) return false;
        var interval = Frequency.Value switch
        {
            UpdateCheckFrequency.Daily => TimeSpan.FromDays(1),
            UpdateCheckFrequency.Weekly => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null,
        };
        if (interval is null) return !_checkedThisRun;
        return LastChecked is not { } last || _time.GetUtcNow() - last >= interval;
    }

    private bool CanCheckNow() => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (IsBusy) return;
        _checkedThisRun = true;
        var previous = Stage;
        Stage = UpdateStage.Checking;
        try
        {
            var found = await _updates.CheckAsync();
            Available = found;
            Error = "";
            // A check the user asked for brings the toast back even if they'd dismissed it.
            if (manual) ToastDismissed = false;
            Stage = found is null ? UpdateStage.UpToDate : UpdateStage.Available;
        }
        catch (Exception e)
        {
            // An automatic check failing (offline, rate-limited) isn't worth interrupting anyone over.
            Error = "Couldn't check for updates: " + e.Message;
            Stage = manual || previous == UpdateStage.Failed ? UpdateStage.Failed : UpdateStage.Idle;
            if (!manual) return;
        }
        RecordCheck();
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (Available is not { } update) return;
        Progress = 0;
        Error = "";
        Stage = UpdateStage.Downloading;
        try
        {
            await _updates.DownloadAsync(update, p => _dispatcher.Post(() => Progress = Math.Clamp(p, 0, 100)));
            Stage = UpdateStage.Restarting;
            PreparingToRestart?.Invoke();
            _updates.ApplyAndRestart(update);
        }
        catch (Exception e)
        {
            Error = "Couldn't install the update: " + e.Message;
            Stage = UpdateStage.Failed;
        }
    }

    [RelayCommand] private void DismissToast() => ToastDismissed = true;

    private void RecordCheck()
    {
        var now = _time.GetUtcNow();
        LastChecked = now;
        _config.Update(c => c.Settings.LastUpdateCheck = now);
    }

    partial void OnCheckAutomaticallyChanged(bool value)
    {
        if (!_loading) _config.Update(c => c.Settings.CheckForUpdates = value);
    }

    partial void OnFrequencyChanged(UpdateFrequencyOption value)
    {
        if (!_loading) _config.Update(c => c.Settings.UpdateCheckFrequency = value.Value);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _config.Current.Settings;
            CheckAutomatically = s.CheckForUpdates;
            Frequency = UpdateFrequencyOption.For(s.UpdateCheckFrequency);
            LastChecked = s.LastUpdateCheck;
        }
        finally
        {
            _loading = false;
        }
    }
}
