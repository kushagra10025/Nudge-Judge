using System;
using System.Diagnostics;
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

        var launch = LaunchArguments.Parse(args);
        // First thing, while the app that opened the link is still our parent (helpers like xdg-open exit quickly).
        if (launch.Urls.Count > 0) launch = launch with { Source = SourceAppDetector.ForCurrentPlatform().Detect() };

        // Decided before Avalonia starts, so a launch that hands its links to the running instance exits at once.
        using var claim = ClaimInstance(launch);
        if (claim?.Forwarded == true) return 0;

        // Open the links before the UI exists: starting Avalonia costs far more than routing does.
        CoreServices? core = null;
        IReadOnlyList<IncomingLink> pending = [];
        if (launch.Urls.Count > 0)
        {
            core = CoreServices.Load();
            pending = core.Route(launch.Urls, launch.Source);
            // Every link opened and nothing to stay in the tray for (only the server does): done, no UI needed.
            var resident = claim?.IsPrimary == true && core.Config.Current.Settings.RunInBackground;
            if (pending.Count == 0 && !resident) return 0;
        }

        return BuildAvaloniaApp(claim, launch.Source, core, pending).StartWithClassicDesktopLifetime(args);
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
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null, null, null, []);

    private static AppBuilder BuildAvaloniaApp(InstanceClaim? claim, NudgeJudge.Core.Models.SourceApp? source, CoreServices? core,
        IReadOnlyList<IncomingLink> pending)
        => AppBuilder.Configure(() => new App { Instance = claim, LaunchSource = source, Core = core, PendingLinks = pending })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
