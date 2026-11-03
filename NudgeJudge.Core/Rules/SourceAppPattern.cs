using System.Text;
using System.Text.RegularExpressions;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Rules;

/// <summary>
/// Matches a "source app" value against the app a link came from. A value can be:
/// <list type="bullet">
/// <item>a program name, with or without <c>.exe</c> (<c>ms-teams</c>, <c>ms-teams.exe</c>, <c>slack</c>),
/// or the app's friendly title (<c>Microsoft Teams</c>), compared ignoring case;</item>
/// <item>a glob with <c>*</c> or <c>?</c> (<c>*teams*</c>) tried against the name, title and path;</item>
/// <item>a full path (contains <c>/</c> or <c>\</c>) compared with the program's path.</item>
/// </list>
/// </summary>
public static class SourceAppPattern
{
    public static SourceAppCompileResult Compile(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0)
            return new(null, "Enter an app, e.g. ms-teams.exe.");
        if (value.Length > SourceApp.MaxPathLength)
            return new(null, "That's too long for an app name or path.");
        if (value.Any(char.IsControl))
            return new(null, "The app name contains control characters.");

        if (value.Contains('*') || value.Contains('?'))
        {
            var regex = PatternCompiler.BuildRegex("^" + Glob(value) + "$", caseSensitive: false);
            return new(app => app is not null &&
                              (regex.IsMatch(app.Name) || regex.IsMatch(WithoutExe(app.Name)) ||
                               (app.Title is { } t && regex.IsMatch(t)) ||
                               (app.Path is { } p && regex.IsMatch(p))), null);
        }

        if (value.Contains('/') || value.Contains('\\'))
        {
            var path = value;
            return new(app => app?.Path is { } p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase), null);
        }

        var bare = WithoutExe(value);
        return new(app => app is not null &&
                          (string.Equals(WithoutExe(app.Name), bare, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(app.Title, value, StringComparison.OrdinalIgnoreCase)), null);
    }

    private static string WithoutExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string Glob(string glob)
    {
        var sb = new StringBuilder(glob.Length * 2);
        foreach (var c in glob)
            sb.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
        return sb.ToString();
    }
}

public readonly record struct SourceAppCompileResult(Func<SourceApp?, bool>? Predicate, string? Error)
{
    public bool Success => Predicate is not null;
}
