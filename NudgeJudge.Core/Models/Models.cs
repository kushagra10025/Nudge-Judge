using System.Text.Json.Serialization;

namespace NudgeJudge.Core.Models;

/// <summary>How a rule's pattern is compared against an incoming URL.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchKind>))]
public enum MatchKind
{
    /// <summary>Host equals the pattern, e.g. <c>youtube.com</c>.</summary>
    Domain,

    /// <summary>Glob over host and optional path, e.g. <c>*.acme.io</c> or <c>github.com/acme/*</c>.</summary>
    Wildcard,

    /// <summary>Host (globs allowed) plus a path prefix matched on segment boundaries, e.g. <c>github.com/acme</c>.</summary>
    PathPrefix,

    /// <summary>.NET regular expression against the full URL. Evaluated with a non-backtracking engine.</summary>
    Regex,
}

/// <summary>What a condition looks at.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConditionField>))]
public enum ConditionField
{
    /// <summary>The URL, compared with a <see cref="MatchKind"/> pattern.</summary>
    Link,

    /// <summary>The app the link was opened from, e.g. <c>ms-teams.exe</c>.</summary>
    SourceApp,

    /// <summary>The local time the link is opened, e.g. <c>Mon-Fri 09:00-17:00</c>. See <see cref="Rules.TimeWindow"/>.</summary>
    Time,

    /// <summary>A nested group: its own <see cref="RuleCondition.Conditions"/>, combined with <see cref="RuleCondition.Join"/>.</summary>
    Group,
}

/// <summary>How a rule combines its conditions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleJoin>))]
public enum RuleJoin
{
    /// <summary>Every condition must hold.</summary>
    All,

    /// <summary>At least one condition must hold.</summary>
    Any,
}

/// <summary>
/// One test in a rule. It holds when any of its <see cref="Values"/> matches (or, with
/// <see cref="Negate"/>, when none does). A <see cref="ConditionField.Group"/> instead holds when its
/// <see cref="Conditions"/> do, combined by <see cref="Join"/>.
/// </summary>
public sealed class RuleCondition
{
    /// <summary>
    /// How many levels conditions may nest, counting the rule itself: a rule, a group in it, and a group in
    /// that. Deeper rules are hard to read and never needed in practice.
    /// </summary>
    public const int MaxDepth = 3;

    public ConditionField Field { get; set; } = ConditionField.Link;

    /// <summary>Groups only: how the group combines its conditions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RuleJoin Join { get; set; } = RuleJoin.All;

    /// <summary>Groups only: the conditions (and groups) inside it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RuleCondition>? Conditions { get; set; }

    [JsonIgnore]
    public bool IsGroup => Field == ConditionField.Group;

    /// <summary>Link conditions only.</summary>
    public MatchKind MatchKind { get; set; } = MatchKind.Wildcard;

    /// <summary>Link conditions only; applies to path prefixes and regular expressions.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>"is not": holds when none of the values match.</summary>
    public bool Negate { get; set; }

    /// <summary>Alternatives, OR'd together. Blank entries are ignored.</summary>
    public List<string> Values { get; set; } = [];

    public static RuleCondition Link(MatchKind kind, params string[] patterns) =>
        new() { Field = ConditionField.Link, MatchKind = kind, Values = [.. patterns] };

    public static RuleCondition Source(params string[] apps) =>
        new() { Field = ConditionField.SourceApp, Values = [.. apps] };

    public static RuleCondition Time(params string[] windows) =>
        new() { Field = ConditionField.Time, Values = [.. windows] };

    public static RuleCondition Group(RuleJoin join, params RuleCondition[] conditions) =>
        new() { Field = ConditionField.Group, Join = join, Conditions = [.. conditions] };

    /// <summary>This condition, or every link, app and time condition inside the group, depth first.</summary>
    public IEnumerable<RuleCondition> Leaves() =>
        IsGroup ? (Conditions ?? []).SelectMany(c => c.Leaves()) : [this];

    public RuleCondition Clone() => new()
    {
        Field = Field,
        Join = Join,
        Conditions = Conditions?.Select(c => c.Clone()).ToList(),
        MatchKind = MatchKind,
        CaseSensitive = CaseSensitive,
        Negate = Negate,
        Values = [.. Values],
    };
}

public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the user calls the rule. Required: it's how the rule is listed and logged.</summary>
    public string Name { get; set; } = "";

    public RuleJoin Join { get; set; } = RuleJoin.All;
    public List<RuleCondition> Conditions { get; set; } = [];
    public Guid BrowserId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Temporary rule: stops matching after this instant.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Temporary rule: stops matching once the machine reboots. Holds the boot id it was created in.</summary>
    public string? ExpiresAfterBootId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True: came with the first-run sample config. False: saved by the user. Null: written before this
    /// flag existed (sample cleanup then compares the rule with the sample templates).
    /// </summary>
    public bool? IsSample { get; set; }

    /// <summary>The rule engine's compiled form of this rule. Never saved; checked against the conditions on every use.</summary>
    internal Rules.CompiledRuleCache? Compiled;

    // Config files written before rules had conditions held one link pattern per rule.
    // These read it; Upgrade() folds it into Conditions and clears them, so they're never written back.

    [JsonPropertyName("matchKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MatchKind? LegacyMatchKind { get; set; }

    [JsonPropertyName("pattern"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPattern { get; set; }

    [JsonPropertyName("caseSensitive"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyCaseSensitive { get; set; }

    [JsonIgnore]
    public bool IsTemporary => ExpiresAt is not null || ExpiresAfterBootId is not null;

    /// <summary>The first link condition, if any (what the chooser and samples create).</summary>
    [JsonIgnore]
    public RuleCondition? FirstLinkCondition => Conditions.Find(c => c.Field == ConditionField.Link);

    /// <summary>Every link and app condition, including those inside groups.</summary>
    public IEnumerable<RuleCondition> Leaves() => Conditions.SelectMany(c => c.Leaves());

    public bool IsExpired(DateTimeOffset now, ISystemBoot boot) =>
        (ExpiresAt is { } at && now >= at) ||
        (ExpiresAfterBootId is { } id && !boot.IsCurrentBoot(id));

    public bool IsActive(DateTimeOffset now, ISystemBoot boot) => Enabled && !IsExpired(now, boot);

    /// <summary>
    /// Brings a rule read from an older config up to date: the single pattern becomes a link condition,
    /// and a rule without a name is named after its pattern. Returns true when anything changed.
    /// </summary>
    public bool Upgrade()
    {
        var changed = false;
        if (LegacyPattern is not null || LegacyMatchKind is not null || LegacyCaseSensitive is not null)
        {
            if (Conditions.Count == 0 && !string.IsNullOrWhiteSpace(LegacyPattern))
            {
                Conditions.Add(new RuleCondition
                {
                    Field = ConditionField.Link,
                    MatchKind = LegacyMatchKind ?? MatchKind.Wildcard,
                    CaseSensitive = LegacyCaseSensitive ?? false,
                    Values = [LegacyPattern.Trim()],
                });
            }
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(LegacyPattern))
                Name = LegacyPattern.Trim();
            LegacyPattern = null;
            LegacyMatchKind = null;
            LegacyCaseSensitive = null;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = Leaves().SelectMany(c => c.Values).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "Untitled rule";
            changed = true;
        }
        return changed;
    }

    public Rule Clone()
    {
        var copy = (Rule)MemberwiseClone();
        copy.Conditions = Conditions.Select(c => c.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// The app a link came from, as far as NudgeJudge could tell. <see cref="Name"/> is the program's file
/// name (<c>ms-teams.exe</c>, <c>teams-for-linux</c>); <see cref="Title"/> is a friendlier name when the
/// OS has one (<c>Microsoft Teams</c>).
/// </summary>
public sealed record SourceApp(string Name, string? Path = null, string? Title = null)
{
    public const int MaxNameLength = 260;
    public const int MaxPathLength = 4096;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title!;

    /// <summary>
    /// A copy that's safe to log and show, or null when there's no usable name. Used on anything that
    /// crossed a process boundary.
    /// </summary>
    public static SourceApp? Sanitize(SourceApp? app)
    {
        if (app is null) return null;
        var name = Clean(app.Name, MaxNameLength);
        if (name is null) return null;
        return new SourceApp(name, Clean(app.Path, MaxPathLength), Clean(app.Title, MaxNameLength));
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length > max || s.Any(char.IsControl)) return null;
        return s;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<BrowserSource>))]
public enum BrowserSource { Manual, Discovered, Sample }

public sealed class BrowserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Argument template. <c>{url}</c> is replaced with the URL; when absent the URL is appended
    /// as the last argument. Arguments are passed as an argv list, never through a shell.
    /// </summary>
    public string Arguments { get; set; } = "";

    public bool IsPrivate { get; set; }
    public string? ProfileLabel { get; set; }
    public BrowserSource Source { get; set; } = BrowserSource.Manual;

    public BrowserProfile Clone() => (BrowserProfile)MemberwiseClone();
}

[JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
public enum ThemePreference { System, Light, Dark }

/// <summary>How often the app looks for a newer release on its own.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateCheckFrequency>))]
public enum UpdateCheckFrequency { Startup, Daily, Weekly }

public sealed class AppSettings
{
    /// <summary>When false every link passes straight through to <see cref="PrimaryBrowserId"/>.</summary>
    public bool RoutingEnabled { get; set; } = true;

    /// <summary>Browser used for pass-through when routing is disabled.</summary>
    public Guid? PrimaryBrowserId { get; set; }

    /// <summary>Unmatched links arriving within this window are grouped into the open chooser.</summary>
    public int ChooserGroupingSeconds { get; set; } = 5;

    public int TemporaryRuleHours { get; set; } = 8;
    /// <summary>Closing the main window keeps NudgeJudge running in the tray.</summary>
    public bool RunInBackground { get; set; } = true;
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>When false, handled links aren't recorded in Activity (or on disk).</summary>
    public bool ActivityLogEnabled { get; set; } = true;

    /// <summary>How many Activity entries to keep. The oldest are deleted once there are more.</summary>
    public int ActivityLogLimit { get; set; } = 100;

    /// <summary>
    /// True on a fresh install until the user either loads the example setup or chooses to start empty.
    /// Absent from configs written before first-run onboarding existed, so those users are never asked.
    /// </summary>
    public bool OfferSamples { get; set; }

    /// <summary>Look for a newer release without being asked. Off means only Settings → "Check now".</summary>
    public bool CheckForUpdates { get; set; } = true;

    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.Startup;

    /// <summary>When the last automatic or manual check finished, so daily and weekly checks survive restarts.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }
}

public sealed class AppConfig
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public AppSettings Settings { get; set; } = new();
    public List<BrowserProfile> Browsers { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    public BrowserProfile? FindBrowser(Guid? id) => id is null ? null : Browsers.Find(b => b.Id == id);

    /// <summary>Upgrades anything written by an older version in place. Returns true when something changed.</summary>
    public bool Upgrade()
    {
        var changed = false;
        foreach (var rule in Rules) changed |= rule.Upgrade();
        if (Version < CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }
        return changed;
    }
}

public interface ISystemBoot
{
    string CurrentBootId { get; }
    bool IsCurrentBoot(string bootId);
}
