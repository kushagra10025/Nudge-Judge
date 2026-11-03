namespace NudgeJudge.Core.Security;

public readonly record struct UrlValidationResult(Uri? Url, string? Error)
{
    public bool IsValid => Url is not null;
}

/// <summary>
/// Gatekeeper for every URL that reaches the router, whether from the command line, IPC or the UI.
/// Only absolute http/https URLs with a host get through; everything else is rejected before
/// rule matching or process launch.
/// </summary>
public static class UrlValidator
{
    public const int MaxLength = 8192;

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
    };

    public static UrlValidationResult Validate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("No URL was given.");

        var text = input.Trim();
        if (text.Length > MaxLength)
            return Fail($"URL is longer than {MaxLength} characters.");

        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return Fail("URL contains whitespace or control characters.");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return Fail("Not a valid absolute URL.");

        if (!AllowedSchemes.Contains(uri.Scheme))
            return Fail($"The {uri.Scheme}: scheme isn't handled.");

        if (string.IsNullOrEmpty(uri.Host))
            return Fail("URL has no host.");

        return new UrlValidationResult(uri, null);
    }

    private static UrlValidationResult Fail(string error) => new(null, error);
}
