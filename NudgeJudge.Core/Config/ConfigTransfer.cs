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

public sealed record ImportResult(int BrowsersAdded, int BrowsersUpdated, int RulesAdded, int RulesUpdated)
{
    public static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    public string Describe()
    {
        static string Part(int added, int updated, string what) =>
            updated == 0 ? Count(added, what) : $"{Count(added + updated, what)} ({updated} replaced)";
        return $"Imported {Part(RulesAdded, RulesUpdated, "rule")} and {Part(BrowsersAdded, BrowsersUpdated, "browser")}.";
    }
}

/// <summary>
/// Exports rules and browsers to a JSON file for backup or sharing, and imports them back. Importing merges by
/// id: an imported browser or rule replaces the one with the same id (restoring a backup) and anything new is
/// added at the end (someone else's rules).
/// </summary>
public static class ConfigTransfer
{
    /// <summary>Exports are a few KB; anything far bigger isn't one.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

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

    /// <summary>Reads an export file. Throws <see cref="InvalidDataException"/> with a message fit to show when it isn't one.</summary>
    public static ConfigExport Read(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("That file is too big to be a NudgeJudge export.");
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
            throw new InvalidDataException("That file isn't a NudgeJudge export.");
        if (export.Version > AppConfig.CurrentVersion)
            throw new InvalidDataException("That file was exported by a newer version of NudgeJudge. Update to import it.");

        export.Browsers = export.Browsers.Where(b => b is not null && b.Id != Guid.Empty).DistinctBy(b => b.Id).ToList();
        export.Rules = export.Rules.Where(r => r is not null && r.Id != Guid.Empty).DistinctBy(r => r.Id).ToList();
        foreach (var rule in export.Rules)
        {
            rule.Conditions ??= [];
            rule.Upgrade();
        }
        return export;
    }

    /// <summary>Merges <paramref name="export"/> into <paramref name="config"/> by id.</summary>
    public static ImportResult Apply(AppConfig config, ConfigExport export)
    {
        int browsersAdded = 0, browsersUpdated = 0, rulesAdded = 0, rulesUpdated = 0;
        foreach (var browser in export.Browsers)
        {
            var copy = browser.Clone();
            var at = config.Browsers.FindIndex(b => b.Id == copy.Id);
            if (at >= 0) { config.Browsers[at] = copy; browsersUpdated++; }
            else { config.Browsers.Add(copy); browsersAdded++; }
        }
        foreach (var rule in export.Rules)
        {
            var copy = rule.Clone();
            var at = config.Rules.FindIndex(r => r.Id == copy.Id);
            if (at >= 0) { config.Rules[at] = copy; rulesUpdated++; }
            else { config.Rules.Add(copy); rulesAdded++; }
        }
        // Someone set up enough to share: no need to offer the sample setup any more.
        if (export.Browsers.Count + export.Rules.Count > 0) config.Settings.OfferSamples = false;
        return new ImportResult(browsersAdded, browsersUpdated, rulesAdded, rulesUpdated);
    }
}
