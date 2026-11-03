using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Platform;

namespace NudgeJudge.Core.Tests;

/// <summary>Records xdg-mime calls and keeps the defaults they set, like a tiny mimeapps.list.</summary>
internal sealed class FakeXdg : ICommandRunner
{
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];
    public bool Installed { get; set; } = true;
    public CommandResult? FailSet { get; set; }

    public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(program + " " + string.Join(' ', arguments));
        if (!Installed) return Task.FromResult(CommandResult.NotFound);

        CommandResult result = (program, arguments.FirstOrDefault()) switch
        {
            ("xdg-mime", "query") => new(true, 0, Defaults.GetValueOrDefault(arguments[2], "") + "\n", ""),
            ("xdg-mime", "default") when FailSet is { } fail => fail,
            ("xdg-mime", "default") => Set(arguments[1], arguments.Skip(2)),
            ("update-desktop-database", _) => new(true, 0, "", ""),
            _ => CommandResult.NotFound,
        };
        return Task.FromResult(result);
    }

    private CommandResult Set(string desktopId, IEnumerable<string> mimeTypes)
    {
        foreach (var mime in mimeTypes) Defaults[mime] = desktopId;
        return new(true, 0, "", "");
    }
}

public sealed class LinuxRegistrarTests : IDisposable
{
    private readonly string _dataHome = Path.Combine(Path.GetTempPath(), "nj-register-" + Guid.NewGuid().ToString("N"));
    private readonly FakeXdg _xdg = new();
    private static readonly string[] App = ["/opt/NudgeJudge/NudgeJudge.Desktop"];

    public void Dispose()
    {
        if (Directory.Exists(_dataHome)) Directory.Delete(_dataHome, recursive: true);
    }

    private LinuxDefaultBrowserRegistrar Registrar(IReadOnlyList<string>? command = null, Func<byte[]?>? icon = null) =>
        new(_dataHome, command ?? App, _xdg, icon);

    [Fact]
    public async Task Fresh_system_is_not_registered()
    {
        _xdg.Defaults["x-scheme-handler/http"] = "firefox.desktop";
        _xdg.Defaults["x-scheme-handler/https"] = "firefox.desktop";

        var status = await Registrar().GetStatusAsync();

        Assert.Equal(RegistrationState.NotRegistered, status.State);
        Assert.Contains("another browser", status.Message);
    }

    [Fact]
    public async Task Register_writes_desktop_entry_and_claims_both_schemes()
    {
        var registrar = Registrar();

        var status = await registrar.RegisterAsync();

        Assert.Equal(RegistrationState.Registered, status.State);
        Assert.Equal(LinuxDefaultBrowserRegistrar.DesktopId, _xdg.Defaults["x-scheme-handler/http"]);
        Assert.Equal(LinuxDefaultBrowserRegistrar.DesktopId, _xdg.Defaults["x-scheme-handler/https"]);
        Assert.True(File.Exists(registrar.DesktopFilePath));
        Assert.Equal(Path.Combine(_dataHome, "applications", "nudgejudge.desktop"), registrar.DesktopFilePath);
    }

    [Fact]
    public async Task Register_never_claims_html_files()
    {
        // The router rejects file:// links, so local HTML must keep opening in a real browser.
        await Registrar().RegisterAsync();

        Assert.DoesNotContain(_xdg.Calls, c => c.Contains("text/html"));
        Assert.DoesNotContain(_xdg.Calls, c => c.StartsWith("xdg-settings"));
        var entry = IniFile.TryLoad(Registrar().DesktopFilePath)!;
        Assert.Equal("x-scheme-handler/http;x-scheme-handler/https;", entry.Get("Desktop Entry", "MimeType"));
    }

    [Fact]
    public async Task Desktop_entry_is_found_by_our_own_discovery_rules()
    {
        await Registrar().RegisterAsync();
        var entry = IniFile.TryLoad(Registrar().DesktopFilePath)!;

        Assert.Equal("Application", entry.Get("Desktop Entry", "Type"));
        Assert.Equal("NudgeJudge", entry.Get("Desktop Entry", "Name"));
        Assert.Contains("WebBrowser", entry.Get("Desktop Entry", "Categories"));
        // Discovery must skip NudgeJudge itself, or it would offer to route links back to itself.
        var paths = new DiscoveryPaths { Home = _dataHome, ConfigHome = _dataHome, SearchPath = ["/opt/NudgeJudge"] };
        Assert.Empty(LinuxBrowserDiscovery.FromEntry(LinuxDefaultBrowserRegistrar.DesktopId, entry, paths));
    }

    [Theory]
    [InlineData("/opt/NudgeJudge/NudgeJudge.Desktop")]
    [InlineData("/home/me/$weird`dir\"/back\\slash/100%/NudgeJudge")]
    [InlineData("/home/me/a;b&c|d(e)*?#~/nj")]
    public async Task Exec_line_round_trips_through_the_desktop_entry_parser(string executable)
    {
        await Registrar([executable]).RegisterAsync();
        var exec = IniFile.TryLoad(Registrar().DesktopFilePath)!.Get("Desktop Entry", "Exec")!;

        var argv = LinuxBrowserDiscovery.ParseExec(LinuxBrowserDiscovery.Unescape(exec));

        Assert.Equal([executable, BrowserLauncher.UrlPlaceholder], argv);
    }

    [Fact]
    public async Task Program_path_with_spaces_goes_through_a_wrapper_xdg_utils_can_read()
    {
        var registrar = Registrar(["/home/me/My Apps/NudgeJudge"]);

        var status = await registrar.RegisterAsync();
        var exec = IniFile.TryLoad(registrar.DesktopFilePath)!.Get("Desktop Entry", "Exec")!;

        Assert.Equal(RegistrationState.Registered, status.State);
        Assert.Equal($"{registrar.LauncherPath} %u", exec);
    }

    [Fact]
    public async Task Wrapper_runs_the_app_with_the_link_intact()
    {
        if (!OperatingSystem.IsLinux()) return;

        // A program whose path needs every kind of shell quoting, which echoes its argv.
        var dir = Path.Combine(_dataHome, "My Apps", "it's \"odd\" $HOME `x`");
        Directory.CreateDirectory(dir);
        var program = Path.Combine(dir, "nudge judge");
        File.WriteAllText(program, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var registrar = Registrar([program, "--from-handler"]);
        await registrar.RegisterAsync();

        const string link = "https://example.com/a b?q=$(id)&x=`id`'\"";
        var result = await new ProcessCommandRunner().RunAsync(registrar.LauncherPath, [link]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["--from-handler", link], result.Output.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task Edited_wrapper_counts_as_not_current()
    {
        var registrar = Registrar(["/home/me/My Apps/NudgeJudge"]);
        await registrar.RegisterAsync();

        File.WriteAllText(registrar.LauncherPath, "#!/bin/sh\nexec /somewhere/else \"$@\"\n");

        Assert.Contains("moved", (await registrar.GetStatusAsync()).Message);
    }

    [Fact]
    public async Task Dotnet_host_command_keeps_the_dll_argument()
    {
        string[] command = ["/usr/share/dotnet/dotnet", "/src/bin/NudgeJudge.Desktop.dll"];
        await Registrar(command).RegisterAsync();
        var exec = IniFile.TryLoad(Registrar().DesktopFilePath)!.Get("Desktop Entry", "Exec")!;

        Assert.Equal([.. command, BrowserLauncher.UrlPlaceholder],
            LinuxBrowserDiscovery.ParseExec(LinuxBrowserDiscovery.Unescape(exec)));
    }

    [Fact]
    public async Task Icon_is_installed_and_referenced_when_supplied()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        var registrar = Registrar(icon: () => png);

        await registrar.RegisterAsync();

        Assert.Equal(png, File.ReadAllBytes(registrar.IconPath));
        Assert.Equal(registrar.IconPath, IniFile.TryLoad(registrar.DesktopFilePath)!.Get("Desktop Entry", "Icon"));
    }

    [Fact]
    public async Task Moving_the_app_after_registering_is_reported()
    {
        await Registrar(["/old/place/NudgeJudge"]).RegisterAsync();

        var status = await Registrar(["/new/place/NudgeJudge"]).GetStatusAsync();

        Assert.Equal(RegistrationState.NotRegistered, status.State);
        Assert.Contains("moved", status.Message);
    }

    [Fact]
    public async Task Registering_again_after_a_move_fixes_it()
    {
        await Registrar(["/old/place/NudgeJudge"]).RegisterAsync();

        var status = await Registrar(["/new/place/NudgeJudge"]).RegisterAsync();

        Assert.Equal(RegistrationState.Registered, status.State);
    }

    [Fact]
    public async Task Half_registration_names_the_scheme_that_escapes()
    {
        await Registrar().RegisterAsync();
        _xdg.Defaults["x-scheme-handler/http"] = "firefox.desktop";

        var status = await Registrar().GetStatusAsync();

        Assert.Equal(RegistrationState.NotRegistered, status.State);
        Assert.Contains("Only HTTPS", status.Message);
    }

    [Fact]
    public async Task Missing_xdg_utils_is_explained()
    {
        _xdg.Installed = false;

        var check = await Registrar().GetStatusAsync();
        var register = await Registrar().RegisterAsync();

        Assert.Equal(RegistrationState.Unknown, check.State);
        Assert.Equal(RegistrationState.Unknown, register.State);
        Assert.Contains("xdg-utils", register.Message);
    }

    [Fact]
    public async Task Xdg_mime_failure_is_reported_with_its_error()
    {
        _xdg.FailSet = new CommandResult(true, 4, "", "xdg-mime: no permission\nmore");

        var status = await Registrar().RegisterAsync();

        Assert.Equal(RegistrationState.NotRegistered, status.State);
        Assert.Contains("no permission", status.Message);
    }

    [Fact]
    public async Task Unknown_launch_command_is_unsupported_and_touches_nothing()
    {
        var registrar = new LinuxDefaultBrowserRegistrar(_dataHome, null, _xdg);

        var status = await registrar.RegisterAsync();

        Assert.False(registrar.IsSupported);
        Assert.Equal(RegistrationState.Unsupported, status.State);
        Assert.Empty(_xdg.Calls);
        Assert.False(Directory.Exists(_dataHome));
    }

    [Fact]
    public void Arguments_with_control_characters_cannot_be_registered()
    {
        Assert.False(Registrar(["/opt/nj\n--evil"]).IsSupported);
    }
}

public sealed class RegistrationPlumbingTests
{
    [Fact]
    public void Launch_command_is_the_process_itself()
    {
        Assert.Equal(["/opt/nj/NudgeJudge.Desktop"],
            AppLaunchCommand.Resolve("/opt/nj/NudgeJudge.Desktop", "/opt/nj/NudgeJudge.Desktop.dll", null));
    }

    [Fact]
    public void Launch_command_through_the_dotnet_host_includes_the_dll()
    {
        const string host = "/usr/lib/dotnet/dotnet";
        Assert.Equal([host, "/src/NudgeJudge.Desktop.dll"], AppLaunchCommand.Resolve(host, "/src/NudgeJudge.Desktop.dll", null));
        Assert.Null(AppLaunchCommand.Resolve(host, "", null));
    }

    [Fact]
    public void Launch_command_prefers_the_appimage_file()
    {
        Assert.Equal(["/home/me/NudgeJudge.AppImage"],
            AppLaunchCommand.Resolve("/tmp/.mount_abc/usr/bin/NudgeJudge", null, "/home/me/NudgeJudge.AppImage"));
    }

    [Fact]
    public void Launch_command_needs_an_absolute_path()
    {
        Assert.Null(AppLaunchCommand.Resolve(null, null, null));
        Assert.Null(AppLaunchCommand.Resolve("NudgeJudge", null, "relative.AppImage"));
    }

    [Theory]
    [InlineData(true, true, true, RegistrationState.Registered)]
    [InlineData(true, true, false, RegistrationState.NotRegistered)]
    [InlineData(true, false, true, RegistrationState.NotRegistered)]
    [InlineData(false, false, true, RegistrationState.NotRegistered)]
    public void Evaluate_requires_both_schemes_and_a_current_handler(bool http, bool https, bool current, RegistrationState expected)
    {
        Assert.Equal(expected, DefaultBrowserRegistration.Evaluate(http, https, current).State);
    }

    [Fact]
    public async Task Stub_is_unsupported()
    {
        var stub = new StubDefaultBrowserRegistrar();
        Assert.False(stub.IsSupported);
        Assert.Equal(RegistrationState.Unsupported, (await stub.RegisterAsync()).State);
    }
}

public sealed class WindowsRegistrationPlanTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\NudgeJudge\NudgeJudge.Desktop.exe";

    private static string? ValueOf(WindowsRegistration.Plan plan, string key, string? name) =>
        plan.Values.SingleOrDefault(v => v.Key == key && v.Name == name)?.Data;

    [Fact]
    public void Open_command_quotes_the_program_and_the_link()
    {
        var plan = WindowsRegistration.Build([Exe])!;

        Assert.Equal($"\"{Exe}\" \"%1\"", plan.OpenCommand);
        Assert.Equal(plan.OpenCommand, ValueOf(plan, WindowsRegistration.ProgIdCommandKey, null));
    }

    [Fact]
    public void Both_schemes_point_at_the_progid()
    {
        var plan = WindowsRegistration.Build([Exe])!;
        var urls = WindowsRegistration.CapabilitiesKey + @"\URLAssociations";

        Assert.Equal(WindowsRegistration.ProgId, ValueOf(plan, urls, "http"));
        Assert.Equal(WindowsRegistration.ProgId, ValueOf(plan, urls, "https"));
    }

    [Fact]
    public void App_is_listed_for_default_apps()
    {
        var plan = WindowsRegistration.Build([Exe])!;

        Assert.Equal(WindowsRegistration.CapabilitiesKey, ValueOf(plan, @"Software\RegisteredApplications", "NudgeJudge"));
        Assert.Equal("NudgeJudge", ValueOf(plan, WindowsRegistration.CapabilitiesKey, "ApplicationName"));
        Assert.Equal($"\"{Exe}\"", ValueOf(plan, WindowsRegistration.ClientKey + @"\shell\open\command", null));
    }

    [Fact]
    public void Everything_is_per_user()
    {
        // Keys are written under HKCU, so they must be relative paths, never a hive prefix.
        var plan = WindowsRegistration.Build([Exe])!;
        Assert.All(plan.Values, v => Assert.StartsWith(@"Software\", v.Key));
    }

    [Fact]
    public void Our_client_is_skipped_by_windows_discovery()
    {
        var plan = WindowsRegistration.Build([Exe])!;
        var name = ValueOf(plan, WindowsRegistration.ClientKey, null)!;

        var found = WindowsRegistryBrowsers.FromEntries([(name, plan.OpenCommand)],
            new DiscoveryPaths { Home = "", ConfigHome = "", IsWindows = true }, _ => true, selfPath: null);

        Assert.Empty(found);
    }

    [Fact]
    public void Dotnet_host_command_is_quoted_per_argument()
    {
        var plan = WindowsRegistration.Build([@"C:\Program Files\dotnet\dotnet.exe", @"C:\src\NudgeJudge.Desktop.dll"])!;

        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\src\\NudgeJudge.Desktop.dll\" \"%1\"", plan.OpenCommand);
    }

    [Fact]
    public void Arguments_that_would_break_quoting_are_rejected()
    {
        Assert.Null(WindowsRegistration.Build([@"C:\a""b\nj.exe"]));
        Assert.Null(WindowsRegistration.Build([""]));
    }
}
