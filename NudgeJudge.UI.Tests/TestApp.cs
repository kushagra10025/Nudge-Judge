using Avalonia;
using Avalonia.Headless;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Platform;
using NudgeJudge.Services;

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

    public int StatusChecks { get; private set; }
    public int Registrations { get; private set; }

    public Task<RegistrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusChecks++;
        return Task.FromResult(Status);
    }

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
