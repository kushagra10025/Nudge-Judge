using System.Globalization;
using System.Text;
using System.Text.Json;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Config;

/// <summary>What an export file holds: browsers and rules, without app settings.</summary>
public sealed class ConfigExport
{
    public const string FormatName = "nudgejudge-export";

    /// <summary>Always <see cref="FormatName"/> in an export; anything else (or missing) means it isn't one.</summary>
    public string Format { get; set; } = "";

    /// <summary>The config version the rules and browsers were written in, so older exports upgrade on import.</summary>
    public int Version { get; set; } = AppConfig.CurrentVersion;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BrowserProfile> Browsers { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];
}

/// <summary>
/// Exports rules and browsers to a JSON file for backup or sharing, and reads them back. An import file is
/// untrusted: <see cref="Read"/> refuses anything malformed or oversized and cleans up names, and
/// <see cref="ImportPlan"/> decides what it may change, for the user to review before anything is applied.
/// </summary>
public static class ConfigTransfer
{
    /// <summary>Exports are a few KB; anything far bigger isn't one.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    public const int MaxBrowsers = 200;
    public const int MaxRules = 1000;
    public const int MaxConditionsPerLevel = 50;
    public const int MaxValuesPerCondition = 100;
    public const int MaxValueLength = 4096;
    public const int MaxNameLength = 200;
    public const int MaxArgumentsLength = 8192;

    /// <summary>
    /// Temporary rules (from the chooser) are left out: they're short-lived, and "until restart" ones are tied to
    /// this machine's boot.
    /// </summary>
    public static byte[] Export(AppConfig config, DateTimeOffset? now = null)
    {
        var export = new ConfigExport
        {
            Format = ConfigExport.FormatName,
            ExportedAt = now ?? DateTimeOffset.UtcNow,
            Browsers = config.Browsers.Select(b => b.Clone()).ToList(),
            Rules = config.Rules.Where(r => !r.IsTemporary).Select(r => r.Clone()).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(export, ConfigJsonContext.Default.ConfigExport);
    }

    /// <summary>
    /// Reads and checks an export file. Throws <see cref="InvalidDataException"/>, with a message fit to show,
    /// when it isn't one or breaks a limit. Nothing in a refused file is used.
    /// </summary>
    public static ConfigExport Read(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw Refuse("That file is too big to be a NudgeJudge export.");
        ConfigExport? export;
        try
        {
            export = JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.ConfigExport);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("That file isn't a NudgeJudge export: " + e.Message, e);
        }
        if (export is null || export.Format != ConfigExport.FormatName)
            throw Refuse("That file isn't a NudgeJudge export.");
        if (export.Version > AppConfig.CurrentVersion)
            throw Refuse("That file was exported by a newer version of NudgeJudge. Update to import it.");

        export.Browsers = (export.Browsers ?? []).Where(b => b is not null && b.Id != Guid.Empty).DistinctBy(b => b.Id).ToList();
        export.Rules = (export.Rules ?? []).Where(r => r is not null && r.Id != Guid.Empty).DistinctBy(r => r.Id).ToList();
        if (export.Browsers.Count > MaxBrowsers) throw Refuse($"That file has more than {MaxBrowsers} browsers.");
        if (export.Rules.Count > MaxRules) throw Refuse($"That file has more than {MaxRules} rules.");

        foreach (var browser in export.Browsers) Check(browser);
        foreach (var rule in export.Rules) Check(rule);
        return export;
    }

    private static void Check(BrowserProfile browser)
    {
        browser.Name = CleanName(browser.Name, "Imported browser");
        browser.ProfileLabel = string.IsNullOrWhiteSpace(browser.ProfileLabel) ? null : CleanName(browser.ProfileLabel, "");
        browser.ExecutablePath = (browser.ExecutablePath ?? "").Trim();
        browser.Arguments = (browser.Arguments ?? "").Trim();
        // Decided again on import from what the entry actually runs, never taken from the file.
        browser.NeedsReview = false;

        var who = $"The browser “{browser.Name}”";
        if (browser.ExecutablePath.Length == 0) throw Refuse($"{who} has no program.");
        if (browser.ExecutablePath.Length > SourceApp.MaxPathLength) throw Refuse($"{who} has a program path that's too long.");
        if (browser.ExecutablePath.Any(char.IsControl)) throw Refuse($"{who} has control characters in its program path.");
        if (browser.Arguments.Length > MaxArgumentsLength) throw Refuse($"{who} has arguments that are too long.");
        // Tabs and newlines are word breaks to a person but not to every reader: refuse rather than guess.
        if (browser.Arguments.Any(char.IsControl)) throw Refuse($"{who} has control characters in its arguments.");
    }

    private static void Check(Rule rule)
    {
        rule.Name = CleanName(rule.Name, "");
        rule.Conditions ??= [];
        // Old single-pattern rules become conditions here, so the limits below cover them too.
        rule.Upgrade();
        rule.Name = CleanName(rule.Name, "Imported rule");
        rule.Compiled = null;
        Check(rule.Conditions, rule.Name, depth: 2);
    }

    private static void Check(List<RuleCondition> conditions, string rule, int depth)
    {
        if (conditions.Count > MaxConditionsPerLevel) throw Refuse($"The rule “{rule}” has too many conditions.");
        if (depth > RuleCondition.MaxDepth + 1) throw Refuse($"The rule “{rule}” nests groups too deeply.");
        conditions.RemoveAll(c => c is null);
        foreach (var c in conditions)
        {
            c.Values ??= [];
            c.Values.RemoveAll(v => v is null);
            if (c.Values.Count > MaxValuesPerCondition) throw Refuse($"The rule “{rule}” has a condition with too many alternatives.");
            if (c.Values.Any(v => v.Length > MaxValueLength)) throw Refuse($"The rule “{rule}” has a pattern that's too long.");
            if (c.IsGroup) Check(c.Conditions ??= [], rule, depth + 1);
        }
    }

    /// <summary>
    /// Removes characters that can make a name look like something else: control characters and the invisible
    /// ones that reorder text (bidirectional overrides and isolates). Trims and caps the length.
    /// </summary>
    public static string CleanName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsControl(c) || IsBidiControl(c)) continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd() + "…";
        return clean.Length == 0 ? fallback : clean;
    }

    private static bool IsBidiControl(char c) =>
        c is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') ||
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.LineSeparator ||
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.ParagraphSeparator;

    private static InvalidDataException Refuse(string message) => new(message);
}
