namespace NudgeJudge.Core.Platform;

public enum RegistrationState { Unknown, Registered, NotRegistered, Unsupported }

public readonly record struct RegistrationStatus(RegistrationState State, string Message);

/// <summary>Registers NudgeJudge as the OS handler for http/https.</summary>
public interface IDefaultBrowserRegistrar
{
    RegistrationStatus GetStatus();
    Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Placeholder until per-OS registration lands: xdg-settings / .desktop on Linux, the
/// Capabilities registry keys on Windows, LSSetDefaultHandlerForURLScheme on macOS.
/// </summary>
public sealed class StubDefaultBrowserRegistrar : IDefaultBrowserRegistrar
{
    public RegistrationStatus GetStatus() =>
        new(RegistrationState.Unknown, "Default-browser registration isn't built yet for " + PlatformName + ".");

    public Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RegistrationStatus(RegistrationState.Unsupported,
            "Registration for " + PlatformName + " arrives in a later step."));

    private static string PlatformName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
}
