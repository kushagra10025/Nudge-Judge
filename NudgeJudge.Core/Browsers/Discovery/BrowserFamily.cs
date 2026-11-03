using System.Text.Json;

namespace NudgeJudge.Core.Browsers.Discovery;

internal enum ProfileStore { None, Firefox, Chromium }

/// <summary>
/// What NudgeJudge knows about a well-known browser: how to open a private window and where its
/// profiles live. Browsers not listed here are still discovered, just without these extras.
/// </summary>
internal sealed record BrowserFamily(
    string[] ExecutableNames,
    string? PrivateFlag,
    string PrivateLabel,
    ProfileStore Store = ProfileStore.None,
    string? LinuxConfigDir = null,
    string? WindowsDataDir = null,
    string? FlatpakId = null)
{
    public static readonly IReadOnlyList<BrowserFamily> Known =
    [
        new(["firefox", "firefox-esr", "firefox-bin"], "-private-window", "Private",
            ProfileStore.Firefox, FlatpakId: "org.mozilla.firefox"),
        new(["google-chrome", "google-chrome-stable", "chrome"], "--incognito", "Incognito",
            ProfileStore.Chromium, "google-chrome", @"Google\Chrome\User Data", "com.google.Chrome"),
        new(["google-chrome-beta"], "--incognito", "Incognito", ProfileStore.Chromium, "google-chrome-beta"),
        new(["google-chrome-unstable"], "--incognito", "Incognito", ProfileStore.Chromium, "google-chrome-unstable"),
        new(["chromium", "chromium-browser"], "--incognito", "Incognito",
            ProfileStore.Chromium, "chromium", @"Chromium\User Data", "org.chromium.Chromium"),
        new(["brave", "brave-browser", "brave-browser-stable"], "--incognito", "Private",
            ProfileStore.Chromium, "BraveSoftware/Brave-Browser", @"BraveSoftware\Brave-Browser\User Data", "com.brave.Browser"),
        new(["microsoft-edge", "microsoft-edge-stable", "msedge"], "--inprivate", "InPrivate",
            ProfileStore.Chromium, "microsoft-edge", @"Microsoft\Edge\User Data", "com.microsoft.Edge"),
        new(["vivaldi", "vivaldi-stable"], "--incognito", "Private",
            ProfileStore.Chromium, "vivaldi", @"Vivaldi\User Data", "com.vivaldi.Vivaldi"),
        new(["opera"], "--private", "Private", FlatpakId: "com.opera.Opera"),
    ];

    /// <summary>
    /// Recognises the browser behind <paramref name="command"/> (argv, already free of an <c>env</c> prefix)
    /// and lists the directories its profile data could be in for that kind of install.
    /// </summary>
    public static (BrowserFamily Family, IReadOnlyList<string> ProfileRoots)? Identify(IReadOnlyList<string> command, DiscoveryPaths paths)
    {
        if (command.Count == 0) return null;
        var exe = command[0];
        var name = ExecutableName(exe);

        if (name == "flatpak")
        {
            var family = Known.FirstOrDefault(f => f.FlatpakId is { } id && command.Contains(id, StringComparer.Ordinal));
            if (family is null) return null;
            var app = Path.Combine(paths.Home, ".var", "app", family.FlatpakId!);
            return (family, family.Store == ProfileStore.Firefox
                ? [Path.Combine(app, ".mozilla", "firefox"), Path.Combine(app, "config", "mozilla", "firefox")]
                : family.LinuxConfigDir is { } rel ? [Path.Combine(app, "config", rel)] : []);
        }

        var match = Known.FirstOrDefault(f => f.ExecutableNames.Contains(name, StringComparer.Ordinal));
        if (match is null) return null;

        if (paths.IsWindows)
        {
            return (match, match.Store switch
            {
                ProfileStore.Firefox when paths.AppData is { } ad => [Path.Combine(ad, "Mozilla", "Firefox")],
                ProfileStore.Chromium when paths.LocalAppData is { } lad && match.WindowsDataDir is { } rel => [Path.Combine(lad, rel)],
                _ => [],
            });
        }

        if (exe.StartsWith("/snap/bin/", StringComparison.Ordinal))
        {
            var common = Path.Combine(paths.Home, "snap", name, "common");
            var roots = new List<string>();
            if (match.Store == ProfileStore.Firefox) roots.Add(Path.Combine(common, ".mozilla", "firefox"));
            if (match.Store == ProfileStore.Chromium)
            {
                roots.Add(Path.Combine(common, name));
                if (match.LinuxConfigDir is { } r && r != name) roots.Add(Path.Combine(common, r));
            }
            return (match, roots);
        }

        return (match, match.Store switch
        {
            // Firefox 147+ uses XDG for new installs but keeps ~/.mozilla when it already exists.
            ProfileStore.Firefox => [Path.Combine(paths.Home, ".mozilla", "firefox"), Path.Combine(paths.ConfigHome, "mozilla", "firefox")],
            ProfileStore.Chromium when match.LinuxConfigDir is { } rel => [Path.Combine(paths.ConfigHome, rel)],
            _ => [],
        });
    }

    /// <summary>Profiles as (display name, extra arguments). Reads the first root that has any.</summary>
    public IReadOnlyList<(string Label, string[] Args)> ReadProfiles(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var found = Store switch
            {
                ProfileStore.Firefox => FirefoxProfiles.Read(root),
                ProfileStore.Chromium => ReadChromium(root),
                _ => [],
            };
            if (found.Count > 0) return found;
        }
        return [];
    }

    /// <summary>The known family an executable belongs to, by file name (e.g. both <c>chrome.exe</c> and <c>google-chrome</c>).</summary>
    public static BrowserFamily? Of(string exe)
    {
        var name = ExecutableName(exe);
        return Known.FirstOrDefault(f => f.ExecutableNames.Contains(name, StringComparer.Ordinal));
    }

    internal static string ExecutableName(string exe)
    {
        var file = exe.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        return file.ToLowerInvariant();
    }

    internal static List<(string, string[])> ReadChromium(string root)
    {
        var file = Path.Combine(root, "Local State");
        try
        {
            if (!File.Exists(file)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!doc.RootElement.TryGetProperty("profile", out var profile) ||
                !profile.TryGetProperty("info_cache", out var cache) ||
                cache.ValueKind != JsonValueKind.Object)
                return [];

            // profiles_order is the order Chrome's own profile picker shows.
            var order = profile.TryGetProperty("profiles_order", out var po) && po.ValueKind == JsonValueKind.Array
                ? po.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];
            return cache.EnumerateObject()
                .Where(p => !p.Name.Contains('/') && !p.Name.Contains('\\') && Directory.Exists(Path.Combine(root, p.Name)))
                .OrderBy(p => order.IndexOf(p.Name) is var i && i >= 0 ? i : int.MaxValue)
                .ThenBy(p => p.Name == "Default" ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(p =>
                {
                    var label = p.Value.ValueKind == JsonValueKind.Object &&
                                p.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s
                        ? s
                        : p.Name;
                    return (label, new[] { "--profile-directory=" + p.Name });
                })
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
