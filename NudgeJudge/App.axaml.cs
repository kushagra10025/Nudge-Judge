using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using NudgeJudge.Core;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Ipc;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;
using NudgeJudge.Services;
using NudgeJudge.ViewModels;

namespace NudgeJudge;

public partial class App : Application
{
    /// <summary>
    /// This process's single-instance claim, set by the desktop entry point. Null in the designer and in
    /// tests, which then run without a server.
    /// </summary>
    public InstanceClaim? Instance { get; init; }

    /// <summary>The app that opened the links on this launch's command line, when the entry point could tell.</summary>
    public NudgeJudge.Core.Models.SourceApp? LaunchSource { get; init; }

    /// <summary>
    /// Config, rules and router, when the entry point already built them to route this launch's links before
    /// the UI started. Null means the links (if any) haven't been routed yet, and the app builds its own.
    /// </summary>
    public CoreServices? Core { get; init; }

    /// <summary>With <see cref="Core"/>: this launch's links that no rule took, waiting for the chooser.</summary>
    public IReadOnlyList<IncomingLink> PendingLinks { get; init; } = [];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        // Once per process: headless UI tests create a fresh App for every test.
        if (!_devToolsAttached)
        {
            this.AttachDeveloperTools();
            _devToolsAttached = true;
        }
#endif
    }

#if DEBUG
    private static bool _devToolsAttached;
#endif

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Compose(desktop);
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Composition root: builds core services and view models, then decides what to show.</summary>
    private void Compose(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var dispatcher = new AvaloniaUiDispatcher();
        var core = Core ?? CoreServices.Load();
        var (boot, config, engine, activity, router) = (core.Boot, core.Config, core.Engine, core.Activity, core.Router);
        var windows = new DesktopWindowService(desktop);

        var chooser = new ChooserViewModel(config, router, boot, dispatcher);
        chooser.Attach(windows);
        var registrar = DefaultBrowserRegistration.ForCurrentPlatform(ReadAppIcon);
        var startup = LaunchAtStartup.ForCurrentPlatform();
        RefreshStartupEntry(startup);
        var updates = new UpdatesViewModel(config, new VelopackUpdateService(), dispatcher);
        var settings = new SettingsViewModel(config, registrar, new AvaloniaThemeService(), dispatcher, startup, updates, windows);
        var main = new MainWindowViewModel(
            new RulesViewModel(config, engine, boot, chooser, windows, dispatcher, settings),
            new BrowsersViewModel(config, BrowserDiscovery.ForCurrentPlatform(), router, windows, dispatcher),
            new ActivityViewModel(activity, dispatcher, settings, windows),
            settings,
            config,
            dispatcher);
        // Settings typed in the last moment before quitting are still waiting out their debounce.
        desktop.Exit += (_, _) => settings.FlushPendingSaves();

        windows.Main = main;
        windows.Chooser = chooser;
        windows.KeepRunningInBackground = () => config.Current.Settings.RunInBackground;
        DataContext = new AppViewModel(settings, windows);

        router.ChoiceRequested += (_, link) => dispatcher.Post(() =>
        {
            chooser.Enqueue(link.Url, link.Source);
            windows.ShowChooser();
        });

        var launch = LaunchArguments.Parse(desktop.Args ?? []);
        var serving = Instance?.StartServer(request =>
            Dispatcher.UIThread.InvokeAsync(() => HandleForwarded(request, router, windows)).GetTask()) == true;

        // Stay in the tray when the user wants that. A launch that only came to open links stays only if it's
        // the server (so the next links are handled instantly) or the user opened the main window meanwhile.
        bool StayResident() => config.Current.Settings.RunInBackground
                               && (serving || launch.Urls.Count == 0 || windows.MainWindowShown);
        void ExitIfIdle()
        {
            if (StayResident() || windows.AnyWindowOpen) return;
            // Later launches now find no server and take over, rather than queueing behind a closing process.
            Instance?.StopServer();
            desktop.Shutdown();
        }
        windows.AllWindowsClosed += (_, _) => ExitIfIdle();

        // Installing an update closes this process: save what's pending and let the next launch take over the server.
        updates.PreparingToRestart = () =>
        {
            settings.FlushPendingSaves();
            Instance?.StopServer();
        };
        // Only a process that sticks around checks; one that just routes a link and exits never touches the network.
        if (launch.Urls.Count == 0 || serving)
        {
            var schedule = new CancellationTokenSource();
            desktop.Exit += (_, _) => schedule.Cancel();
            _ = updates.RunScheduleAsync(schedule.Token);
        }

        if (launch.Urls.Count == 0)
        {
            if (!launch.Background) windows.ShowMainWindow();
            return;
        }

        // Launched with links (as the default browser is). Links forwarded by later launches join the same chooser.
        if (Core is null)
        {
            foreach (var url in launch.Urls)
                router.Route(url, LaunchSource);
        }
        else
        {
            // Routed by the entry point before the UI started; only the ones no rule took are left.
            foreach (var link in PendingLinks)
                dispatcher.Post(() =>
                {
                    chooser.Enqueue(link.Url, link.Source);
                    windows.ShowChooser();
                });
        }

        // The dispatcher loop hasn't started yet; shutting down synchronously here throws.
        Dispatcher.UIThread.Post(ExitIfIdle);
    }

    /// <summary>
    /// The primary instance's side of the single-instance channel. The request is already authenticated;
    /// each URL is still validated by <see cref="LinkRouter.Route"/>, as any other incoming link is.
    /// </summary>
    private static InstanceReply HandleForwarded(InstanceRequest request, LinkRouter router, DesktopWindowService windows)
    {
        switch (request.Command)
        {
            case InstanceCommand.Show:
                windows.ShowMainWindow();
                break;
            case InstanceCommand.Open:
                // The source is the sender's say-so; Route sanitizes it like any other input.
                foreach (var url in request.Urls)
                    router.Route(url, request.Source);
                break;
        }
        return InstanceReply.Success;
    }

    /// <summary>Keeps an existing startup entry pointing at this install, in case the app moved since it was added.</summary>
    private static void RefreshStartupEntry(ILaunchAtStartup startup)
    {
        try
        {
            startup.Refresh();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning($"NudgeJudge: couldn't update the startup entry: {e.Message}");
        }
    }

    /// <summary>The app icon as PNG bytes, for the Linux launcher entry written when registering.</summary>
    private static byte[]? ReadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://NudgeJudge/Assets/nudgejudge.png"));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            return null;
        }
    }
}
