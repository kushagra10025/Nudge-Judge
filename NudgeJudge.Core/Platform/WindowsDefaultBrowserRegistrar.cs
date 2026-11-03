using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Registers NudgeJudge per user (HKCU, no admin) as a browser Windows can offer: a URL ProgID, a
/// <c>StartMenuInternet</c> client with Capabilities, and a <c>RegisteredApplications</c> entry.
/// Windows doesn't let apps make themselves the default (the UserChoice hash), so the last step opens
/// Default Apps and the user picks NudgeJudge there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDefaultBrowserRegistrar(IReadOnlyList<string>? launchCommand) : IDefaultBrowserRegistrar
{
    private const string AssociationsKey = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations";
    private const string SettingsUri = "ms-settings:defaultapps?registeredAppUser=" + WindowsRegistration.AppName;

    private WindowsRegistration.Plan? Plan => launchCommand is { Count: > 0 } c ? WindowsRegistration.Build(c) : null;

    public bool IsSupported => Plan is not null;

    public Task<RegistrationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ReadStatus, cancellationToken);

    public Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (Plan is not { } plan) return CantLocateSelf;
        try
        {
            foreach (var value in plan.Values)
            {
                using var key = Registry.CurrentUser.CreateSubKey(value.Key, writable: true);
                key.SetValue(value.Name ?? "", value.Data, RegistryValueKind.String);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new RegistrationStatus(RegistrationState.NotRegistered, $"Couldn't write the registry: {e.Message}");
        }

        // Tell Explorer the associations changed so Default Apps lists NudgeJudge straight away.
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

        var status = ReadStatus();
        if (status.State == RegistrationState.Registered) return status;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(SettingsUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new RegistrationStatus(RegistrationState.NotRegistered,
                "NudgeJudge is installed as a browser. Open Settings → Apps → Default apps and choose NudgeJudge.");
        }
        return new RegistrationStatus(RegistrationState.NotRegistered,
            "Default apps is open. Choose NudgeJudge there (“Set default”), then come back here.");
    }, cancellationToken);

    private RegistrationStatus ReadStatus()
    {
        if (Plan is not { } plan) return CantLocateSelf;
        try
        {
            var ownsHttp = UserChoice("http") == WindowsRegistration.ProgId;
            var ownsHttps = UserChoice("https") == WindowsRegistration.ProgId;
            using var command = Registry.CurrentUser.OpenSubKey(WindowsRegistration.ProgIdCommandKey);
            var current = command?.GetValue(null) as string == plan.OpenCommand;
            return DefaultBrowserRegistration.Evaluate(ownsHttp, ownsHttps, current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new RegistrationStatus(RegistrationState.Unknown, $"Couldn't read the default browser: {e.Message}");
        }
    }

    private static string? UserChoice(string scheme)
    {
        // Recent Windows 11 builds record the choice under UserChoiceLatest and keep UserChoice for older readers.
        foreach (var name in new[] { "UserChoiceLatest", "UserChoice" })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{AssociationsKey}\{scheme}\{name}");
            if (key?.GetValue("ProgId") is string { Length: > 0 } progId) return progId;
        }
        return null;
    }

    private static RegistrationStatus CantLocateSelf { get; } = new(RegistrationState.Unsupported,
        "NudgeJudge can't tell how it was started, so it can't register itself. Run the installed app instead.");

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}

/// <summary>The registry layout, kept free of registry calls so it can be tested on any OS.</summary>
internal static class WindowsRegistration
{
    public const string AppName = "NudgeJudge";
    public const string ProgId = "NudgeJudgeURL";
    public const string ProgIdKey = @"Software\Classes\" + ProgId;
    public const string ProgIdCommandKey = ProgIdKey + @"\shell\open\command";
    public const string ClientKey = @"Software\Clients\StartMenuInternet\" + AppName;
    public const string CapabilitiesKey = ClientKey + @"\Capabilities";
    private const string Description = "Opens each link in the browser your rules choose.";

    /// <param name="Name">Value name; null is the key's default value.</param>
    public sealed record Value(string Key, string? Name, string Data);

    public sealed record Plan(string OpenCommand, IReadOnlyList<Value> Values);

    /// <summary>Null when the command can't be written as a Windows command line.</summary>
    /// <param name="fileExists">Checks for the app's own launcher (see <see cref="IconFile"/>); defaults to the file system.</param>
    public static Plan? Build(IReadOnlyList<string> command, Func<string, bool>? fileExists = null)
    {
        // Windows paths can't contain quotes, so plain quoting is unambiguous.
        if (command.Any(a => a.Length == 0 || a.Contains('"') || a.Any(char.IsControl))) return null;

        var program = string.Join(' ', command.Select(a => $"\"{a}\""));
        var open = program + " \"%1\"";
        var icon = $"\"{IconFile(command, fileExists ?? File.Exists)}\",0";

        return new Plan(open,
        [
            new(ProgIdKey, null, "NudgeJudge Link"),
            new(ProgIdKey, "FriendlyTypeName", "NudgeJudge Link"),
            new(ProgIdKey + @"\Application", "ApplicationName", AppName),
            new(ProgIdKey + @"\Application", "ApplicationDescription", Description),
            new(ProgIdKey + @"\Application", "ApplicationIcon", icon),
            new(ProgIdKey + @"\DefaultIcon", null, icon),
            new(ProgIdCommandKey, null, open),

            new(ClientKey, null, AppName),
            new(ClientKey + @"\DefaultIcon", null, icon),
            new(ClientKey + @"\shell\open\command", null, program),
            new(CapabilitiesKey, "ApplicationName", AppName),
            new(CapabilitiesKey, "ApplicationDescription", Description),
            new(CapabilitiesKey, "ApplicationIcon", icon),
            new(CapabilitiesKey + @"\StartMenu", "StartMenuInternet", AppName),
            new(CapabilitiesKey + @"\URLAssociations", "http", ProgId),
            new(CapabilitiesKey + @"\URLAssociations", "https", ProgId),

            new(@"Software\RegisteredApplications", AppName, CapabilitiesKey),
        ]);
    }

    /// <summary>
    /// The file whose icon Windows shows for NudgeJudge. Usually the program itself. When the command is the shared
    /// <c>dotnet.exe</c> host running our dll (a per-user .NET install the launcher can't find on its own), that
    /// would be .NET's generic icon, so use the app's launcher next to the dll, which carries NudgeJudge's icon.
    /// Stays on <c>dotnet.exe</c> only when there's no launcher (built with UseAppHost off), rather than naming a
    /// missing file.
    /// </summary>
    internal static string IconFile(IReadOnlyList<string> command, Func<string, bool> fileExists)
    {
        var program = command[0];
        var name = program[(program.LastIndexOfAny(['\\', '/']) + 1)..];
        if (command.Count >= 2 &&
            string.Equals(name, "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            command[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var launcher = command[1][..^4] + ".exe";
            if (fileExists(launcher)) return launcher;
        }
        return program;
    }
}
