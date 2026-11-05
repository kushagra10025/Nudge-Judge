using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Platform;

namespace NudgeJudge.Core.NativeMessaging;

/// <summary>
/// Lets the browser extension reach NudgeJudge: a launcher that starts the app in native-host mode, plus a host
/// manifest each browser reads to find it. The files on disk are the source of truth, like launch at startup.
/// </summary>
public interface INativeHostRegistration
{
    /// <summary>False where it isn't built (macOS) or the app can't tell how it was started.</summary>
    bool IsSupported { get; }

    /// <summary>True when the launcher is in place.</summary>
    bool IsEnabled();

    /// <summary>Writes or removes everything. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Rewrites what's out of date (the app moved or updated, a browser was installed since). Does nothing when off.
    /// </summary>
    void Refresh();
}

public static class NativeHostRegistration
{
    /// <summary>
    /// Chromium extension IDs allowed to connect. The first is the development build (pinned by the public key in
    /// NudgeJudge.Extension/wxt.config.ts). Add the Chrome Web Store and Edge Add-ons IDs once each store assigns one.
    /// </summary>
    public static readonly IReadOnlyList<string> ChromiumExtensionIds =
    [
        "bmfgmoobkfipdpiabahjlafpfehjkjfg",
    ];

    /// <summary>The Firefox extension's ID (<c>browser_specific_settings.gecko.id</c>), the same in development and on AMO.</summary>
    public const string FirefoxExtensionId = "extension@nudgejudge";

    private const string Description = "NudgeJudge: opens links from the browser extension where your rules say";

    public static INativeHostRegistration ForCurrentPlatform()
    {
        var command = AppLaunchCommand.Current();
        if (OperatingSystem.IsWindows()) return new WindowsNativeHostRegistration(AppPaths.ConfigDirectory, command);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            return LinuxNativeHostRegistration.FromEnvironment(AppPaths.ConfigDirectory, command);
        return new StubNativeHostRegistration();
    }

    internal static string ChromiumManifest(string launcher) => Manifest(launcher, w =>
    {
        w.WriteStartArray("allowed_origins");
        foreach (var id in ChromiumExtensionIds) w.WriteStringValue($"chrome-extension://{id}/");
        w.WriteEndArray();
    });

    internal static string FirefoxManifest(string launcher) => Manifest(launcher, w =>
    {
        w.WriteStartArray("allowed_extensions");
        w.WriteStringValue(FirefoxExtensionId);
        w.WriteEndArray();
    });

    private static string Manifest(string launcher, Action<Utf8JsonWriter> allowed)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", NativeHost.Name);
            w.WriteString("description", Description);
            w.WriteString("path", launcher);
            w.WriteString("type", "stdio");
            allowed(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Writes <paramref name="text"/> unless the file already holds exactly that.</summary>
    internal static void WriteIfChanged(string path, string text, bool privateDirectory, bool executable = false)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
        }
        catch (IOException) { }
        AppPaths.WritePrivateFileAtomic(path, new UTF8Encoding(false).GetBytes(text), privateDirectory);
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// Linux: <c>native-host.sh</c> in the config folder, and a manifest in each browser's <c>NativeMessagingHosts</c>
/// folder. Chromium browsers only get one when their config folder exists (they're installed and have been run),
/// so nothing is created for browsers the user doesn't have; <see cref="Refresh"/> catches ones installed later.
/// </summary>
public sealed class LinuxNativeHostRegistration(string appConfigDir, string home, string configHome, IReadOnlyList<string>? launchCommand)
    : INativeHostRegistration
{
    /// <summary>Chromium-family config folders under <c>$XDG_CONFIG_HOME</c>.</summary>
    internal static readonly string[] ChromiumConfigDirs =
    [
        "google-chrome", "google-chrome-beta", "google-chrome-unstable", "chromium",
        "BraveSoftware/Brave-Browser", "microsoft-edge", "microsoft-edge-beta", "microsoft-edge-dev", "vivaldi",
    ];

    public static LinuxNativeHostRegistration FromEnvironment(string appConfigDir, IReadOnlyList<string>? launchCommand)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } v && Path.IsPathRooted(v)
            ? v
            : Path.Combine(home, ".config");
        return new LinuxNativeHostRegistration(appConfigDir, home, configHome, launchCommand);
    }

    public string LauncherPath => Path.Combine(appConfigDir, "native-host.sh");

    private string ManifestFile => NativeHost.Name + ".json";

    public bool IsSupported => Script is not null;

    internal string? Script => launchCommand is { Count: > 0 } c && c.All(a => a.Length > 0 && !a.Any(char.IsControl))
        ? "#!/bin/sh\n# Started by your browser for the NudgeJudge extension. Written by NudgeJudge; don't edit.\nexec " +
          string.Join(' ', c.Select(ShellQuote)) + " " + NativeHost.Flag + " \"$@\"\n"
        : null;

    /// <summary>Where manifests go now: Firefox's folder always, Chromium browsers' when the browser is there.</summary>
    internal IEnumerable<(string Path, bool Firefox)> ManifestTargets()
    {
        yield return (Path.Combine(home, ".mozilla", "native-messaging-hosts", ManifestFile), true);
        // Firefox 147+ keeps a new profile under XDG; it reads this folder then.
        if (Directory.Exists(Path.Combine(configHome, "mozilla")))
            yield return (Path.Combine(configHome, "mozilla", "native-messaging-hosts", ManifestFile), true);
        foreach (var dir in ChromiumConfigDirs)
        {
            var root = Path.Combine(configHome, dir);
            if (Directory.Exists(root)) yield return (Path.Combine(root, "NativeMessagingHosts", ManifestFile), false);
        }
    }

    /// <summary>Every place a manifest of ours could be, for removal.</summary>
    private IEnumerable<string> AllManifestPaths() =>
    [
        Path.Combine(home, ".mozilla", "native-messaging-hosts", ManifestFile),
        Path.Combine(configHome, "mozilla", "native-messaging-hosts", ManifestFile),
        .. ChromiumConfigDirs.Select(d => Path.Combine(configHome, d, "NativeMessagingHosts", ManifestFile)),
    ];

    public bool IsEnabled() => File.Exists(LauncherPath);

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            foreach (var path in AllManifestPaths()) NativeHostRegistration.DeleteIfExists(path);
            NativeHostRegistration.DeleteIfExists(LauncherPath);
            return;
        }
        Write();
    }

    public void Refresh()
    {
        if (Script is not null && IsEnabled()) Write();
    }

    private void Write()
    {
        if (Script is not { } script) throw new IOException("NudgeJudge can't tell how it was started, so browsers can't start it.");
        NativeHostRegistration.WriteIfChanged(LauncherPath, script, privateDirectory: true, executable: true);
        foreach (var (path, firefox) in ManifestTargets())
        {
            NativeHostRegistration.WriteIfChanged(path, firefox
                ? NativeHostRegistration.FirefoxManifest(LauncherPath)
                : NativeHostRegistration.ChromiumManifest(LauncherPath), privateDirectory: false);
        }
    }

    /// <summary>POSIX single quotes: everything literal, a quote itself written as <c>'\''</c>.</summary>
    internal static string ShellQuote(string s) => "'" + s.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// Windows: <c>native-host.bat</c> and two manifests in the config folder, found through
/// <c>HKCU\Software\…\NativeMessagingHosts\com.nudgejudge.host</c> for each browser vendor. No admin needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeHostRegistration(string appConfigDir, IReadOnlyList<string>? launchCommand) : INativeHostRegistration
{
    /// <summary>Registry parents read by Chromium browsers (Vivaldi and others read Chrome's).</summary>
    internal static readonly string[] ChromiumKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts", @"Software\Chromium\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    ];

    internal const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts";

    public string LauncherPath => Path.Combine(appConfigDir, "native-host.bat");
    private string ChromiumManifestPath => Path.Combine(appConfigDir, "native-host-chromium.json");
    private string FirefoxManifestPath => Path.Combine(appConfigDir, "native-host-firefox.json");

    public bool IsSupported => Script is not null;

    internal string? Script => WindowsBatch(launchCommand);

    /// <summary>A batch file that runs the app in native-host mode, passing on the browser's arguments.</summary>
    internal static string? WindowsBatch(IReadOnlyList<string>? command)
    {
        // Windows paths can't contain quotes; '%' would expand inside a batch file even when quoted.
        if (command is not { Count: > 0 } c || c.Any(a => a.Length == 0 || a.Contains('"') || a.Any(char.IsControl))) return null;
        var line = string.Join(' ', c.Select(a => "\"" + a.Replace("%", "%%", StringComparison.Ordinal) + "\""));
        return "@echo off\r\nrem Started by your browser for the NudgeJudge extension. Written by NudgeJudge; don't edit.\r\n" +
               line + " " + NativeHost.Flag + " %*\r\n";
    }

    public bool IsEnabled() => File.Exists(LauncherPath);

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                foreach (var key in ChromiumKeys.Append(FirefoxKey))
                    Registry.CurrentUser.DeleteSubKeyTree(key + @"\" + NativeHost.Name, throwOnMissingSubKey: false);
                NativeHostRegistration.DeleteIfExists(ChromiumManifestPath);
                NativeHostRegistration.DeleteIfExists(FirefoxManifestPath);
                NativeHostRegistration.DeleteIfExists(LauncherPath);
                return;
            }
            Write();
        }
        catch (System.Security.SecurityException e)
        {
            throw new UnauthorizedAccessException(e.Message, e);
        }
    }

    public void Refresh()
    {
        if (Script is not null && IsEnabled()) Write();
    }

    private void Write()
    {
        if (Script is not { } script) throw new IOException("NudgeJudge can't tell how it was started, so browsers can't start it.");
        NativeHostRegistration.WriteIfChanged(LauncherPath, script, privateDirectory: true);
        NativeHostRegistration.WriteIfChanged(ChromiumManifestPath, NativeHostRegistration.ChromiumManifest(LauncherPath), privateDirectory: true);
        NativeHostRegistration.WriteIfChanged(FirefoxManifestPath, NativeHostRegistration.FirefoxManifest(LauncherPath), privateDirectory: true);
        foreach (var key in ChromiumKeys) SetDefault(key, ChromiumManifestPath);
        SetDefault(FirefoxKey, FirefoxManifestPath);
    }

    private static void SetDefault(string parent, string manifest)
    {
        using var key = Registry.CurrentUser.CreateSubKey(parent + @"\" + NativeHost.Name, writable: true);
        if (key.GetValue(null) as string != manifest) key.SetValue(null, manifest, RegistryValueKind.String);
    }
}

/// <summary>Used where the extension can't be connected yet (macOS).</summary>
public sealed class StubNativeHostRegistration : INativeHostRegistration
{
    public bool IsSupported => false;
    public bool IsEnabled() => false;
    public void SetEnabled(bool enabled) => throw new IOException("The browser extension isn't available on this system yet.");
    public void Refresh() { }
}
