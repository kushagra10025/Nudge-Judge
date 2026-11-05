using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Rules;

/// <summary>
/// The parts of a link that patterns look at, worked out once per match instead of once per pattern.
/// <see cref="Uri"/> recomputes some of these (the IDN host in particular) on every access.
/// </summary>
internal sealed class LinkInput
{
    private string? _absoluteUri;

    public LinkInput(Uri url, SourceApp? source, DateTime? localTime = null)
    {
        Url = url;
        Source = source;
        LocalTime = localTime ?? DateTime.Now;
        // Lower-cased once here so every host pattern (already lower case) can compare ordinally.
        var idn = url.IdnHost;
        Host = idn.TrimEnd('.').ToLowerInvariant();
        var unicode = url.Host;
        // A glob inside a non-ASCII label ("*münchen.de") can't be punycoded, so hosts are also tried in Unicode.
        UnicodeHost = string.Equals(unicode, idn, StringComparison.Ordinal) ? null : unicode.TrimEnd('.').ToLowerInvariant();
        Path = url.AbsolutePath;
    }

    public Uri Url { get; }
    public SourceApp? Source { get; }

    /// <summary>The local wall-clock time the link is being matched at, for time conditions.</summary>
    public DateTime LocalTime { get; }

    /// <summary>The ASCII (punycode) host in lower case, without a trailing dot.</summary>
    public string Host { get; }

    /// <summary>The Unicode host in lower case when it differs from <see cref="Host"/>, otherwise null.</summary>
    public string? UnicodeHost { get; }

    public string Path { get; }

    /// <summary>Only regular expressions need the full URL, so it's built on first use.</summary>
    public string AbsoluteUri => _absoluteUri ??= Url.AbsoluteUri;
}

/// <summary>
/// A <c>*</c>-only glob, matched with plain string operations. A star matches any run of
/// characters. Linear in practice: the literal pieces are found left to right with a vectorised search,
/// and leftmost placement is always correct when the only wildcard is <c>*</c>.
/// </summary>
internal sealed class StarGlob
{
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly string[] _middle;
    private readonly int _minLength;
    private readonly bool _literal;
    private readonly bool _ignoreCase;

    private StarGlob(string glob, bool ignoreCase)
    {
        _ignoreCase = ignoreCase;
        var parts = glob.Split('*');
        _literal = parts.Length == 1;
        _prefix = parts[0];
        _suffix = _literal ? "" : parts[^1];
        _middle = _literal ? [] : parts[1..^1].Where(p => p.Length > 0).ToArray();
        _minLength = _prefix.Length + _suffix.Length + _middle.Sum(p => p.Length);
    }

    /// <summary>Null when the glob has a <c>?</c>, which this matcher doesn't handle.</summary>
    public static StarGlob? TryCreate(string glob, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        glob.Contains('?') ? null : new StarGlob(glob, comparison == StringComparison.OrdinalIgnoreCase);

    public bool IsMatch(ReadOnlySpan<char> s)
    {
        // Length first: it settles most mismatches without looking at a character.
        if (s.Length < _minLength) return false;
        if (_literal) return s.Length == _prefix.Length && Same(s, _prefix);
        if (_prefix.Length > 0 && !Same(s[.._prefix.Length], _prefix)) return false;
        if (_suffix.Length > 0 && !Same(s[^_suffix.Length..], _suffix)) return false;
        if (_middle.Length == 0) return true;

        var rest = s[_prefix.Length..^_suffix.Length];
        foreach (var piece in _middle)
        {
            var at = _ignoreCase ? rest.IndexOf(piece, StringComparison.OrdinalIgnoreCase) : rest.IndexOf(piece.AsSpan());
            if (at < 0) return false;
            rest = rest[(at + piece.Length)..];
        }
        return true;
    }

    private bool Same(ReadOnlySpan<char> a, string b) =>
        _ignoreCase ? a.Equals(b, StringComparison.OrdinalIgnoreCase) : a.SequenceEqual(b);
}
