using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NudgeJudge.Core;
using NudgeJudge.Core.Ipc;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Security;

namespace NudgeJudge.Tray;

/// <summary>
/// What runs while NudgeJudge sits in the tray: the single-instance server, the router and a native tray icon,
/// and nothing from the UI toolkit, so it stays a few MB. Windows (the main window, the chooser) live in a separate
/// <c>--ui</c> process this host starts on demand, which exits and returns its memory when they close.
/// Everything reaches it over the single-instance channel, including what the launch that started it brought.
/// </summary>
internal sealed class ResidentHost(InstanceClaim claim, CoreServices core)
{
    /// <summary>How long to keep trying to reach a UI process that's starting.</summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(12);

    /// <summary>Quiet time after the last activity before memory is handed back to the OS.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    private readonly InstanceEndpoint _ui = InstanceEndpoint.ForUi();
    private readonly SemaphoreSlim _deliveries = new(1, 1);
    private readonly Lock _routing = new();
    private readonly CancellationTokenSource _quit = new();
    private readonly Lock _gate = new();
    private Process? _uiProcess;
    private ITrayIcon? _tray;
    private Timer? _settle;

    public int Run()
    {
        claim.StartServer(HandleAsync);

        var menu = new TrayMenu(
            Open: () => _ = ShowAsync(null),
            OpenRules: () => _ = ShowAsync("rules"),
            OpenActivity: () => _ = ShowAsync("activity"),
            ToggleRouting: () => _ = ToggleRoutingAsync(),
            Quit: () => _ = QuitAsync(),
            RoutingEnabled: () =>
            {
                // The UI process may have changed it since; the menu is about to show it.
                core.Config.Refresh();
                return core.Config.Current.Settings.RoutingEnabled;
            });
        _tray = TrayIcon.Create(menu);
        if (_tray is null) Trace.TraceWarning("NudgeJudge: no tray available; running in the background without an icon.");

        Settle();
        if (_tray is not null) _tray.Run(_quit.Token);
        else _quit.Token.WaitHandle.WaitOne();

        claim.StopServer();
        _tray?.Dispose();
        return 0;
    }

    /// <summary>How to start the UI program with <paramref name="extraArgs"/>. Null when it can't be found.</summary>
    private static ProcessStartInfo? StartInfo(params string[] extraArgs)
    {
        if (AppLaunchCommand.Companion("NudgeJudge.Desktop") is not { Count: > 0 } command) return null;
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var a in command.Skip(1).Concat(extraArgs)) start.ArgumentList.Add(a);
        return start;
    }

    private async Task<InstanceReply> HandleAsync(InstanceRequest request)
    {
        // Every await below is short; handing to a UI process that has to start first happens in the background.
        try
        {
            switch (request.Command)
            {
                case InstanceCommand.Open:
                    await OpenAsync(request.Urls, request.Source);
                    return InstanceReply.Success;
                case InstanceCommand.Show:
                    // Answered straight away: the launch that asked shouldn't wait for the window process to start.
                    _ = ShowAsync(request.Page);
                    return InstanceReply.Success;
                case InstanceCommand.Choose:
                    // From the launch that started us: links it routed itself before handing over.
                    var source = SourceApp.Sanitize(request.Source);
                    foreach (var url in request.Urls)
                        if (UrlValidator.Validate(url) is { IsValid: true, Url: { } valid })
                            _ = ChooseAsync(new IncomingLink(valid, source));
                    return InstanceReply.Success;
                case InstanceCommand.Quit:
                    // From a UI process that's about to install an update: it's closing itself.
                    await QuitAsync(closeUi: false);
                    return InstanceReply.Success;
                default:
                    return new InstanceReply(false, $"{request.Command} isn't handled here.");
            }
        }
        finally
        {
            Settle();
        }
    }

    private async Task OpenAsync(IReadOnlyList<string> urls, SourceApp? source)
    {
        // While the UI is open it routes, so its Activity page and chooser stay live, exactly as before.
        if (UiRunning && await DeliverAsync(InstanceCommand.Open, urls, source, startUi: false)) return;

        // Start the chooser the moment a link needs it: the router asks before it writes the activity log.
        void Ask(object? sender, IncomingLink link) => _ = ChooseAsync(link);
        lock (_routing)
        {
            // Rules may have been edited in the UI process since the last link.
            core.Config.Refresh();
            core.Router.ChoiceRequested += Ask;
            try
            {
                foreach (var url in urls) core.Router.Route(url, source);
            }
            finally
            {
                core.Router.ChoiceRequested -= Ask;
            }
        }
    }

    private Task<bool> ChooseAsync(IncomingLink link) =>
        DeliverAsync(InstanceCommand.Choose, [link.Url.AbsoluteUri], link.Source);

    private Task<bool> ShowAsync(string? page) => DeliverAsync(InstanceCommand.Show, [], null, page: page);

    private async Task ToggleRoutingAsync()
    {
        // The UI's Settings page shows this switch, so let it flip it when it's open; otherwise flip it here.
        if (!(UiRunning && await DeliverAsync(InstanceCommand.ToggleRouting, [], null, startUi: false)))
            core.Config.Update(c => c.Settings.RoutingEnabled = !c.Settings.RoutingEnabled);
        core.Config.Refresh();
        _tray?.Invalidate();
        Settle();
    }

    private async Task QuitAsync(bool closeUi = true)
    {
        if (closeUi && UiRunning) await DeliverAsync(InstanceCommand.Quit, [], null, startUi: false);
        _quit.Cancel();
    }

    private bool UiRunning
    {
        get
        {
            lock (_gate) return _uiProcess is { HasExited: false };
        }
    }

    /// <summary>
    /// Hands a request to the UI process, starting one if needed (and allowed). One at a time, so two links
    /// arriving together don't start two UI processes. True when the UI process took it (or was started with it).
    /// </summary>
    private async Task<bool> DeliverAsync(InstanceCommand command, IReadOnlyList<string> urls, SourceApp? source,
        bool startUi = true, string? page = null)
    {
        await _deliveries.WaitAsync();
        try
        {
            // A new window process takes its first order on its command line, so it can show the chooser (or the
            // main window) without first waiting for its own server, and without us polling for it.
            if (!UiRunning) return startUi && StartUi(InstanceProtocol.EncodeOrder(command, urls, source, page));

            var deadline = DateTime.UtcNow + DeliveryBudget;
            string? lastError = null;
            while (!_quit.IsCancellationRequested)
            {
                var (result, error) = await InstanceClient.SendAsync(_ui, command, urls, source, page: page);
                // Refused means it saw the request; sending it again could open a link twice.
                if (result != DeliveryResult.Unreachable) return true;
                lastError = error;

                // Unreachable while running: it's just starting (its server isn't up yet) or closing.
                if (!startUi) return false;
                if (!UiRunning) return StartUi(InstanceProtocol.EncodeOrder(command, urls, source, page));
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(20);
            }
            Trace.TraceWarning($"NudgeJudge: the window process didn't answer: {lastError}");
            return false;
        }
        finally
        {
            _deliveries.Release();
            Settle();
        }
    }

    private bool StartUi(string order)
    {
        if (StartInfo("--ui", order) is not { } start) return false;

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"NudgeJudge: couldn't start the window process: {e.Message}");
            return false;
        }
        if (process is null) return false;

        // Pass on the right to take focus, which the launch that brought us this link gave us.
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow((uint)process.Id);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnUiExited(process);
        lock (_gate)
        {
            _uiProcess?.Dispose();
            _uiProcess = process;
        }
        return true;
    }

    private void OnUiExited(Process process)
    {
        lock (_gate)
        {
            if (_uiProcess != process) return;
            _uiProcess = null;
        }
        process.Dispose();

        // It may have changed settings, including whether to stay in the tray at all.
        core.Config.Refresh();
        _tray?.Invalidate();
        if (!core.Config.Current.Settings.RunInBackground) _ = QuitAsync(closeUi: false);
        Settle();
    }

    /// <summary>
    /// Once things are quiet, gives back what routing a link or starting the UI used: a full compacting GC, then
    /// (Windows) trimming the working set so the idle tray icon shows its real, small footprint.
    /// </summary>
    private void Settle()
    {
        lock (_gate)
        {
            _settle ??= new Timer(_ =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                if (OperatingSystem.IsWindows()) SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            });
            _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}

/// <summary>The tray menu's entries, as actions on the host. Called on the tray's own thread.</summary>
internal sealed record TrayMenu(
    Action Open,
    Action OpenRules,
    Action OpenActivity,
    Action ToggleRouting,
    Action Quit,
    Func<bool> RoutingEnabled);

internal interface ITrayIcon : IDisposable
{
    /// <summary>Shows the icon and blocks until <paramref name="stop"/> is cancelled.</summary>
    void Run(CancellationToken stop);

    /// <summary>The menu's state (the routing check mark) may have changed.</summary>
    void Invalidate();
}

internal static class TrayIcon
{
    public static ITrayIcon? Create(TrayMenu menu)
    {
        if (OperatingSystem.IsWindows()) return new WindowsTray(menu);
        if (OperatingSystem.IsLinux()) return LinuxTray.TryCreate(menu);
        return null;
    }
}
