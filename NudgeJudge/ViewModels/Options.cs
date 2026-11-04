using NudgeJudge.Core.Models;

namespace NudgeJudge.ViewModels;

public sealed record MatchKindOption(MatchKind Kind, string Label, string Hint, string Example)
{
    public static IReadOnlyList<MatchKindOption> All { get; } =
    [
        new(MatchKind.Wildcard, "Wildcard", "Use * for any text. *.example.com also matches example.com itself.", "*.example.com  or  github.com/acme/*"),
        new(MatchKind.Domain, "Exact domain", "Matches one host exactly, nothing else.", "www.youtube.com"),
        new(MatchKind.PathPrefix, "Path prefix", "A host plus the start of a path. Matches whole path segments.", "github.com/acme"),
        new(MatchKind.Regex, "Regular expression", "Advanced. Matched against the full URL. Lookarounds and backreferences aren't allowed.", @"^https://([a-z]+\.)?bank\.example/"),
    ];

    public static MatchKindOption For(MatchKind kind) => All.First(o => o.Kind == kind);
    public override string ToString() => Label;
}

public sealed record JoinOption(RuleJoin Join, string Label)
{
    public static IReadOnlyList<JoinOption> All { get; } = [new(RuleJoin.All, "all"), new(RuleJoin.Any, "any")];
    public static JoinOption For(RuleJoin join) => All.First(o => o.Join == join);
    public override string ToString() => Label;
}

/// <summary>"is" or "is not", worded for the condition's field.</summary>
public sealed record OperatorOption(bool Negate, string Label)
{
    public static IReadOnlyList<OperatorOption> Link { get; } = [new(false, "matches"), new(true, "doesn't match")];
    public static IReadOnlyList<OperatorOption> Source { get; } = [new(false, "is"), new(true, "is not")];
    public override string ToString() => Label;
}

public sealed record BrowserOption(Guid Id, string Name, string Detail)
{
    public static BrowserOption From(BrowserProfile b) => new(b.Id, b.Name, BrowserDetail(b));

    public static string BrowserDetail(BrowserProfile b)
    {
        var parts = new List<string>();
        if (b.IsPrivate) parts.Add("Private window");
        if (!string.IsNullOrWhiteSpace(b.ProfileLabel)) parts.Add(b.ProfileLabel!);
        return string.Join(" · ", parts);
    }

    public override string ToString() => Name;
}

public sealed record ThemeOption(ThemePreference Value, string Label)
{
    public static IReadOnlyList<ThemeOption> All { get; } =
        [new(ThemePreference.System, "Match system"), new(ThemePreference.Light, "Light"), new(ThemePreference.Dark, "Dark")];
    public override string ToString() => Label;
}

public enum RememberKind { Once, Hours, UntilRestart, Always }

/// <summary>What the chooser does besides opening the link: nothing, or save a rule of some lifetime.</summary>
public sealed record RememberOption(RememberKind Kind, string Label)
{
    public bool SavesRule => Kind != RememberKind.Once;
    public override string ToString() => Label;
}

public sealed record UpdateFrequencyOption(UpdateCheckFrequency Value, string Label)
{
    public static IReadOnlyList<UpdateFrequencyOption> All { get; } =
    [
        new(UpdateCheckFrequency.Startup, "Every time it starts"),
        new(UpdateCheckFrequency.Daily, "Once a day"),
        new(UpdateCheckFrequency.Weekly, "Once a week"),
    ];

    public static UpdateFrequencyOption For(UpdateCheckFrequency value) => All.FirstOrDefault(o => o.Value == value) ?? All[0];
    public override string ToString() => Label;
}
