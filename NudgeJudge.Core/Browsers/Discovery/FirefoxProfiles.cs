using Microsoft.Data.Sqlite;

namespace NudgeJudge.Core.Browsers.Discovery;

/// <summary>
/// Lists Firefox profiles from both places Firefox keeps them:
/// <list type="bullet">
/// <item><c>profiles.ini</c>, the classic profile list (<c>about:profiles</c>).</item>
/// <item>The newer profile manager's store, <c>Profile Groups/&lt;StoreID&gt;.sqlite</c>. Profiles made in the
/// toolbar profile menu exist only there, and the names shown in that menu (e.g. "Personal") come from it.</item>
/// </list>
/// Profiles are opened with <c>--profile &lt;directory&gt;</c>, as the profile menu does, because store-only
/// profiles have no name <c>-P</c> could look up.
/// </summary>
internal static class FirefoxProfiles
{
    public static List<(string Label, string[] Args)> Read(string root)
    {
        var ini = IniFile.TryLoad(Path.Combine(root, "profiles.ini"));
        if (ini is null) return [];

        // Directory → display name, in the order Firefox lists them.
        var profiles = new List<(string Dir, string Name)>();
        var storeIds = new List<string>();

        foreach (var group in ini.Groups.Where(g => g.StartsWith("Profile", StringComparison.Ordinal)))
        {
            var name = ini.Get(group, "Name");
            var path = ini.Get(group, "Path");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
            if (ini.Get(group, "StoreID") is { Length: > 0 } store && !storeIds.Contains(store)) storeIds.Add(store);

            var dir = Resolve(root, path, ini.Get(group, "IsRelative") != "0");
            // Firefox leaves behind empty "default" profiles that were never used; only offer real ones.
            if (File.Exists(Path.Combine(dir, "prefs.js"))) profiles.Add((dir, name));
        }

        foreach (var store in storeIds)
        {
            foreach (var (path, name) in ReadStore(Path.Combine(root, "Profile Groups", store + ".sqlite")))
            {
                var dir = Resolve(root, path, !Path.IsPathRooted(path));
                if (!Directory.Exists(dir)) continue;
                var i = profiles.FindIndex(p => SameDir(p.Dir, dir));
                if (i >= 0) profiles[i] = (profiles[i].Dir, name); // the profile menu's name wins
                else profiles.Add((dir, name));
            }
        }

        return profiles.Select(p => (p.Name, new[] { "--profile", p.Dir })).ToList();
    }

    /// <summary>(path, name) rows of a profile-manager store. Empty when it can't be read.</summary>
    internal static List<(string Path, string Name)> ReadStore(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return Query(file);
        }
        catch (SqliteException)
        {
            // Firefox may hold the database in a state a read-only connection can't open (e.g. no shared
            // memory file yet). Read a private copy instead of touching the original.
            var temp = Directory.CreateTempSubdirectory("nudgejudge-ff-");
            try
            {
                var copy = Path.Combine(temp.FullName, "store.sqlite");
                foreach (var suffix in new[] { "", "-wal" })
                {
                    if (File.Exists(file + suffix)) File.Copy(file + suffix, copy + suffix);
                }
                return Query(copy);
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { temp.Delete(recursive: true); } catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<(string, string)> Query(string file)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, name FROM Profiles ORDER BY id";
        using var reader = cmd.ExecuteReader();

        var rows = new List<(string, string)>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var path = reader.GetString(0);
            var name = reader.GetString(1);
            if (path.Length > 0 && name.Length > 0) rows.Add((path, name));
        }
        return rows;
    }

    private static string Resolve(string root, string path, bool relative) =>
        Path.GetFullPath(relative ? Path.Combine(root, path) : path);

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
