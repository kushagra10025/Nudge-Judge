using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Platform;

namespace NudgeJudge.Core.Tests;

public sealed class LinuxLaunchAtStartupTests : IDisposable
{
    private readonly string _configHome = Path.Combine(Path.GetTempPath(), "nj-autostart-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] App = ["/opt/NudgeJudge/NudgeJudge.Desktop"];

    public void Dispose()
    {
        if (Directory.Exists(_configHome)) Directory.Delete(_configHome, recursive: true);
    }

    private LinuxLaunchAtStartup Startup(IReadOnlyList<string>? command = null) => new(_configHome, command ?? App);

    private string? Exec(LinuxLaunchAtStartup s) => IniFile.TryLoad(s.EntryPath)?.Get("Desktop Entry", "Exec");

    [Fact]
    public void Off_until_enabled()
    {
        Assert.False(Startup().IsEnabled());
    }

    [Fact]
    public void Enabling_writes_an_autostart_entry_that_starts_in_the_tray()
    {
        var startup = Startup();

        startup.SetEnabled(true);

        Assert.True(startup.IsEnabled());
        Assert.Equal(Path.Combine(_configHome, "autostart", "nudgejudge.desktop"), startup.EntryPath);
        Assert.Equal("/opt/NudgeJudge/NudgeJudge.Desktop --background", Exec(startup));
        Assert.DoesNotContain("%u", File.ReadAllText(startup.EntryPath));
    }

    [Fact]
    public void Paths_with_spaces_are_quoted()
    {
        var startup = Startup(["/home/me/My Apps/NudgeJudge.Desktop"]);

        startup.SetEnabled(true);

        Assert.Equal("\"/home/me/My Apps/NudgeJudge.Desktop\" --background", Exec(startup));
    }

    [Fact]
    public void Disabling_removes_the_entry()
    {
        var startup = Startup();
        startup.SetEnabled(true);

        startup.SetEnabled(false);

        Assert.False(startup.IsEnabled());
        Assert.False(File.Exists(startup.EntryPath));
        startup.SetEnabled(false); // already gone: no error
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void An_entry_the_desktop_switched_off_counts_as_off(string line)
    {
        var startup = Startup();
        Directory.CreateDirectory(Path.GetDirectoryName(startup.EntryPath)!);
        File.WriteAllText(startup.EntryPath, $"[Desktop Entry]\nType=Application\nExec=/x --background\n{line}\n");

        Assert.False(startup.IsEnabled());
    }

    [Fact]
    public void Refresh_points_an_existing_entry_at_the_current_install()
    {
        Startup(["/old/place/NudgeJudge.Desktop"]).SetEnabled(true);
        var startup = Startup();

        startup.Refresh();

        Assert.Equal("/opt/NudgeJudge/NudgeJudge.Desktop --background", Exec(startup));
    }

    [Fact]
    public void Refresh_never_turns_it_on()
    {
        var startup = Startup();

        startup.Refresh();

        Assert.False(File.Exists(startup.EntryPath));
    }

    [Fact]
    public void Unsupported_without_a_launch_command()
    {
        var startup = new LinuxLaunchAtStartup(_configHome, null);

        Assert.False(startup.IsSupported);
        Assert.Throws<IOException>(() => startup.SetEnabled(true));
    }
}

public sealed class WindowsLaunchAtStartupTests
{
    [Fact]
    public void Run_value_quotes_each_argument_and_starts_in_the_tray()
    {
        Assert.Equal("\"C:\\Program Files\\NudgeJudge\\NudgeJudge.exe\" --background",
            WindowsRunCommand.Build([@"C:\Program Files\NudgeJudge\NudgeJudge.exe"]));
        Assert.Equal("\"C:\\dotnet\\dotnet.exe\" \"C:\\app\\NudgeJudge.Desktop.dll\" --background",
            WindowsRunCommand.Build([@"C:\dotnet\dotnet.exe", @"C:\app\NudgeJudge.Desktop.dll"]));
    }

    [Fact]
    public void Unwritable_commands_are_refused()
    {
        Assert.Null(WindowsRunCommand.Build(["C:\\a\"b.exe"]));
    }
}
