using System.Text;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Browsers.Discovery;

/// <summary>
/// Finds browsers from freedesktop <c>.desktop</c> entries in the XDG data directories (which also covers
/// Snap and Flatpak exports). An entry counts as a browser when it is in the <c>WebBrowser</c> category or
/// handles both <c>http</c> and <c>https</c> links.
/// </summary>
public sealed class LinuxBrowserDiscovery(DiscoveryPaths? paths = null) : IBrowserDiscovery
{
    private const string MainGroup = "Desktop Entry";

    public bool IsSupported => true;

    public Task<IReadOnlyList<BrowserProfile>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(paths ?? DiscoveryPaths.FromEnvironment(), cancellationToken), cancellationToken);

    internal static IReadOnlyList<BrowserProfile> Discover(DiscoveryPaths paths, CancellationToken cancellationToken = default)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var browsers = new List<List<BrowserProfile>>();

        foreach (var dataDir in paths.DataDirs)
        {
            var root = Path.Combine(dataDir, "applications");
            foreach (var file in EnumerateDesktopFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Desktop-file id: path below applications/ with '/' turned into '-'. An id found earlier
                // shadows the same id further down, even when the earlier one hides the app.
                var id = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '-');
                if (!seenIds.Add(id)) continue;

                if (IniFile.TryLoad(file) is { } entry && FromEntry(id, entry, paths) is { Count: > 0 } found)
                    browsers.Add(found);
            }
        }

        return BrowserVariants.Distinct(
            browsers.OrderBy(b => b[0].Name, StringComparer.CurrentCultureIgnoreCase).SelectMany(b => b),
            ignorePathCase: false);
    }

    internal static List<BrowserProfile> FromEntry(string id, IniFile entry, DiscoveryPaths paths)
    {
        string? Get(string key) => entry.Get(MainGroup, key) is { } v ? Unescape(v) : null;

        if (Get("Type") != "Application") return [];
        if (IsTrue(Get("Hidden")) || IsTrue(Get("NoDisplay"))) return [];
        if (!IsBrowser(Get("Categories"), Get("MimeType"))) return [];
        if (id.Contains("nudgejudge", StringComparison.OrdinalIgnoreCase)) return [];

        var name = Get("Name");
        var exec = Get("Exec");
        if (string.IsNullOrWhiteSpace(name) || exec is null) return [];
        if (exec.Contains("nudgejudge", StringComparison.OrdinalIgnoreCase)) return [];
        if (Get("TryExec") is { Length: > 0 } tryExec && !paths.CanExecute(tryExec)) return [];

        if (ParseExec(exec) is not { Count: > 0 } command) return [];
        var real = BrowserVariants.WithoutEnv(command);
        if (real.Count == 0 || !paths.CanExecute(real[0])) return [];

        var (privateCommand, privateLabel) = FindPrivateAction(entry);
        return BrowserVariants.Expand(CleanName(name), command, privateCommand, privateLabel, paths);
    }

    private static (List<string>?, string?) FindPrivateAction(IniFile entry)
    {
        var actions = entry.Get(MainGroup, "Actions")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        foreach (var action in actions)
        {
            var group = "Desktop Action " + action;
            var label = entry.Get(group, "Name") is { } n ? Unescape(n) : "";
            if (!IsPrivateWord(action) && !IsPrivateWord(label)) continue;
            if (entry.Get(group, "Exec") is not { } exec || ParseExec(Unescape(exec)) is not { Count: > 0 } cmd) continue;

            var suffix = label.Contains("incognito", StringComparison.OrdinalIgnoreCase) ? "Incognito"
                : label.Contains("inprivate", StringComparison.OrdinalIgnoreCase) ? "InPrivate"
                : "Private";
            return (cmd, suffix);
        }
        return (null, null);

        static bool IsPrivateWord(string s) =>
            s.Contains("private", StringComparison.OrdinalIgnoreCase) || s.Contains("incognito", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBrowser(string? categories, string? mimeTypes)
    {
        var cats = (categories ?? "").Split(';', StringSplitOptions.TrimEntries);
        if (cats.Contains("WebBrowser", StringComparer.Ordinal)) return true;
        var mimes = (mimeTypes ?? "").Split(';', StringSplitOptions.TrimEntries);
        return mimes.Contains("x-scheme-handler/http", StringComparer.Ordinal) &&
               mimes.Contains("x-scheme-handler/https", StringComparer.Ordinal);
    }

    /// <summary>"Firefox Web Browser" → "Firefox": the category is implied on a browsers page.</summary>
    internal static string CleanName(string name)
    {
        name = name.Trim();
        const string suffix = " Web Browser";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length
            ? name[..^suffix.Length].TrimEnd()
            : name;
    }

    private static bool IsTrue(string? v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>String-value escapes from the Desktop Entry spec: <c>\s \n \t \r \\</c>.</summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var next = value[++i];
            switch (next)
            {
                case 's': sb.Append(' '); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(next); break; // leave Exec's own escapes for ParseExec
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits an <c>Exec</c> value into argv and resolves field codes: <c>%u %U %f %F</c> become
    /// <see cref="BrowserLauncher.UrlPlaceholder"/>, <c>%%</c> becomes <c>%</c>, and the rest are dropped.
    /// Flatpak's <c>@@u … @@</c> file-forwarding markers are removed. Returns null when the value is malformed.
    /// </summary>
    internal static List<string>? ParseExec(string exec)
    {
        var raw = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var inToken = false;

        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                    current.Append(exec[++i]);
                else if (c == '"')
                    inQuote = false;
                else
                    current.Append(c);
                continue;
            }
            if (c == '"')
            {
                inQuote = true;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) raw.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        if (inQuote) return null;
        if (inToken) raw.Add(current.ToString());

        var result = new List<string>();
        var urlPlaced = false;
        foreach (var token in raw)
        {
            if (token is "@@" or "@@u" or "@@f") continue;
            if (token is "%u" or "%U" or "%f" or "%F")
            {
                if (!urlPlaced) result.Add(BrowserLauncher.UrlPlaceholder);
                urlPlaced = true;
                continue;
            }

            var sb = new StringBuilder(token.Length);
            var hadCode = false;
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] != '%' || i + 1 == token.Length)
                {
                    sb.Append(token[i]);
                    continue;
                }
                var code = token[++i];
                if (code == '%')
                {
                    sb.Append('%');
                }
                else if (code is 'u' or 'U' or 'f' or 'F' && !urlPlaced)
                {
                    sb.Append(BrowserLauncher.UrlPlaceholder);
                    urlPlaced = true;
                }
                else
                {
                    hadCode = true; // %i %c %k and deprecated codes: dropped
                }
            }
            if (sb.Length > 0 || !hadCode) result.Add(sb.ToString());
        }
        return result;
    }

    private static IEnumerable<string> EnumerateDesktopFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
            return Directory.EnumerateFiles(root, "*.desktop", options).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
