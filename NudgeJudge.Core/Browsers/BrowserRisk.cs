using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Browsers;

/// <summary>Why a browser entry from somewhere else (an import) shouldn't be trusted without a look.</summary>
public enum BrowserWarning
{
    /// <summary>The program isn't a browser NudgeJudge knows (Firefox, Chrome, Edge, …).</summary>
    Unrecognised,

    /// <summary>The program is a script or a script interpreter (.sh, .bat, powershell, python, …).</summary>
    Script,

    /// <summary>The program doesn't exist on this computer.</summary>
    NotFound,

    /// <summary>The arguments include flags that change what the browser does in risky ways.</summary>
    RiskyArguments,
}

public sealed record BrowserAssessment(IReadOnlyList<BrowserWarning> Warnings, IReadOnlyList<string> RiskyArguments)
{
    public bool IsClean => Warnings.Count == 0;

    /// <summary>A few words per warning, for one-line places like a list row: "runs a script · risky arguments".</summary>
    public string Short => Warnings.Count == 0
        ? "imported"
        : string.Join(" · ", Warnings.Select(w => w switch
        {
            BrowserWarning.Unrecognised => "unknown program",
            BrowserWarning.Script => "runs a script",
            BrowserWarning.NotFound => "not installed",
            BrowserWarning.RiskyArguments => "risky arguments",
            _ => w.ToString(),
        }));

    /// <summary>The program-related reasons as sentences, without the risky arguments (shown on their own, in mono).</summary>
    public IEnumerable<string> ProgramReasons => Describe().Take(Warnings.Count(w => w != BrowserWarning.RiskyArguments));

    /// <summary>One line per warning, to show next to the browser.</summary>
    public IEnumerable<string> Describe() => Warnings.Select(w => w switch
    {
        BrowserWarning.Unrecognised => "Not a browser NudgeJudge recognises.",
        BrowserWarning.Script => "Runs a script or script interpreter, not a browser.",
        BrowserWarning.NotFound => "This program isn't on this computer.",
        BrowserWarning.RiskyArguments => "Risky arguments: " + string.Join(" ", RiskyArguments),
        _ => w.ToString(),
    });
}

/// <summary>
/// Looks at a browser entry's program and arguments for signs it isn't (just) a browser: an unknown program,
/// a script, a missing file, or flags that load extensions, open a debugging port, turn off security or
/// point the browser at someone else's profile.
/// </summary>
public static class BrowserRisk
{
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".bash", ".zsh", ".fish", ".csh", ".ksh", ".py", ".pyw", ".pl", ".rb", ".php", ".js", ".jse", ".mjs",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".wsf", ".wsh", ".hta", ".lnk", ".scr", ".com", ".msi",
        ".jar", ".appimage", ".desktop", ".command", ".scpt",
    };

    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "zsh", "fish", "dash", "csh", "ksh", "env", "cmd", "powershell", "pwsh", "python", "python3",
        "pythonw", "perl", "ruby", "node", "php", "java", "javaw", "wscript", "cscript", "mshta", "rundll32",
        "regsvr32", "msiexec", "osascript", "xdg-open", "open", "start", "nohup", "sudo", "pkexec", "doas", "curl", "wget",
    };

    /// <summary>Flags (without leading dashes, lower case) that change what a browser does in ways an attacker wants.</summary>
    private static readonly HashSet<string> RiskyFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chromium family
        "load-extension", "disable-extensions-except", "remote-debugging-port", "remote-debugging-pipe",
        "remote-debugging-address", "remote-allow-origins", "disable-web-security", "user-data-dir", "disk-cache-dir",
        "proxy-server", "proxy-pac-url", "host-resolver-rules", "host-rules", "ignore-certificate-errors",
        "ignore-certificate-errors-spki-list", "no-sandbox", "disable-site-isolation-trials", "allow-running-insecure-content",
        "unsafely-treat-insecure-origin-as-secure", "gpu-launcher", "renderer-cmd-prefix", "utility-cmd-prefix",
        "browser-subprocess-path", "enable-automation", "headless", "js-flags", "auto-open-devtools-for-tabs",
        "allow-file-access-from-files", "disable-features", "enable-features", "policy",
        // Firefox. (--profile isn't here: NudgeJudge itself opens Firefox profiles with it.)
        "marionette", "remote-allow-hosts", "remote-allow-origins", "start-debugger-server", "jsconsole", "devtools",
    };

    /// <param name="programExists">Whether a program path or name exists here. Defaults to looking on disk and on PATH.</param>
    public static BrowserAssessment Assess(BrowserProfile browser, Func<string, bool>? programExists = null)
    {
        programExists ??= ProgramExists;
        var warnings = new List<BrowserWarning>();
        var exe = browser.ExecutablePath.Trim();
        var args = CommandLine.Split(browser.Arguments ?? "");
        var name = BrowserFamily.ExecutableName(exe);

        var isScript = ScriptExtensions.Contains(Path.GetExtension(exe)) || Interpreters.Contains(name);
        var recognised = !isScript && (BrowserFamily.Of(exe) is not null ||
                         (name == "flatpak" && BrowserFamily.Known.Any(f => f.FlatpakId is { } id && args.Contains(id, StringComparer.Ordinal))));

        if (isScript) warnings.Add(BrowserWarning.Script);
        else if (!recognised) warnings.Add(BrowserWarning.Unrecognised);
        if (exe.Length == 0 || !programExists(exe)) warnings.Add(BrowserWarning.NotFound);

        var risky = args.Where(IsRisky).ToList();
        if (risky.Count > 0) warnings.Add(BrowserWarning.RiskyArguments);
        return new BrowserAssessment(warnings, risky);
    }

    private static bool IsRisky(string arg)
    {
        if (!arg.StartsWith('-')) return false;
        var flag = arg.TrimStart('-');
        var eq = flag.IndexOf('=');
        if (eq >= 0) flag = flag[..eq];
        return RiskyFlags.Contains(flag);
    }

    /// <summary>A full path that exists, or a bare name found on PATH (with PATHEXT on Windows).</summary>
    public static bool ProgramExists(string exe)
    {
        try
        {
            if (exe.Contains('/') || exe.Contains('\\')) return File.Exists(exe);
            var extensions = OperatingSystem.IsWindows()
                ? ["", .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries)]
                : new[] { "" };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (var ext in extensions)
                    if (File.Exists(Path.Combine(dir, exe + ext))) return true;
            return false;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
