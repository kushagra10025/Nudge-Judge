using System.Text.RegularExpressions;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Rules;

/// <summary>A compiled, reusable URL predicate.</summary>
public sealed class CompiledPattern
{
    internal CompiledPattern(Func<LinkInput, bool> predicate) => Predicate = predicate;

    /// <summary>The test itself, for the rule engine to call without going through this wrapper.</summary>
    internal Func<LinkInput, bool> Predicate { get; }

    /// <exception cref="RegexMatchTimeoutException">The pattern exceeded its time budget.</exception>
    public bool IsMatch(Uri url) => Predicate(new LinkInput(url, null));
}

public readonly record struct PatternCompileResult(CompiledPattern? Pattern, string? Error)
{
    public bool Success => Pattern is not null;
}

/// <summary>
/// Turns user patterns into predicates. Domains, wildcards and path prefixes compile to plain string
/// comparisons (<see cref="StarGlob"/>); only user-written regular expressions use the regex engine.
/// ReDoS defence, in layers: every regex (user-written or generated from a glob) runs on
/// <see cref="RegexOptions.NonBacktracking"/>, which matches in linear time and rejects the
/// constructs that need backtracking; patterns are length-capped; and a match timeout backs both up.
/// </summary>
public static class PatternCompiler
{
    public const int MaxPatternLength = 512;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    public static PatternCompileResult Compile(MatchKind type, string? pattern, bool caseSensitive = false)
    {
        pattern = pattern?.Trim() ?? "";
        if (pattern.Length == 0)
            return Fail("Enter a pattern.");
        if (pattern.Length > MaxPatternLength)
            return Fail($"Patterns are limited to {MaxPatternLength} characters.");
        if (pattern.Any(char.IsControl))
            return Fail("The pattern contains control characters.");

        return type switch
        {
            MatchKind.Domain => CompileDomain(pattern),
            MatchKind.Wildcard => CompileWildcard(pattern),
            MatchKind.PathPrefix => CompilePathPrefix(pattern, caseSensitive),
            MatchKind.Regex => CompileRegex(pattern, caseSensitive),
            _ => Fail("Unknown match type."),
        };
    }

    /// <summary>Suggests a sensible wildcard rule for a URL, e.g. https://jira.acme.io/x → *.acme.io.</summary>
    public static string SuggestWildcard(Uri url)
    {
        var host = url.IdnHost.TrimEnd('.');
        var labels = host.Split('.');
        // Keep the registrable-looking tail (last two labels; three for short ccTLD pairs like co.uk).
        var keep = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
        if (labels.Length <= keep || IsIpAddress(host))
            return host;
        return "*." + string.Join('.', labels[^keep..]);
    }

    private static PatternCompileResult CompileDomain(string pattern)
    {
        if (!TrySplitHostAndPath(pattern, out var host, out var path, out var error))
            return Fail(error);
        if (path.Length > 0)
            return Fail("A domain rule matches the host only. Use Path prefix to include a path.");
        if (host.Contains('*') || host.Contains('?'))
            return Fail("Domain rules don't take wildcards. Switch to Wildcard for patterns like *.example.com.");

        return Ok(link => string.Equals(link.Host, host, StringComparison.Ordinal));
    }

    private static PatternCompileResult CompileWildcard(string pattern)
    {
        if (!TrySplitHostAndPath(pattern, out var host, out var path, out var error))
            return Fail(error);

        var hostGlob = new HostGlob(host);
        if (path.Length == 0) return Ok(hostGlob.IsMatch);

        // Hosts are case-insensitive; paths are matched case-insensitively for globs as well,
        // which is what people expect when typing a quick pattern. ('?' starts a query, so it never gets here.)
        var pathGlob = StarGlob.TryCreate(path)!;
        return Ok(link => hostGlob.IsMatch(link) && pathGlob.IsMatch(link.Path));
    }

    private static PatternCompileResult CompilePathPrefix(string pattern, bool caseSensitive)
    {
        if (!TrySplitHostAndPath(pattern, out var host, out var path, out var error))
            return Fail(error);
        if (path.Length == 0)
            return Fail("Add a path after the host, e.g. github.com/acme.");
        if (path.Contains('*') || path.Contains('?'))
            return Fail("Path prefixes are literal. Use Wildcard for * in paths.");

        var hostGlob = new HostGlob(host);
        var prefix = path.TrimEnd('/');
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return Ok(link =>
        {
            if (!hostGlob.IsMatch(link)) return false;
            var p = link.Path;
            if (prefix.Length == 0) return true;
            // Match on segment boundaries: /acme matches /acme and /acme/x but not /acmecorp.
            return p.StartsWith(prefix, comparison) && (p.Length == prefix.Length || p[prefix.Length] == '/');
        });
    }

    private static PatternCompileResult CompileRegex(string pattern, bool caseSensitive)
    {
        Regex regex;
        try
        {
            regex = BuildRegex(pattern, caseSensitive);
        }
        catch (NotSupportedException)
        {
            return Fail("Lookarounds, backreferences and atomic groups aren't supported: they make matching slow and unsafe.");
        }
        catch (RegexParseException ex)
        {
            return Fail(DescribeRegexError(ex));
        }
        catch (ArgumentException)
        {
            return Fail("Not a valid regular expression.");
        }

        return Ok(link => regex.IsMatch(link.AbsoluteUri));
    }

    /// <summary>Plain-language version of .NET's parser message, pointing at the character that broke it.</summary>
    internal static string DescribeRegexError(RegexParseException ex)
    {
        var problem = ex.Error switch
        {
            RegexParseError.InsufficientClosingParentheses => "a ( is never closed",
            RegexParseError.InsufficientOpeningParentheses => "there's a ) with no ( before it",
            RegexParseError.UnterminatedBracket => "a [ is never closed",
            RegexParseError.UnescapedEndingBackslash => @"it ends with a lone \ (write \\ to match a backslash)",
            RegexParseError.QuantifierAfterNothing => @"a *, + or ? has nothing before it to repeat (put \ in front to match the character itself)",
            RegexParseError.NestedQuantifiersNotParenthesized => "two repeat marks are stacked, like a** (wrap the first part in brackets)",
            RegexParseError.ReversedCharacterRange => "a range like [z-a] runs backwards",
            RegexParseError.ReversedQuantifierRange => "a count like {5,2} runs backwards",
            RegexParseError.UnrecognizedEscape => @"it uses an escape like \q that doesn't exist",
            _ => null,
        };
        var where = ex.Offset > 0 ? $" (at character {ex.Offset})" : "";
        return problem is null
            ? $"Not a valid regular expression{where}."
            : $"Not a valid regular expression: {problem}{where}.";
    }

    internal static Regex BuildRegex(string pattern, bool caseSensitive)
    {
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, MatchTimeout);
    }

    /// <summary>Accepts "host", "host/path" or a full URL and splits it into lower-case host and path.</summary>
    private static bool TrySplitHostAndPath(string pattern, out string host, out string path, out string error)
    {
        host = path = error = "";
        var p = pattern;
        var scheme = p.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) p = p[(scheme + 3)..];

        var cut = p.IndexOfAny(['?', '#']);
        if (cut >= 0) p = p[..cut];

        var slash = p.IndexOf('/');
        host = (slash >= 0 ? p[..slash] : p).TrimEnd('.').ToLowerInvariant();
        path = slash >= 0 ? p[slash..] : "";
        if (path == "/") path = "";

        if (host.Length == 0)
        {
            error = "Enter a host, e.g. example.com.";
            return false;
        }
        if (host.Any(c => char.IsWhiteSpace(c) || c is '@' or '\\'))
        {
            error = "The host contains characters that can't appear in a domain.";
            return false;
        }
        if (host.Contains(':'))
            host = host[..host.IndexOf(':')]; // ports aren't matched

        // URLs are matched on their ASCII (punycode) host, so "münchen.de" must become "xn--mnchen-3ya.de".
        try
        {
            var idn = new System.Globalization.IdnMapping();
            host = string.Join('.', host.Split('.').Select(l =>
                l.Any(c => c > 127) && !l.Contains('*') && !l.Contains('?') ? idn.GetAscii(l) : l));
        }
        catch (ArgumentException)
        {
            error = "The host isn't a valid domain name.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The host part of a valid domain, wildcard or path-prefix pattern: lower case, punycode, and possibly with
    /// <c>*</c> (where a leading <c>*.</c> also covers the bare domain). Null for regular expressions and invalid patterns.
    /// </summary>
    public static string? HostOf(MatchKind type, string? pattern)
    {
        if (type == MatchKind.Regex || !Compile(type, pattern).Success) return null;
        return TrySplitHostAndPath(pattern!.Trim(), out var host, out _, out _) ? host : null;
    }

    private static bool IsIpAddress(string host) => System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _);

    private static PatternCompileResult Ok(Func<LinkInput, bool> predicate) => new(new CompiledPattern(predicate), null);
    private static PatternCompileResult Fail(string error) => new(null, error);
}

/// <summary>
/// Host glob, tried against the ASCII host and then the Unicode one. A leading "*." also matches the bare
/// domain: "*.acme.io" is "acme.io" or anything ending in ".acme.io" (the "*" may span dots).
/// Both sides are lower case (the pattern from TrySplitHostAndPath, the link from <see cref="LinkInput"/>).
/// </summary>
internal sealed class HostGlob(string host)
{
    private readonly StarGlob _glob = StarGlob.TryCreate(host, StringComparison.Ordinal)!;
    private readonly StarGlob? _bare = host.StartsWith("*.", StringComparison.Ordinal) ? StarGlob.TryCreate(host[2..], StringComparison.Ordinal) : null;

    public bool IsMatch(LinkInput link) => Matches(link.Host) || (link.UnicodeHost is { } u && Matches(u));

    private bool Matches(string h) => _glob.IsMatch(h) || (_bare is not null && _bare.IsMatch(h));
}
