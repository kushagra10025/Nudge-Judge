using NudgeJudge.Core.Models;

namespace NudgeJudge.ViewModels;

public enum SummaryPartKind
{
    /// <summary>Connecting words ("or", "from", "and").</summary>
    Text,
    /// <summary>A pattern or program name, set in mono.</summary>
    Value,
    /// <summary>The match-kind glyph of a link condition.</summary>
    LinkGlyph,
    /// <summary>The source-app icon.</summary>
    AppGlyph,
}

public sealed record SummaryPart(string Text, SummaryPartKind Kind, MatchKind Glyph = MatchKind.Wildcard);

/// <summary>A rule's conditions as one line: ◇ youtube.com or youtu.be  and  ▦ from ms-teams.exe</summary>
public static class RuleSummary
{
    private const int MaxValuesShown = 3;

    public static IReadOnlyList<SummaryPart> Describe(Rule rule)
    {
        var parts = new List<SummaryPart>();
        var conditions = rule.Conditions.Where(c => c.Values.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        for (var i = 0; i < conditions.Count; i++)
        {
            if (i > 0) parts.Add(new(rule.Join == RuleJoin.Any ? "   or   " : "   and   ", SummaryPartKind.Text));
            Describe(conditions[i], parts);
        }
        if (parts.Count == 0) parts.Add(new("No conditions yet", SummaryPartKind.Text));
        return parts;
    }

    /// <summary>The same line as plain text, for tooltips and search.</summary>
    public static string DescribeText(Rule rule) =>
        string.Concat(Describe(rule).Where(p => p.Kind is SummaryPartKind.Text or SummaryPartKind.Value).Select(p => p.Text)).Trim();

    private static void Describe(RuleCondition condition, List<SummaryPart> parts)
    {
        var values = condition.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (condition.Field == ConditionField.SourceApp)
        {
            parts.Add(new("", SummaryPartKind.AppGlyph));
            parts.Add(new(condition.Negate ? "not from " : "from ", SummaryPartKind.Text));
        }
        else
        {
            parts.Add(new("", SummaryPartKind.LinkGlyph, condition.MatchKind));
            if (condition.Negate) parts.Add(new("not ", SummaryPartKind.Text));
        }

        for (var i = 0; i < values.Count && i < MaxValuesShown; i++)
        {
            if (i > 0) parts.Add(new(condition.Negate ? " nor " : " or ", SummaryPartKind.Text));
            parts.Add(new(values[i], SummaryPartKind.Value));
        }
        if (values.Count > MaxValuesShown) parts.Add(new($" +{values.Count - MaxValuesShown} more", SummaryPartKind.Text));
    }
}

/// <summary>Suggested rule names, so the required name never starts blank.</summary>
public static class RuleNaming
{
    /// <summary>"*.acme.io" → "acme.io"; with an app, "acme.io from Microsoft Teams".</summary>
    public static string Suggest(string? linkPattern, string? app)
    {
        var link = Tidy(linkPattern);
        var from = string.IsNullOrWhiteSpace(app) ? null : app.Trim();
        return (link, from) switch
        {
            ({ Length: > 0 }, { } a) => $"{link} from {a}",
            ({ Length: > 0 }, null) => link,
            (_, { } a) => $"Links from {a}",
            _ => "",
        };
    }

    private static string Tidy(string? pattern)
    {
        var p = pattern?.Trim() ?? "";
        var scheme = p.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) p = p[(scheme + 3)..];
        if (p.StartsWith("*.", StringComparison.Ordinal)) p = p[2..];
        if (p.EndsWith("/*", StringComparison.Ordinal)) p = p[..^2];
        return p.TrimEnd('/');
    }
}
