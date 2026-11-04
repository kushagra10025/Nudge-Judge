using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.Core.Models;
using NudgeJudge.Services;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.UI.Tests;

/// <summary>Update checks, the sidebar toast, and Settings → Updates.</summary>
public sealed class UpdateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    private static (UpdatesViewModel Vm, FakeUpdates Updates, MemoryConfigStore Config, ManualTime Time) Make(
        Action<AppSettings>? setup = null, bool supported = true)
    {
        var config = new MemoryConfigStore();
        setup?.Invoke(config.Current.Settings);
        var updates = new FakeUpdates { IsSupported = supported };
        var time = new ManualTime(Start);
        return (new UpdatesViewModel(config, updates, new InlineDispatcher(), time), updates, config, time);
    }

    [AvaloniaFact]
    public async Task A_found_update_opens_the_toast_until_dismissed()
    {
        var (vm, updates, _, _) = Make();
        updates.Next = new AvailableUpdate("2.0.0", null);

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(UpdateStage.Available, vm.Stage);
        Assert.True(vm.IsToastOpen);
        Assert.Equal("Update to 2.0.0", vm.UpdateLabel);

        vm.DismissToastCommand.Execute(null);
        Assert.False(vm.IsToastOpen);
        // Still offered in Settings.
        Assert.True(vm.HasUpdate);

        // Asking again brings it back.
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.True(vm.IsToastOpen);
    }

    [AvaloniaFact]
    public async Task Updating_downloads_then_saves_and_restarts()
    {
        var (vm, updates, _, _) = Make();
        updates.Next = new AvailableUpdate("2.0.0", null);
        await vm.CheckNowCommand.ExecuteAsync(null);
        var prepared = false;
        vm.PreparingToRestart = () =>
        {
            Assert.Equal(1, updates.Downloads);
            prepared = true;
        };

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.True(prepared);
        Assert.Equal("2.0.0", updates.Applied?.Version);
        Assert.Equal(100, vm.Progress);
        Assert.Equal(UpdateStage.Restarting, vm.Stage);
        Assert.True(vm.IsToastOpen);
        Assert.False(vm.CanDismiss);
    }

    [AvaloniaFact]
    public async Task A_failed_download_says_why_and_offers_to_try_again()
    {
        var (vm, updates, _, _) = Make();
        updates.Next = new AvailableUpdate("2.0.0", null);
        await vm.CheckNowCommand.ExecuteAsync(null);
        updates.DownloadFailure = new HttpRequestException("Connection reset.");

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateStage.Failed, vm.Stage);
        Assert.True(vm.ToastFailed);
        Assert.True(vm.IsToastOpen);
        Assert.Contains("Connection reset.", vm.ToastDetail);
        Assert.Null(updates.Applied);
        Assert.True(vm.UpdateCommand.CanExecute(null));

        updates.DownloadFailure = null;
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal("2.0.0", updates.Applied?.Version);
    }

    [AvaloniaFact]
    public async Task An_automatic_check_that_fails_stays_quiet()
    {
        var (vm, updates, config, _) = Make();
        updates.CheckFailure = new HttpRequestException("No network.");
        UpdatesViewModel.StartupDelay = TimeSpan.Zero;
        using var stop = new CancellationTokenSource();

        var schedule = vm.RunScheduleAsync(stop.Token);
        await updates.Checked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await schedule;

        Assert.Equal(UpdateStage.Idle, vm.Stage);
        Assert.False(vm.IsToastOpen);
        Assert.Null(config.Current.Settings.LastUpdateCheck);
    }

    [AvaloniaFact]
    public async Task A_manual_check_that_fails_says_so()
    {
        var (vm, updates, _, _) = Make();
        updates.CheckFailure = new HttpRequestException("No network.");

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(UpdateStage.Failed, vm.Stage);
        Assert.Contains("No network.", vm.StatusText);
        Assert.False(vm.IsToastOpen);
    }

    [AvaloniaFact]
    public async Task Startup_checks_run_once_per_launch()
    {
        var (vm, _, config, _) = Make(s => s.UpdateCheckFrequency = UpdateCheckFrequency.Startup);
        Assert.True(vm.IsDue());

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.False(vm.IsDue());
        Assert.Equal(Start, config.Current.Settings.LastUpdateCheck);
    }

    [AvaloniaFact]
    public void Daily_and_weekly_checks_follow_the_last_check_across_restarts()
    {
        var (daily, _, _, time) = Make(s =>
        {
            s.UpdateCheckFrequency = UpdateCheckFrequency.Daily;
            s.LastUpdateCheck = Start.AddHours(-23);
        });
        Assert.False(daily.IsDue());
        time.Now = Start.AddHours(1);
        Assert.True(daily.IsDue());

        var (weekly, _, _, weeklyTime) = Make(s =>
        {
            s.UpdateCheckFrequency = UpdateCheckFrequency.Weekly;
            s.LastUpdateCheck = Start.AddDays(-2);
        });
        Assert.False(weekly.IsDue());
        weeklyTime.Now = Start.AddDays(5);
        Assert.True(weekly.IsDue());
    }

    [AvaloniaFact]
    public void Turned_off_or_not_installed_never_checks_on_its_own()
    {
        Assert.False(Make(s => s.CheckForUpdates = false).Vm.IsDue());

        var (vm, _, _, _) = Make(supported: false);
        Assert.False(vm.IsDue());
        Assert.False(vm.CheckNowCommand.CanExecute(null));
        Assert.Contains("installer", vm.StatusText);
    }

    [AvaloniaFact]
    public void Settings_save_as_they_change()
    {
        var (vm, _, config, _) = Make();

        vm.CheckAutomatically = false;
        vm.Frequency = UpdateFrequencyOption.For(UpdateCheckFrequency.Weekly);

        Assert.False(config.Current.Settings.CheckForUpdates);
        Assert.Equal(UpdateCheckFrequency.Weekly, config.Current.Settings.UpdateCheckFrequency);
    }

    [AvaloniaFact]
    public void Opening_settings_keeps_the_saved_frequency_and_theme()
    {
        var config = new MemoryConfigStore();
        config.Current.Settings.UpdateCheckFrequency = UpdateCheckFrequency.Weekly;
        config.Current.Settings.Theme = ThemePreference.Dark;

        var updates = new UpdatesViewModel(config, new FakeUpdates(), new InlineDispatcher());
        _ = new SettingsViewModel(config, new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), new FakeStartup(), updates);

        Assert.Equal(UpdateCheckFrequency.Weekly, config.Current.Settings.UpdateCheckFrequency);
        Assert.Equal(ThemePreference.Dark, config.Current.Settings.Theme);
    }

    [AvaloniaFact]
    public async Task Settings_page_shows_the_update_and_about()
    {
        var (updates, fake, config, _) = Make();
        fake.Next = new AvailableUpdate("2.0.0", null);
        await updates.CheckNowCommand.ExecuteAsync(null);
        var settings = new SettingsViewModel(config, new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), new FakeStartup(), updates);

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var text = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Update to 2.0.0", text);
        Assert.Contains("v1.0.0", text);
        Assert.Contains(window.GetVisualDescendants().OfType<HyperlinkButton>(), b => b.NavigateUri == settings.RepositoryUrl);
    }
}

internal sealed class FakeUpdates : IUpdateService
{
    public bool IsSupported { get; init; } = true;
    public string CurrentVersion => "1.0.0";
    public AvailableUpdate? Next { get; set; }
    public Exception? CheckFailure { get; set; }
    public Exception? DownloadFailure { get; set; }
    public int Downloads { get; private set; }
    public AvailableUpdate? Applied { get; private set; }
    public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checked.TrySetResult();
        return CheckFailure is { } e ? Task.FromException<AvailableUpdate?>(e) : Task.FromResult(Next);
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
    {
        if (DownloadFailure is { } e) return Task.FromException(e);
        Downloads++;
        progress(50);
        progress(100);
        return Task.CompletedTask;
    }

    public void ApplyAndRestart(AvailableUpdate update) => Applied = update;
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
