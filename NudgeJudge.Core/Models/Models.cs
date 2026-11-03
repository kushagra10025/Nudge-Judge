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

public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public MatchKind MatchKind { get; set; } = MatchKind.Wildcard;
    public string Pattern { get; set; } = "";
    public Guid BrowserId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool CaseSensitive { get; set; }

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

    [JsonIgnore]
    public bool IsTemporary => ExpiresAt is not null || ExpiresAfterBootId is not null;

    public bool IsExpired(DateTimeOffset now, ISystemBoot boot) =>
        (ExpiresAt is { } at && now >= at) ||
        (ExpiresAfterBootId is { } id && !boot.IsCurrentBoot(id));

    public bool IsActive(DateTimeOffset now, ISystemBoot boot) => Enabled && !IsExpired(now, boot);

    public Rule Clone() => (Rule)MemberwiseClone();
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
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<BrowserProfile> Browsers { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    public BrowserProfile? FindBrowser(Guid? id) => id is null ? null : Browsers.Find(b => b.Id == id);
}

public interface ISystemBoot
{
    string CurrentBootId { get; }
    bool IsCurrentBoot(string bootId);
}
