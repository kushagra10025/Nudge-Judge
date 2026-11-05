using NudgeJudge.Core.Platform;

namespace NudgeJudge.Core.Tests;

/// <summary>How the app starts its tray host and the tray host starts the window process.</summary>
public sealed class CompanionLaunchTests
{
    private static readonly string Bin = Path.Combine(Path.GetTempPath(), "nj-bin");
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";
    private static readonly string Muxer = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    private static readonly string[] HostSearched = [Path.Combine(Path.GetTempPath(), "usr-lib-dotnet")];

    private static Func<string, bool> Files(params string[] names) =>
        path => names.Any(n => path == Path.Combine(Bin, n));

    [Fact]
    public void A_per_user_runtime_the_launcher_cant_find_runs_the_dll_on_this_runtime()
    {
        // Started from a browser or the desktop there's no DOTNET_ROOT, so NudgeJudge.Tray's launcher would fail.
        var userDotnet = Path.Combine(Path.GetTempPath(), "home", ".dotnet");
        Assert.Equal([Path.Combine(userDotnet, Muxer), Path.Combine(Bin, "NudgeJudge.Tray.dll")],
            AppLaunchCommand.ResolveCompanion(Bin, "NudgeJudge.Tray", null, null, userDotnet, HostSearched,
                Files("NudgeJudge.Tray" + Exe, "NudgeJudge.Tray.dll")));
    }

    [Fact]
    public void A_runtime_the_launcher_finds_by_itself_uses_the_launcher()
    {
        Assert.Equal([Path.Combine(Bin, "NudgeJudge.Tray" + Exe)],
            AppLaunchCommand.ResolveCompanion(Bin, "NudgeJudge.Tray", null, null, HostSearched[0], HostSearched,
                Files("NudgeJudge.Tray" + Exe, "NudgeJudge.Tray.dll")));
    }

    [Fact]
    public void A_native_build_has_no_runtime_and_uses_the_launcher()
    {
        Assert.Equal([Path.Combine(Bin, "NudgeJudge.Tray" + Exe)],
            AppLaunchCommand.ResolveCompanion(Bin, "NudgeJudge.Tray", null, null, null, HostSearched, Files("NudgeJudge.Tray" + Exe)));
    }

    [Fact]
    public void Inside_an_AppImage_it_goes_through_the_image()
    {
        Assert.Equal(["/home/me/NudgeJudge.AppImage", "--tray-host"],
            AppLaunchCommand.ResolveCompanion(Bin, "NudgeJudge.Tray", "--tray-host", "/home/me/NudgeJudge.AppImage", "/x", HostSearched, _ => true));
    }

    [Fact]
    public void Nothing_there_means_null()
    {
        Assert.Null(AppLaunchCommand.ResolveCompanion(Bin, "NudgeJudge.Tray", null, null, null, HostSearched, _ => false));
    }
}
