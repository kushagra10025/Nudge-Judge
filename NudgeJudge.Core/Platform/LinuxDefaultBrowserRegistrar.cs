using System.Text;
using NudgeJudge.Core.Browsers.Discovery;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Freedesktop registration: installs <c>nudgejudge.desktop</c> in <c>$XDG_DATA_HOME/applications</c> and makes it
/// the default handler for <c>x-scheme-handler/http</c> and <c>https</c> with <c>xdg-mime</c>.
/// </summary>
/// <remarks>
/// <c>xdg-settings set default-web-browser</c> is deliberately not used: on most desktops it also claims
/// <c>text/html</c>, which would send local HTML files here, and the router only accepts http(s) URLs.
/// </remarks>
public sealed class LinuxDefaultBrowserRegistrar(
    string dataHome,
    IReadOnlyList<string>? launchCommand,
    ICommandRunner runner,
    Func<byte[]?>? iconPng = null) : IDefaultBrowserRegistrar
{
    public const string DesktopId = "nudgejudge.desktop";
    private static readonly string[] Schemes = ["x-scheme-handler/http", "x-scheme-handler/https"];

    private const string MissingXdgUtils =
        "xdg-mime isn't installed, so NudgeJudge can't check or change the default browser. Install the xdg-utils package.";

    public static LinuxDefaultBrowserRegistrar FromEnvironment(IReadOnlyList<string>? launchCommand, ICommandRunner runner,
        Func<byte[]?>? iconPng = null)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } v && Path.IsPathRooted(v)
            ? v
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return new LinuxDefaultBrowserRegistrar(dataHome, launchCommand, runner, iconPng);
    }

    public string DesktopFilePath => Path.Combine(dataHome, "applications", DesktopId);
    public string IconPath => Path.Combine(dataHome, "nudgejudge", "nudgejudge.png");

    /// <summary>Wrapper used when the program path has whitespace (see <see cref="Launcher"/>).</summary>
    public string LauncherPath => Path.Combine(dataHome, "nudgejudge", "nudgejudge-open");

    public bool IsSupported => Exec is not null;

    /// <summary>The <c>Exec</c> line for this install, or null when the launch command can't be expressed.</summary>
    private string? Exec => launchCommand is { Count: > 0 } c && !c.Any(a => a.Any(char.IsControl))
        ? DesktopExec.Build(Launcher is null ? c : [LauncherPath])
        : null;

    /// <summary>
    /// A shell wrapper for the launch command, or null when Exec can run it directly. xdg-utils (1.1.x) finds an
    /// entry's program by cutting <c>Exec</c> at the first space and taking that literally, without removing quotes
    /// or escapes. So a program path Exec has to quote looks uninstalled, and <c>xdg-mime</c>/<c>xdg-open</c> skip the
    /// entry and fall back to another browser: a space, but also the "(1)" a browser adds to a second download
    /// (<c>NudgeJudge(1).AppImage</c>), or any other character the Desktop Entry spec reserves.
    /// </summary>
    private string? Launcher =>
        launchCommand is { Count: > 0 } c && NeedsWrapper(c[0]) && !NeedsWrapper(LauncherPath)
            ? "#!/bin/sh\n# Written by NudgeJudge when registering as the default browser.\nexec " +
              string.Join(' ', c.Select(ShellQuote)) + " \"$@\"\n"
            : null;

    /// <summary>True when <c>Exec</c> would quote or escape <paramref name="program"/>, which xdg-utils can't read back.</summary>
    internal static bool NeedsWrapper(string program) => DesktopExec.Build([program], withUrl: false) != program;

    public async Task<RegistrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Exec is not { } exec) return CantLocateSelf;

        var owns = new bool[Schemes.Length];
        for (var i = 0; i < Schemes.Length; i++)
        {
            var result = await runner.RunAsync("xdg-mime", ["query", "default", Schemes[i]], cancellationToken);
            if (!result.Started) return new(RegistrationState.Unknown, MissingXdgUtils);
            owns[i] = result.Succeeded && string.Equals(result.Output.Trim(), DesktopId, StringComparison.Ordinal);
        }

        var current = IniFile.TryLoad(DesktopFilePath)?.Get("Desktop Entry", "Exec") == exec &&
                      (Launcher is not { } script || ReadOrNull(LauncherPath) == script);
        return DefaultBrowserRegistration.Evaluate(owns[0], owns[1], current);
    }

    public async Task<RegistrationStatus> RegisterAsync(CancellationToken cancellationToken = default)
    {
        if (Exec is not { } exec) return CantLocateSelf;

        string? icon = null;
        try
        {
            icon = WriteIcon();
            if (Launcher is { } script)
            {
                WriteAtomic(LauncherPath, script);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(LauncherPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            WriteAtomic(DesktopFilePath, DesktopEntry(exec, icon));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(RegistrationState.NotRegistered, $"Couldn't write {DesktopFilePath}: {e.Message}");
        }

        // Refreshes the mimeinfo cache some desktops read; harmless (and optional) everywhere else.
        await runner.RunAsync("update-desktop-database", ["-q", Path.GetDirectoryName(DesktopFilePath)!], cancellationToken);

        var set = await runner.RunAsync("xdg-mime", ["default", DesktopId, .. Schemes], cancellationToken);
        if (!set.Started) return new(RegistrationState.Unknown, MissingXdgUtils);
        if (!set.Succeeded)
        {
            var detail = set.FirstErrorLine is { Length: > 0 } line ? $" ({line})" : "";
            return new(RegistrationState.NotRegistered, $"xdg-mime couldn't change the default browser{detail}.");
        }

        return await GetStatusAsync(cancellationToken);
    }

    private static RegistrationStatus CantLocateSelf { get; } = new(RegistrationState.Unsupported,
        "NudgeJudge can't tell how it was started, so it can't register itself. Run the installed app instead.");

    internal static string DesktopEntry(string exec, string? iconPath) => $"""
        [Desktop Entry]
        Type=Application
        Version=1.5
        Name=NudgeJudge
        GenericName=Link Router
        Comment=Opens each link in the browser your rules choose
        Exec={exec}
        Icon={EscapeValue(iconPath ?? "web-browser")}
        Terminal=false
        StartupNotify=false
        Categories=Network;WebBrowser;
        MimeType={string.Join(';', Schemes)};
        Keywords=browser;router;links;

        """;

    private string? WriteIcon()
    {
        if (iconPng?.Invoke() is not { Length: > 0 } bytes) return null;
        WriteAtomic(IconPath, bytes);
        return IconPath;
    }

    private static void WriteAtomic(string path, string text) => WriteAtomic(path, new UTF8Encoding(false).GetBytes(text));

    private static void WriteAtomic(string path, byte[] bytes) =>
        Config.AppPaths.WritePrivateFileAtomic(path, bytes, privateDirectory: false);

    private static string EscapeValue(string value) => value.Replace("\\", @"\\");

    internal static string ShellQuote(string arg) => "'" + arg.Replace("'", @"'\''") + "'";

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Builds a Desktop Entry <c>Exec</c> value, the inverse of the parsing done in browser discovery.</summary>
internal static class DesktopExec
{
    // Characters that force an argument to be quoted (Desktop Entry spec, "The Exec key").
    private const string Reserved = " \t\n\"'\\><~|&;$*?#()`";

    /// <summary>
    /// Exec value that runs <paramref name="command"/>, followed by the link (<c>%u</c>) unless <paramref name="withUrl"/>
    /// is false. Null if an argument can't be expressed.
    /// </summary>
    public static string? Build(IReadOnlyList<string> command, bool withUrl = true)
    {
        var parts = new List<string>(command.Count + 1);
        foreach (var arg in command)
        {
            if (arg.Length == 0 || arg.Any(char.IsControl)) return null;
            parts.Add(Quote(arg));
        }
        if (withUrl) parts.Add("%u");
        // The string-value escape for backslash applies on top of the Exec quoting rules.
        return string.Join(' ', parts).Replace("\\", @"\\");
    }

    private static string Quote(string arg)
    {
        arg = arg.Replace("%", "%%");
        if (arg.IndexOfAny(Reserved.ToCharArray()) < 0 && arg[0] != '=') return arg;

        var sb = new StringBuilder(arg.Length + 2).Append('"');
        foreach (var c in arg)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
