using Avalonia;
using Avalonia.Headless;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Platform;
using NudgeJudge.Services;
using NudgeJudge.ViewModels;

[assembly: AvaloniaTestApplication(typeof(NudgeJudge.UI.Tests.TestApp))]

namespace NudgeJudge.UI.Tests;

/// <summary>The real <see cref="App"/> (themes, styles, icons) on Avalonia's headless platform.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
        .WithInterFont();
}

internal sealed class FakeRegistrar : IDefaultBrowserRegistrar
{
    public bool IsSupported { get; init; } = true;
    public RegistrationStatus Status { get; set; } = new(RegistrationState.NotRegistered, "Links open in another browser.");

    /// <summary>What RegisterAsync reports; defaults to becoming registered.</summary>
    public Func<RegistrationStatus> OnRegister { get; set; } = () => new(RegistrationState.Registered, "Links come here first.");

    /// <summary>When set, RegisterAsync waits on it, so a test can look at the in-progress state.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, GetStatusAsync waits on it, so a test can trigger another check mid-check.</summary>
    public TaskCompletionSource? StatusGate { get; set; }

    /// <summary>What the view model asked to be told about OS-side changes; a test calls it to simulate one.</summary>
    public Action? Changed { get; private set; }

    public int StatusChecks { get; private set; }
    public int Registrations { get; private set; }

    public async Task<RegistrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusChecks++;
        if (StatusGate is { } gate)
        {
            StatusGate = null;
            await gate.Task;
        }
        return Status;
    }

    public void WatchForChanges(Action changed) => Changed = changed;

    public async Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default)
    {
        Registrations++;
        if (Gate is not null) await Gate.Task;
        Status = OnRegister();
        return Status;
    }
}

internal sealed class MemoryConfigStore : IConfigStore
{
    public AppConfig Current { get; } = new();
    public string? LoadError => null;

    public void Update(Action<AppConfig> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
}

internal sealed class NoTheme : IThemeService
{
    public void Apply(ThemePreference preference) { }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeBoot : ISystemBoot
{
    public string CurrentBootId => "test-boot";
    public bool IsCurrentBoot(string bootId) => bootId == CurrentBootId;
}

internal sealed class FakeLauncher : IBrowserLauncher
{
    public List<(BrowserProfile Browser, Uri Url)> Launched { get; } = [];
    public void Launch(BrowserProfile browser, Uri url) => Launched.Add((browser, url));
}

internal sealed class NoDiscovery : IBrowserDiscovery
{
    public bool IsSupported => false;
    public Task<IReadOnlyList<BrowserProfile>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BrowserProfile>>([]);
}

internal sealed class FakeWindows : IWindowService, IDialogService
{
    public MainWindowViewModel? Main { get; set; }
    public void ShowMainWindow(AppPage? page = null)
    {
        if (page is { } p) Main?.Navigate(p);
    }
    public void ShowChooser() { }
    public void HideChooser() { }
    public void Quit() { }
    public Task<string?> PickExecutableAsync() => Task.FromResult<string?>(null);

    /// <summary>What the save and open pickers "choose"; null acts as cancel.</summary>
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public Task<string?> PickSaveJsonAsync(string title, string suggestedName) => Task.FromResult(SavePath);
    public Task<string?> PickOpenJsonAsync(string title) => Task.FromResult(OpenPath);
}

internal sealed class FakeStartup : ILaunchAtStartup
{
    public bool IsSupported { get; init; } = true;
    public bool Enabled { get; set; }

    /// <summary>When set, SetEnabled throws it instead of changing anything.</summary>
    public Exception? Fail { get; set; }

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool enabled)
    {
        if (Fail is not null) throw Fail;
        Enabled = enabled;
    }

    public void Refresh() { }
}
