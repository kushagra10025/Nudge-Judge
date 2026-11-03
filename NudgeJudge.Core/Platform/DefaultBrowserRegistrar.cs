namespace NudgeJudge.Core.Platform;

public enum RegistrationState
{
    /// <summary>Couldn't tell (the OS tooling failed or is missing).</summary>
    Unknown,
    Registered,
    NotRegistered,
    /// <summary>This platform or install can't be registered from inside the app.</summary>
    Unsupported,
}

public readonly record struct RegistrationStatus(RegistrationState State, string Message);

/// <summary>Registers NudgeJudge as the OS handler for http/https.</summary>
public interface IDefaultBrowserRegistrar
{
    /// <summary>False where registration isn't built (macOS), so the UI can hide the button.</summary>
    bool IsSupported { get; }

    /// <summary>Asks the OS who handles http/https links now. May start a process, so it's async.</summary>
    Task<RegistrationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs NudgeJudge as a link handler and makes it the default where the OS allows that.
    /// Windows only lets the user pick, so there this opens Default Apps and reports NotRegistered
    /// until the user has chosen NudgeJudge.
    /// </summary>
    Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default);
}

public static class DefaultBrowserRegistration
{
    /// <summary>The registrar for the OS this process runs on.</summary>
    /// <param name="iconPng">Supplies the app icon as PNG bytes for the Linux launcher entry.</param>
    public static IDefaultBrowserRegistrar ForCurrentPlatform(Func<byte[]?>? iconPng = null)
    {
        var command = AppLaunchCommand.Current();
        if (OperatingSystem.IsWindows()) return new WindowsDefaultBrowserRegistrar(command);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            return LinuxDefaultBrowserRegistrar.FromEnvironment(command, new ProcessCommandRunner(), iconPng);
        return new StubDefaultBrowserRegistrar();
    }

    /// <summary>
    /// Turns "who handles http and https" into a status. Shared by the platform registrars so the
    /// wording and the edge cases (half-registered, moved since registering) stay the same everywhere.
    /// </summary>
    internal static RegistrationStatus Evaluate(bool ownsHttp, bool ownsHttps, bool handlerIsCurrent)
    {
        if (ownsHttp && ownsHttps)
        {
            return handlerIsCurrent
                ? new(RegistrationState.Registered, "Links from other apps come here first.")
                : new(RegistrationState.NotRegistered,
                    "NudgeJudge has moved since it was made the default, so links can't reach it. Make it default again to fix that.");
        }

        if (ownsHttp || ownsHttps)
        {
            var (caught, missed) = ownsHttps ? ("HTTPS", "HTTP") : ("HTTP", "HTTPS");
            return new(RegistrationState.NotRegistered,
                $"Only {caught} links come here; {missed} links go straight to another browser.");
        }

        return new(RegistrationState.NotRegistered, "Links from other apps open in another browser and skip your rules.");
    }
}

/// <summary>Used where registration isn't built yet (macOS; LSSetDefaultHandlerForURLScheme later).</summary>
public sealed class StubDefaultBrowserRegistrar : IDefaultBrowserRegistrar
{
    private static readonly RegistrationStatus NotBuilt =
        new(RegistrationState.Unsupported, "Making NudgeJudge the default browser isn't available on this system yet.");

    public bool IsSupported => false;

    public Task<RegistrationStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(NotBuilt);

    public Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default) => Task.FromResult(NotBuilt);
}
