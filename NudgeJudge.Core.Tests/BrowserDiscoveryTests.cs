using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Tests;

public sealed class BrowserDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nj-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly DiscoveryPaths _paths;

    public BrowserDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DiscoveryPaths
        {
            Home = Dir("home"),
            ConfigHome = Dir("home/.config"),
            DataDirs = [Dir("home/.local/share"), Dir("usr/share")],
            SearchPath = [Dir("bin")],
        };
        Executable("bin/firefox");
        Executable("bin/flatpak");
        Executable("opt/google/chrome-stable");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string rel) => Directory.CreateDirectory(Path.Combine(_root, rel)).FullName;

    private string Executable(string rel)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private void DesktopFile(string dataDir, string name, string contents) =>
        File.WriteAllText(Path.Combine(Dir(dataDir + "/applications"), name), contents);

    private IReadOnlyList<BrowserProfile> Discover() => LinuxBrowserDiscovery.Discover(_paths);

    private const string Firefox = """
        [Desktop Entry]
        Type=Application
        Name=Firefox Web Browser
        Name[de]=Firefox-Webbrowser
        Exec=firefox %u
        Categories=GNOME;GTK;Network;WebBrowser;
        Actions=new-window;new-private-window;

        [Desktop Action new-window]
        Name=Open a New Window
        Exec=firefox -new-window

        [Desktop Action new-private-window]
        Name=Open a New Private Window
        Exec=firefox -private-window
        """;

    [Fact]
    public void Linux_finds_browser_and_private_action()
    {
        DesktopFile("usr/share", "firefox.desktop", Firefox);

        var found = Discover();

        Assert.Collection(found,
            b =>
            {
                Assert.Equal("Firefox", b.Name);
                Assert.Equal("firefox", b.ExecutablePath);
                Assert.Equal("", b.Arguments);
                Assert.False(b.IsPrivate);
                Assert.Equal(BrowserSource.Discovered, b.Source);
            },
            b =>
            {
                Assert.Equal("Firefox Private", b.Name);
                Assert.Equal("-private-window", b.Arguments);
                Assert.True(b.IsPrivate);
            });
    }

    [Fact]
    public void Linux_skips_hidden_nodisplay_non_browsers_and_missing_executables()
    {
        DesktopFile("usr/share", "alias.desktop", "[Desktop Entry]\nType=Application\nName=Alias\nExec=firefox %u\nCategories=WebBrowser;\nNoDisplay=true\n");
        DesktopFile("usr/share", "editor.desktop", "[Desktop Entry]\nType=Application\nName=Editor\nExec=firefox %F\nMimeType=text/html;\n");
        DesktopFile("usr/share", "gone.desktop", "[Desktop Entry]\nType=Application\nName=Gone\nExec=not-installed %u\nCategories=WebBrowser;\n");
        DesktopFile("usr/share", "tryexec.desktop", "[Desktop Entry]\nType=Application\nName=TryExec\nTryExec=missing\nExec=firefox %u\nCategories=WebBrowser;\n");
        DesktopFile("usr/share", "nudgejudge.desktop", "[Desktop Entry]\nType=Application\nName=NudgeJudge\nExec=nudgejudge %u\nMimeType=x-scheme-handler/http;x-scheme-handler/https;\n");

        Assert.Empty(Discover());
    }

    [Fact]
    public void Linux_user_entry_shadows_system_entry_even_when_hidden()
    {
        DesktopFile("usr/share", "firefox.desktop", Firefox);
        DesktopFile("home/.local/share", "firefox.desktop", "[Desktop Entry]\nType=Application\nHidden=true\n");

        Assert.Empty(Discover());
    }

    [LinuxFact]
    public void Linux_accepts_http_and_https_handlers_without_category()
    {
        var chrome = Path.Combine(_root, "opt/google/chrome-stable");
        DesktopFile("usr/share", "c.desktop",
            $"[Desktop Entry]\nType=Application\nName=Odd Browser\nExec=\"{chrome}\" --flag %U\nMimeType=text/html;x-scheme-handler/http;x-scheme-handler/https;\n");

        var b = Assert.Single(Discover());
        Assert.Equal(chrome, b.ExecutablePath);
        Assert.Equal("--flag", b.Arguments);
    }

    [LinuxFact]
    public void Linux_flatpak_entry_keeps_run_command_and_gets_private_flag()
    {
        var flatpak = Path.Combine(_root, "bin/flatpak");
        DesktopFile("usr/share", "org.mozilla.firefox.desktop",
            $"[Desktop Entry]\nType=Application\nName=Firefox\nExec={flatpak} run --branch=stable --command=firefox --file-forwarding org.mozilla.firefox @@u %u @@\nCategories=WebBrowser;\n");

        var found = Discover();

        Assert.Equal(2, found.Count);
        Assert.Equal("run --branch=stable --command=firefox --file-forwarding org.mozilla.firefox", found[0].Arguments);
        Assert.Equal("run --branch=stable --command=firefox --file-forwarding org.mozilla.firefox -private-window", found[1].Arguments);
    }

    [Fact]
    public void Firefox_profiles_listed_only_when_more_than_one_is_real()
    {
        DesktopFile("usr/share", "firefox.desktop", Firefox);
        var ff = Dir("home/.mozilla/firefox");
        File.WriteAllText(Path.Combine(ff, "profiles.ini"), """
            [Profile0]
            Name=default-release
            IsRelative=1
            Path=abc.default-release

            [Profile1]
            Name=default
            IsRelative=1
            Path=old.default

            [Profile2]
            Name=Work Stuff
            IsRelative=1
            Path=xyz.work
            """);
        File.WriteAllText(Path.Combine(Dir("home/.mozilla/firefox/abc.default-release"), "prefs.js"), "");
        Dir("home/.mozilla/firefox/old.default"); // never used: no prefs.js
        File.WriteAllText(Path.Combine(Dir("home/.mozilla/firefox/xyz.work"), "prefs.js"), "");

        var profiles = Discover().Where(b => b.ProfileLabel is not null).ToList();

        Assert.Equal(["default-release", "Work Stuff"], profiles.Select(p => p.ProfileLabel));
        var work = Path.Combine(ff, "xyz.work");
        Assert.Equal(["--profile", work, "https://a.com/"], BrowserLauncher.BuildArguments(profiles[1].Arguments, new Uri("https://a.com")));
    }

    [Fact]
    public void Firefox_profile_manager_store_adds_profiles_and_names()
    {
        DesktopFile("usr/share", "firefox.desktop", Firefox);
        var ff = Dir("home/.mozilla/firefox");
        File.WriteAllText(Path.Combine(ff, "profiles.ini"), """
            [Profile0]
            Name=default-release
            IsRelative=1
            Path=abc.default-release
            StoreID=s1

            [Profile1]
            Name=default
            IsRelative=1
            Path=old.default
            """);
        File.WriteAllText(Path.Combine(Dir("home/.mozilla/firefox/abc.default-release"), "prefs.js"), "");
        Dir("home/.mozilla/firefox/old.default");
        Dir("home/.mozilla/firefox/qrs.Profile 1"); // exists only in the store
        CreateStore(Path.Combine(Dir("home/.mozilla/firefox/Profile Groups"), "s1.sqlite"),
            ("abc.default-release", "Personal"), ("qrs.Profile 1", "Work"), ("deleted.Profile 2", "Gone"));

        var profiles = Discover().Where(b => b.ProfileLabel is not null).ToList();

        Assert.Equal(["Firefox (Personal)", "Firefox (Work)"], profiles.Select(p => p.Name));
        Assert.Equal(["--profile", Path.Combine(ff, "qrs.Profile 1"), "https://a.com/"],
            BrowserLauncher.BuildArguments(profiles[1].Arguments, new Uri("https://a.com")));
    }

    [Fact]
    public void Firefox_unreadable_store_falls_back_to_profiles_ini()
    {
        var ff = Dir("ff");
        File.WriteAllText(Path.Combine(ff, "profiles.ini"), "[Profile0]\nName=a\nPath=a\nStoreID=bad\n[Profile1]\nName=b\nPath=b\n");
        File.WriteAllText(Path.Combine(Dir("ff/a"), "prefs.js"), "");
        File.WriteAllText(Path.Combine(Dir("ff/b"), "prefs.js"), "");
        File.WriteAllText(Path.Combine(Dir("ff/Profile Groups"), "bad.sqlite"), "not a database");

        Assert.Equal(["a", "b"], FirefoxProfiles.Read(ff).Select(p => p.Label));
    }

    private static void CreateStore(string file, params (string Path, string Name)[] rows)
    {
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = "CREATE TABLE Profiles (id INTEGER NOT NULL PRIMARY KEY, path TEXT NOT NULL UNIQUE, name TEXT NOT NULL, avatar TEXT NOT NULL DEFAULT '')";
            create.ExecuteNonQuery();
            foreach (var (path, name) in rows)
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO Profiles (path, name) VALUES ($p, $n)";
                insert.Parameters.AddWithValue("$p", path);
                insert.Parameters.AddWithValue("$n", name);
                insert.ExecuteNonQuery();
            }
        }
    }

    [LinuxFact]
    public void Chromium_profiles_read_from_local_state()
    {
        var linkDir = Dir("bin2");
        var exe = Path.Combine(linkDir, "google-chrome-stable");
        File.WriteAllText(exe, "");
        DesktopFile("usr/share", "google-chrome.desktop",
            $"[Desktop Entry]\nType=Application\nName=Google Chrome\nExec={exe} %U\nCategories=WebBrowser;\n");
        var data = Dir("home/.config/google-chrome");
        File.WriteAllText(Path.Combine(data, "Local State"),
            """{"profile":{"profiles_order":["Profile 1","Default"],"info_cache":{"Profile 1":{"name":"Work"},"Default":{"name":"Personal"},"Profile 9":{"name":"Deleted"}}}}""");
        Dir("home/.config/google-chrome/Default");
        Dir("home/.config/google-chrome/Profile 1");

        var found = Discover();

        // Chrome's own picker order (profiles_order), not alphabetical.
        Assert.Equal(["Google Chrome", "Google Chrome Incognito", "Google Chrome (Work)", "Google Chrome (Personal)"], found.Select(b => b.Name));
        Assert.Equal("\"--profile-directory=Profile 1\"", found[2].Arguments);
        Assert.Equal(["--profile-directory=Profile 1", "https://a.com/"], BrowserLauncher.BuildArguments(found[2].Arguments, new Uri("https://a.com")));
    }

    [Fact]
    public void Chromium_profiles_read_from_its_own_config_dir()
    {
        File.WriteAllText(Path.Combine(_root, "bin/chromium"), "");
        DesktopFile("usr/share", "chromium.desktop",
            "[Desktop Entry]\nType=Application\nName=Chromium Web Browser\nExec=chromium %U\nCategories=WebBrowser;\n");
        var data = Dir("home/.config/chromium");
        File.WriteAllText(Path.Combine(data, "Local State"),
            """{"profile":{"info_cache":{"Default":{"name":"Me"},"Profile 2":{"name":"Testing"}}}}""");
        Dir("home/.config/chromium/Default");
        Dir("home/.config/chromium/Profile 2");

        Assert.Equal(["Chromium", "Chromium Incognito", "Chromium (Me)", "Chromium (Testing)"], Discover().Select(b => b.Name));
    }

    [Theory]
    [InlineData("firefox %u", new[] { "firefox", "{url}" })]
    [InlineData("\"/opt/My Browser/run\" --x %U", new[] { "/opt/My Browser/run", "--x", "{url}" })]
    [InlineData("browser --icon %i --name=%c %u %%done", new[] { "browser", "--icon", "--name=", "{url}", "%done" })]
    [InlineData("env A=1 firefox %u", new[] { "env", "A=1", "firefox", "{url}" })]
    [InlineData("b \"say \\\"hi\\\"\"", new[] { "b", "say \"hi\"" })]
    public void Exec_parsing(string exec, string[] expected) =>
        Assert.Equal(expected, LinuxBrowserDiscovery.ParseExec(exec));

    [Fact]
    public void Exec_with_unterminated_quote_is_rejected() =>
        Assert.Null(LinuxBrowserDiscovery.ParseExec("\"broken"));

    [Theory]
    [InlineData("a\\sb", "a b")]
    [InlineData("a\\\\b", "a\\b")]
    [InlineData("a\\\"b", "a\\\"b")]
    public void Desktop_string_unescape(string raw, string expected) =>
        Assert.Equal(expected, LinuxBrowserDiscovery.Unescape(raw));

    [Fact]
    public void Env_prefix_is_skipped_for_recognition()
    {
        Assert.Equal(["/snap/bin/firefox", "{url}"],
            BrowserVariants.WithoutEnv(["env", "BAMF_DESKTOP_FILE_HINT=/x.desktop", "/snap/bin/firefox", "{url}"]));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"", "C:\\Program Files\\Mozilla Firefox\\firefox.exe")]
    [InlineData("C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe -- \"%1\"", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    [InlineData("relative.exe %1", null)]
    [InlineData("", null)]
    public void Windows_command_executable(string command, string? expected)
    {
        // Rooted-path checks differ by OS; only assert parsing where the result would be rooted here too.
        var exe = WindowsRegistryBrowsers.ExecutableOf(command);
        if (expected is null || OperatingSystem.IsWindows()) Assert.Equal(expected, exe);
    }

    [Fact]
    public void Windows_entries_dedupe_skip_ie_and_add_private()
    {
        var paths = _paths with { IsWindows = true, LocalAppData = Dir("lad"), AppData = Dir("ad") };
        var edge = Path.Combine(_root, "msedge.exe");
        var ie = Path.Combine(_root, "iexplore.exe");
        var missing = Path.Combine(_root, "gone.exe");
        var entries = new (string, string?)[]
        {
            ("Microsoft Edge", $"\"{edge}\" --single-argument %1"),
            ("Microsoft Edge", $"\"{edge}\" --single-argument %1"),
            ("Internet Explorer", $"\"{ie}\""),
            ("Gone", $"\"{missing}\""),
            ("Broken", null),
        };

        var found = WindowsRegistryBrowsers.FromEntries(entries, paths, p => p != missing, selfPath: null);

        Assert.Equal(["Microsoft Edge", "Microsoft Edge InPrivate"], found.Select(b => b.Name));
        Assert.All(found, b => Assert.Equal(edge, b.ExecutablePath));
        Assert.Equal(["", "--inprivate"], found.Select(b => b.Arguments));
    }

    [Theory]
    [InlineData(new[] { "--private-window" }, "--private-window")]
    [InlineData(new[] { "--profile-directory=Profile 1", "{url}" }, "\"--profile-directory=Profile 1\" {url}")]
    [InlineData(new[] { "it's" }, "\"it's\"")]
    [InlineData(new[] { "say \"hi\"" }, "'say \"hi\"'")]
    [InlineData(new[] { "" }, "\"\"")]
    public void Command_line_join_round_trips(string[] args, string expected)
    {
        var joined = CommandLine.Join(args);
        Assert.Equal(expected, joined);
        Assert.Equal(args, CommandLine.Split(joined!));
    }

    [Fact]
    public void Command_line_join_rejects_unrepresentable() =>
        Assert.Null(CommandLine.Join(["both ' and \""]));
}
