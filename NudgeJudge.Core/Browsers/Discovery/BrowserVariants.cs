using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Browsers.Discovery;

/// <summary>
/// Turns one discovered browser command into the entries offered to the user: the browser itself,
/// a private-window entry, and one entry per profile when there is more than one.
/// </summary>
internal static class BrowserVariants
{
    /// <param name="name">Display name.</param>
    /// <param name="command">argv; <see cref="BrowserLauncher.UrlPlaceholder"/> marks where the URL goes, if anywhere.</param>
    /// <param name="privateCommand">A private-window command the browser itself advertises, if any.</param>
    /// <param name="privateLabel">Suffix for that command's entry when the browser isn't a known family.</param>
    public static List<BrowserProfile> Expand(string name, IReadOnlyList<string> command,
        IReadOnlyList<string>? privateCommand, string? privateLabel, DiscoveryPaths paths)
    {
        var result = new List<BrowserProfile>();
        if (Make(name, command) is not { } main) return result;
        result.Add(main);

        var identified = BrowserFamily.Identify(WithoutEnv(command), paths);
        var family = identified?.Family;

        IReadOnlyList<string>? priv = privateCommand
            ?? (family?.PrivateFlag is { } flag ? InsertBeforeUrl(command, [flag]) : null);
        if (priv is not null && Make($"{name} {family?.PrivateLabel ?? privateLabel ?? "Private"}", priv) is { } p)
        {
            p.IsPrivate = true;
            result.Add(p);
        }

        if (identified is { } id)
        {
            var profiles = id.Family.ReadProfiles(id.ProfileRoots);
            // A single profile is what the plain entry opens anyway.
            if (profiles.Count > 1)
            {
                foreach (var (label, args) in profiles)
                {
                    if (Make($"{name} ({label})", InsertBeforeUrl(command, args)) is not { } prof) continue;
                    prof.ProfileLabel = label;
                    result.Add(prof);
                }
            }
        }
        return result;
    }

    /// <summary>Skips an <c>env VAR=value …</c> prefix so the real executable can be recognised.</summary>
    public static IReadOnlyList<string> WithoutEnv(IReadOnlyList<string> command)
    {
        if (command.Count == 0 || BrowserFamily.ExecutableName(command[0]) != "env") return command;
        var i = 1;
        while (i < command.Count && (command[i].StartsWith('-') || command[i].Contains('='))) i++;
        return command.Skip(i).ToList();
    }

    /// <summary>Inserts <paramref name="extra"/> before the URL (or a <c>--</c> separator), else at the end.</summary>
    internal static List<string> InsertBeforeUrl(IReadOnlyList<string> command, IEnumerable<string> extra)
    {
        var list = command.ToList();
        var at = list.FindIndex(1, a => a == "--" || a.Contains(BrowserLauncher.UrlPlaceholder, StringComparison.Ordinal));
        list.InsertRange(at < 0 ? list.Count : at, extra);
        return list;
    }

    private static BrowserProfile? Make(string name, IReadOnlyList<string> command)
    {
        if (command.Count == 0 || string.IsNullOrWhiteSpace(command[0])) return null;
        var args = command.Skip(1).ToList();
        // The launcher appends the URL when there's no placeholder, so a trailing bare one is noise and
        // would stop a rescan from recognising entries the user typed in without it.
        if (args.Count > 0 && args[^1] == BrowserLauncher.UrlPlaceholder) args.RemoveAt(args.Count - 1);
        if (CommandLine.Join(args) is not { } joined) return null;

        return new BrowserProfile
        {
            Name = name.Trim(),
            ExecutablePath = command[0],
            Arguments = joined,
            Source = BrowserSource.Discovered,
        };
    }

    /// <summary>Drops entries with the same command as an earlier one.</summary>
    public static List<BrowserProfile> Distinct(IEnumerable<BrowserProfile> all, bool ignorePathCase)
    {
        var seen = new HashSet<(string, string)>();
        return all.Where(b => seen.Add((ignorePathCase ? b.ExecutablePath.ToLowerInvariant() : b.ExecutablePath, b.Arguments))).ToList();
    }
}
