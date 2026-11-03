using System.Diagnostics;
using System.Text;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Browsers;

public interface IBrowserDiscovery
{
    /// <summary>False when this platform has no discovery yet, so the UI can say so instead of "nothing found".</summary>
    bool IsSupported { get; }

    /// <summary>Finds installed browsers. Results are new <see cref="BrowserProfile"/>s with <see cref="BrowserSource.Discovered"/>.</summary>
    Task<IReadOnlyList<BrowserProfile>> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>Used where discovery isn't built yet (macOS, LaunchServices later). Returns nothing.</summary>
public sealed class StubBrowserDiscovery : IBrowserDiscovery
{
    public bool IsSupported => false;

    public Task<IReadOnlyList<BrowserProfile>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BrowserProfile>>([]);
}

public interface IBrowserLauncher
{
    void Launch(BrowserProfile browser, Uri url);
}

/// <summary>
/// Starts a browser with an explicit argv list (no shell), so neither the URL nor the template
/// can inject commands. The URL has already passed <see cref="Security.UrlValidator"/>, so it always
/// starts with http(s): and can't be mistaken for a command-line flag.
/// </summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public const string UrlPlaceholder = "{url}";

    public void Launch(BrowserProfile browser, Uri url)
    {
        if (string.IsNullOrWhiteSpace(browser.ExecutablePath))
            throw new InvalidOperationException($"“{browser.Name}” has no executable path.");

        var psi = new ProcessStartInfo(browser.ExecutablePath) { UseShellExecute = false };
        foreach (var arg in BuildArguments(browser.Arguments, url))
            psi.ArgumentList.Add(arg);

        using var _ = Process.Start(psi);
    }

    public static IReadOnlyList<string> BuildArguments(string? template, Uri url)
    {
        var args = CommandLine.Split(template ?? "");
        var placed = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].Contains(UrlPlaceholder, StringComparison.Ordinal)) continue;
            args[i] = args[i].Replace(UrlPlaceholder, url.AbsoluteUri, StringComparison.Ordinal);
            placed = true;
        }
        if (!placed) args.Add(url.AbsoluteUri);
        return args;
    }
}

/// <summary>Splits an argument template on whitespace, honouring double and single quotes.</summary>
public static class CommandLine
{
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) result.Add(current.ToString());
                current.Clear();
                inToken = false;
                continue;
            }
            current.Append(c);
            inToken = true;
        }
        if (inToken) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Inverse of <see cref="Split"/>: quotes arguments that contain whitespace or quotes.
    /// Returns null for an argument holding both quote kinds, which <see cref="Split"/> can't represent.
    /// </summary>
    public static string? Join(IEnumerable<string> args)
    {
        var parts = new List<string>();
        foreach (var a in args)
        {
            if (a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\''))
                parts.Add(a);
            else if (!a.Contains('"'))
                parts.Add('"' + a + '"');
            else if (!a.Contains('\''))
                parts.Add('\'' + a + '\'');
            else
                return null;
        }
        return string.Join(' ', parts);
    }
}
