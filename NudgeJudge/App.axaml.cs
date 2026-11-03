using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Browsers.Discovery;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Platform;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;
using NudgeJudge.Services;
using NudgeJudge.ViewModels;

namespace NudgeJudge;

public partial class App : Application
{
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
        var boot = new SystemBoot();
        var config = new JsonConfigStore(AppPaths.ConfigFile, SampleData.FirstRun);
        var engine = new RuleEngine(boot);
        var activity = new ActivityLog();
        var router = new LinkRouter(config, engine, new BrowserLauncher(), activity);
        var windows = new DesktopWindowService(desktop);

        var chooser = new ChooserViewModel(config, router, boot, dispatcher);
        chooser.Attach(windows);
        var registrar = DefaultBrowserRegistration.ForCurrentPlatform(ReadAppIcon);
        var settings = new SettingsViewModel(config, registrar, new AvaloniaThemeService(), dispatcher);
        var main = new MainWindowViewModel(
            new RulesViewModel(config, engine, boot, chooser, windows, dispatcher, settings),
            new BrowsersViewModel(config, BrowserDiscovery.ForCurrentPlatform(), router, windows, dispatcher),
            new ActivityViewModel(activity, dispatcher, settings, windows),
            settings,
            config);

        windows.Main = main;
        windows.Chooser = chooser;
        windows.KeepRunningInBackground = () => config.Current.Settings.RunInBackground;
        DataContext = new AppViewModel(settings, windows);

        router.ChoiceRequested += (_, url) => dispatcher.Post(() =>
        {
            chooser.Enqueue(url);
            windows.ShowChooser();
        });

        var args = desktop.Args ?? [];
        var urls = args.Where(a => !a.StartsWith('-')).ToList();
        if (urls.Count == 0)
        {
            if (!args.Contains("--background")) windows.ShowMainWindow();
            return;
        }

        // Launched with links (as the default browser would be). Until the single-instance
        // server exists, this process handles them itself and exits when it's done.
        var needsChoice = false;
        foreach (var url in urls)
            needsChoice |= router.Route(url).Kind == RouteKind.NeedsChoice;

        if (!needsChoice)
        {
            // The dispatcher loop hasn't started yet; shutting down synchronously here throws.
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
            return;
        }
        windows.AllWindowsClosed += (_, _) =>
        {
            if (!windows.MainWindowShown) desktop.Shutdown();
        };
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
