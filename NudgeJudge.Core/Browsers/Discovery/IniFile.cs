namespace NudgeJudge.Core.Browsers.Discovery;

/// <summary>
/// Minimal reader for the <c>[Group]</c> / <c>Key=Value</c> format shared by freedesktop
/// <c>.desktop</c> files and Firefox's <c>profiles.ini</c>. Values are returned raw (no unescaping).
/// The first occurrence of a group or key wins.
/// </summary>
internal sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _groups = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public IReadOnlyList<string> Groups => _order;

    public static IniFile Parse(IEnumerable<string> lines)
    {
        var ini = new IniFile();
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1];
                if (ini._groups.ContainsKey(name))
                {
                    current = null; // duplicate group: ignore its keys
                    continue;
                }
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                ini._groups[name] = current;
                ini._order.Add(name);
                continue;
            }

            var eq = line.IndexOf('=');
            if (current is null || eq <= 0) continue;
            current.TryAdd(line[..eq].TrimEnd(), line[(eq + 1)..].TrimStart());
        }
        return ini;
    }

    public static IniFile? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadLines(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? Get(string group, string key) =>
        _groups.TryGetValue(group, out var g) && g.TryGetValue(key, out var v) ? v : null;
}
