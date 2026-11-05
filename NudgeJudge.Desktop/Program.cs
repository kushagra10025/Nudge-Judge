using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Collections.Generic;
using Avalonia;
using Velopack;
using NudgeJudge.Core;
using NudgeJudge.Core.Ipc;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;

namespace NudgeJudge.Desktop;

internal sealed class Program
{
    // Don't use Avalonia or any SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: the installer and updater start the app with hook arguments, handled here and then exited.
        var velopack = VelopackApp.Build();
        // Only the Windows installer runs uninstall hooks (Linux ships as a single AppImage file).
        if (OperatingSystem.IsWindows()) velopack.OnBeforeUninstallFastCallback(_ => CleanUpBeforeUninstall());
        velopack.Run();

        // Started through the AppImage to become the tray host: become it in place, keeping the image mounted.
        if (args is ["--tray-host"]) return BecomeTrayHost();

        var launch = LaunchArguments.Parse(args);
        if (launch.Ui) return RunUiProcess(args);

        // First thing, while the app that opened the link is still our parent (helpers like xdg-open exit quickly).
        if (launch.Urls.Count > 0) launch = launch with { Source = SourceAppDetector.ForCurrentPlatform().Detect() };

        // Decided before Avalonia starts, so a launch that hands its links to the running instance exits at once.
        using var claim = ClaimInstance(launch);
        if (claim?.Forwarded == true) return 0;

        // Open the links before the UI exists: starting Avalonia costs far more than routing does.
        var core = CoreServices.Load();
        IReadOnlyList<IncomingLink> pending = launch.Urls.Count > 0 ? core.Route(launch.Urls, launch.Source) : [];

        // Staying in the tray: hand over to the small tray host, which never loads the UI. It starts a separate
        // UI process for windows and the chooser, which exits (and gives its memory back) when they close.
        if (claim?.IsPrimary == true && core.Config.Current.Settings.RunInBackground &&
            HandOverToTrayHost(claim, pending, showMain: launch.Urls.Count == 0 && !launch.Background))
            return 0;

        // Not staying: every link opened, or a login launch with nothing to show. Done, no UI needed.
        if (launch.Urls.Count > 0 ? pending.Count == 0 : launch.Background) return 0;

        var exit = BuildAvaloniaApp(claim, core, pending).StartWithClassicDesktopLifetime(args);

        // "Keep running in the tray" was switched on while the window was open: hand over to the tray host.
        core.Config.Refresh();
        if (claim?.IsPrimary == true && core.Config.Current.Settings.RunInBackground && !App.RestartingForUpdate)
            HandOverToTrayHost(claim, [], showMain: false);
        return exit;
    }

    /// <summary>
    /// Starts the tray host, lets go of the single-instance lock for it to take, then sends it the links still
    /// waiting for the chooser (and asks for the main window, if this launch was to show it). False when it couldn't
    /// be started or didn't take over in time; nothing has been handed over then, and this process carries on.
    /// </summary>
    private static bool HandOverToTrayHost(InstanceClaim claim, IReadOnlyList<IncomingLink> pending, bool showMain)
    {
        if (AppLaunchCommand.Companion("NudgeJudge.Tray", viaAppImage: "--tray-host") is not { Count: > 0 } command)
        {
            Trace.TraceWarning("NudgeJudge: the tray host isn't installed next to the app; not staying in the tray.");
            return false;
        }
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var arg in command.Skip(1)) start.ArgumentList.Add(arg);
        try
        {
            using var started = Process.Start(start);
            if (started is null) return false;
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow((uint)started.Id);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"NudgeJudge: couldn't start the tray host: {e.Message}");
            return false;
        }

        claim.Dispose();
        foreach (var link in pending)
            if (!SendToTrayHost(InstanceCommand.Choose, [link.Url.AbsoluteUri], link.Source)) return false;
        return !showMain || SendToTrayHost(InstanceCommand.Show, [], null);
    }

    /// <summary>Delivers to the tray host, waiting while it starts up.</summary>
    private static bool SendToTrayHost(InstanceCommand command, IReadOnlyList<string> urls, NudgeJudge.Core.Models.SourceApp? source)
    {
        var endpoint = InstanceEndpoint.ForCurrentUser();
        var deadline = DateTime.UtcNow + SingleInstance.DefaultBudget;
        while (true)
        {
            var (result, error) = InstanceClient.SendAsync(endpoint, command, urls, source).GetAwaiter().GetResult();
            if (result != DeliveryResult.Unreachable) return true;
            if (DateTime.UtcNow >= deadline)
            {
                Trace.TraceWarning($"NudgeJudge: the tray host didn't answer: {error}");
                return false;
            }
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// <c>--tray-host</c> (Linux AppImage): replaces this process with the tray host from the same mount. The
    /// AppImage keeps its files mounted for as long as the process it started runs, which this way is the host.
    /// </summary>
    private static int BecomeTrayHost()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "NudgeJudge.Tray");
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return 1;
        var argv = new[] { Marshal.StringToCoTaskMemUTF8(path), 0 };
        execv(argv[0], argv);
        // Only returns on failure.
        Trace.TraceWarning($"NudgeJudge: couldn't start the tray host: error {Marshal.GetLastPInvokeError()}");
        return 1;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int execv(nint path, nint[] argv);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>
    /// A <c>--ui</c> process, started by the resident host: the full app, reached over its own channel, without a
    /// tray icon of its own. Exits once its windows close.
    /// </summary>
    private static int RunUiProcess(string[] args)
    {
        // Another UI process may still be closing; wait for it rather than run two.
        using var claim = SingleInstance.ClaimExclusive(InstanceEndpoint.ForUi());
        if (claim is null)
        {
            // One is running and staying: give it our order rather than drop it.
            if (InstanceProtocol.DecodeOrder(args) is { } order)
                InstanceClient.SendAsync(InstanceEndpoint.ForUi(), order.Command, order.Urls, order.Source, page: order.Page)
                    .GetAwaiter().GetResult();
            return 0;
        }
        return BuildAvaloniaApp(claim, null, []).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Uninstalling deletes the app, so the startup entry and the browser registration would point at nothing.
    /// Each step is best effort: the uninstall goes ahead regardless.
    /// </summary>
    private static void CleanUpBeforeUninstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var startup = LaunchAtStartup.ForCurrentPlatform();
            if (startup.IsSupported && startup.IsEnabled()) startup.SetEnabled(false);
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"NudgeJudge: couldn't remove the startup entry on uninstall: {e.Message}");
        }

        try
        {
            new WindowsDefaultBrowserRegistrar(AppLaunchCommand.Current()).Unregister();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"NudgeJudge: couldn't remove the browser registration on uninstall: {e.Message}");
        }
    }

    private static InstanceClaim? ClaimInstance(LaunchArguments launch)
    {
        try
        {
            var claim = SingleInstance.Claim(InstanceEndpoint.ForCurrentUser(), launch);
            if (claim.Note is { } note) Trace.TraceWarning($"NudgeJudge: {note}");
            return claim;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never lose a link over this: run on our own.
            Trace.TraceWarning($"NudgeJudge: running without single-instance mode: {e.Message}");
            return null;
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null, null, []);

    private static AppBuilder BuildAvaloniaApp(InstanceClaim? claim, CoreServices? core, IReadOnlyList<IncomingLink> pending)
        => AppBuilder.Configure(() => new App { Instance = claim, Core = core, PendingLinks = pending })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
