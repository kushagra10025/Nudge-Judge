using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using NudgeJudge.Core.Ipc;

namespace NudgeJudge.Desktop;

internal sealed class Program
{
    // Don't use Avalonia or any SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        // Decided before Avalonia starts, so a launch that hands its links to the running instance exits at once.
        using var claim = ClaimInstance(LaunchArguments.Parse(args));
        if (claim?.Forwarded == true) return 0;
        return BuildAvaloniaApp(claim).StartWithClassicDesktopLifetime(args);
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
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(InstanceClaim? claim)
        => AppBuilder.Configure(() => new App { Instance = claim })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
