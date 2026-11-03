using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;
using NudgeJudge.Services;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.UI.Tests;

/// <summary>
/// The persistent activity log, driven through the real main window: the rolling file, the Settings switch
/// and limit, and "Create rule" on an entry.
/// </summary>
public sealed class ActivityLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nudgejudge-activity-").FullName;
    private readonly List<Window> _windows = [];

    private string LogPath => Path.Combine(_dir, "activity.log");

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Directory.Delete(_dir, recursive: true);
    }

    // ---- Settings ----

    [AvaloniaFact]
    public void Defaults_are_logging_on_and_100_entries()
    {
        var app = new Harness(LogPath);
        var window = Show(app, AppPage.Settings);

        Assert.True(new AppSettings().ActivityLogEnabled);
        Assert.Equal(100, new AppSettings().ActivityLogLimit);
        Assert.Equal(100m, Find<NumericUpDown>(window, "Activity entries to keep").Value);
        Assert.True(Find<ToggleSwitch>(window, "Keep an activity log").IsChecked);
        Assert.Equal(100, app.Log.Capacity);
    }

    [AvaloniaFact]
    public void Configs_written_before_the_setting_existed_get_the_defaults()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{ "version": 1, "settings": { "routingEnabled": true } }""");

        var store = new JsonConfigStore(path);

        Assert.True(store.Current.Settings.ActivityLogEnabled);
        Assert.Equal(100, store.Current.Settings.ActivityLogLimit);
    }

    [AvaloniaFact]
    public void Settings_are_saved_to_the_config_file()
    {
        var path = Path.Combine(_dir, "config.json");
        var settings = new SettingsViewModel(new JsonConfigStore(path), new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), new FakeStartup())
        {
            ActivityLogEnabled = false,
            ActivityLogLimit = 250,
        };
        settings.FlushPendingSaves();

        var reloaded = new JsonConfigStore(path).Current.Settings;
        Assert.False(reloaded.ActivityLogEnabled);
        Assert.Equal(250, reloaded.ActivityLogLimit);
        Assert.False(settings.ActivityLogEnabled);
    }

    // ---- Persistence and rolling ----

    [AvaloniaFact]
    public void Links_are_written_to_the_log_file_and_survive_a_restart()
    {
        var first = new Harness(LogPath);
        first.Router.Route("https://jira.example.com/browse/NJ-1");

        var lines = File.ReadAllLines(LogPath);
        Assert.Single(lines);
        Assert.Contains("jira.example.com/browse/NJ-1", lines[0]);
        Assert.Contains("\"routed\"", lines[0], StringComparison.OrdinalIgnoreCase);

        // A new process reads the same file.
        var second = new Harness(LogPath);
        var window = Show(second, AppPage.Activity);

        Assert.True(Shows(window, "jira.example.com"));
        Assert.Equal("1 link · keeps the latest 100", second.Main.Activity.CountText);
        var entry = Assert.Single(second.Log.Snapshot());
        Assert.Equal(ActivityOutcome.Routed, entry.Outcome);
        Assert.Equal(first.Firefox.Id, entry.BrowserId);
        Assert.Equal(1, entry.RuleNumber);
    }

    [AvaloniaFact]
    public void The_log_rolls_over_at_the_limit()
    {
        var app = new Harness(LogPath, s => s.ActivityLogLimit = 3);
        var window = Show(app, AppPage.Activity);

        foreach (var host in new[] { "a", "b", "c", "d", "e" })
            app.Router.Route($"https://{host}.example.com/");
        Settle(window);

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.Contains("c.example.com", lines[0]);
        Assert.Contains("e.example.com", lines[2]);

        Assert.Equal(["e.example.com", "d.example.com", "c.example.com"], app.Main.Activity.Entries.Select(e => e.Host));
        Assert.True(Shows(window, "c.example.com"));
        Assert.False(Shows(window, "b.example.com"));
        Assert.False(Shows(window, "a.example.com"));
    }

    [AvaloniaFact]
    public void Lowering_the_limit_in_settings_deletes_the_oldest_entries_once_it_settles()
    {
        var app = new Harness(LogPath);
        foreach (var host in new[] { "a", "b", "c", "d", "e" })
            app.Router.Route($"https://{host}.example.com/");
        var window = Show(app, AppPage.Settings);

        Find<NumericUpDown>(window, "Activity entries to keep").Value = 2;
        Settle(window);
        // Still inside the debounce: nothing is trimmed yet.
        Assert.Equal(5, File.ReadAllLines(LogPath).Length);
        app.Settings.FlushPendingSaves();
        Settle(window);

        Assert.Equal(2, app.Config.Current.Settings.ActivityLogLimit);
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
        Assert.Equal(["e.example.com", "d.example.com"], app.Main.Activity.Entries.Select(e => e.Host));
    }

    [AvaloniaFact]
    public void Spinning_the_limit_down_and_back_up_keeps_every_entry()
    {
        var app = new Harness(LogPath);
        foreach (var host in new[] { "a", "b", "c", "d", "e" })
            app.Router.Route($"https://{host}.example.com/");
        var window = Show(app, AppPage.Settings);

        var limit = Find<NumericUpDown>(window, "Activity entries to keep");
        foreach (var value in new[] { 4, 3, 2, 1, 2, 3, 100 })
            limit.Value = value;
        app.Settings.FlushPendingSaves();
        Settle(window);

        Assert.Equal(100, app.Config.Current.Settings.ActivityLogLimit);
        Assert.Equal(5, File.ReadAllLines(LogPath).Length);
    }

    [AvaloniaFact]
    public void A_debounced_setting_saves_by_itself_after_the_delay()
    {
        var path = Path.Combine(_dir, "config.json");
        var settings = new SettingsViewModel(new JsonConfigStore(path), new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), new FakeStartup())
        {
            TemporaryRuleHours = 24,
        };
        Assert.Equal(8, new JsonConfigStore(path).Current.Settings.TemporaryRuleHours);

        var deadline = DateTime.UtcNow + SettingsViewModel.SaveDelay + TimeSpan.FromSeconds(5);
        while (new JsonConfigStore(path).Current.Settings.TemporaryRuleHours != 24 && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Equal(24, new JsonConfigStore(path).Current.Settings.TemporaryRuleHours);
        Assert.Equal(24, settings.TemporaryRuleHours);
    }

    [AvaloniaFact]
    public void Lines_that_cannot_be_read_are_skipped()
    {
        new Harness(LogPath).Router.Route("https://ok.example.com/");
        File.AppendAllText(LogPath, "{ not json\n\n{\"timestamp\":\"nonsense\"}\n");

        var app = new Harness(LogPath);

        Assert.Equal(["ok.example.com"], app.Main.Activity.Entries.Select(e => e.Host));
    }

    [AvaloniaFact]
    public void Links_logged_by_another_process_show_up_when_the_window_comes_back()
    {
        var running = new Harness(LogPath);
        var window = Show(running, AppPage.Activity);
        running.Router.Route("https://one.example.com/");

        // Each link launch is its own process today; one writes while the main window is in the background.
        var launch = new Harness(LogPath);
        launch.Router.Route("https://two.example.com/");
        Assert.Equal(["one.example.com"], running.Main.Activity.Entries.Select(e => e.Host));

        running.Main.OnActivated();
        Settle(window);
        Assert.Equal(["two.example.com", "one.example.com"], running.Main.Activity.Entries.Select(e => e.Host));

        // And the running app's next write keeps the other process's entry.
        running.Router.Route("https://three.example.com/");
        Assert.Equal(3, File.ReadAllLines(LogPath).Length);
    }

    [AvaloniaFact]
    public void Clear_empties_the_file_and_undo_restores_it()
    {
        var app = new Harness(LogPath);
        app.Router.Route("https://one.example.com/");
        app.Router.Route("https://two.example.com/");
        var window = Show(app, AppPage.Activity);

        Click(window, ButtonFor(window, app.Main.Activity.ClearCommand));
        Settle(window);

        Assert.Empty(File.ReadAllLines(LogPath));
        Assert.True(Shows(window, "Nothing routed yet"));

        Click(window, ButtonFor(window, app.Main.Activity.Undo.UndoCommand));
        Settle(window);

        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
        Assert.Equal(["two.example.com", "one.example.com"], app.Main.Activity.Entries.Select(e => e.Host));
    }

    // ---- Turning it off ----

    [AvaloniaFact]
    public void Turning_logging_off_stops_recording_but_links_still_open()
    {
        var app = new Harness(LogPath);
        app.Router.Route("https://kept.example.com/");
        var window = Show(app, AppPage.Settings);

        Click(window, Find<ToggleSwitch>(window, "Keep an activity log"));
        Settle(window);
        Assert.False(app.Config.Current.Settings.ActivityLogEnabled);

        app.Router.Route("https://unrecorded.example.com/");

        Assert.Equal(2, app.Launcher.Launched.Count);
        Assert.Single(File.ReadAllLines(LogPath));
        Assert.Equal(["kept.example.com"], app.Main.Activity.Entries.Select(e => e.Host));

        // The Activity page says why nothing new appears, and offers to turn it back on.
        app.Main.Navigate(AppPage.Activity);
        Settle(window);
        Assert.True(Shows(window, "Activity logging is off, so new links aren't recorded."));
        Assert.Equal("1 link · logging is off", app.Main.Activity.CountText);

        Click(window, ButtonFor(window, app.Main.Activity.TurnOnLoggingCommand));
        Settle(window);
        Assert.True(app.Config.Current.Settings.ActivityLogEnabled);
        Assert.False(Shows(window, "Activity logging is off, so new links aren't recorded."));

        app.Router.Route("https://recorded.example.com/");
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [AvaloniaFact]
    public void Logging_off_with_nothing_logged_explains_itself()
    {
        var app = new Harness(LogPath, s => s.ActivityLogEnabled = false);
        var window = Show(app, AppPage.Activity);

        app.Router.Route("https://unrecorded.example.com/");
        Settle(window);

        Assert.True(Shows(window, "Activity logging is off"));
        Assert.False(Shows(window, "Nothing routed yet"));
        Assert.False(File.Exists(LogPath));
    }

    // ---- Create rule from an entry ----

    [AvaloniaFact]
    public void Create_rule_opens_the_editor_with_the_link_and_the_browser_it_went_to()
    {
        var app = new Harness(LogPath, withRule: false);
        var url = new Uri("https://docs.acme.io/guide/start");
        app.Router.Route(url.AbsoluteUri); // no rule: asked
        app.Router.OpenChosen(url, app.Chrome); // the user picked Chrome
        var window = Show(app, AppPage.Activity);

        var row = app.Main.Activity.Entries[0];
        Assert.Equal(ActivityOutcome.Chosen, row.Outcome);
        Click(window, CreateRuleButtons(window)[0]);
        Settle(window);

        var rules = app.Main.Rules;
        Assert.Equal((int)AppPage.Rules, app.Main.SelectedPageIndex);
        Assert.True(rules.Editor.IsOpen);
        Assert.True(rules.Editor.IsNew);
        Assert.Equal("*.acme.io", rules.Editor.Conditions.OfType<ConditionEditorViewModel>().Single().FirstValue);
        Assert.Equal("acme.io", rules.Editor.Name); // a suggested name, so the required field never starts empty
        Assert.Equal(app.Chrome.Id, rules.Editor.Browser?.Id);
        Assert.Equal(url.AbsoluteUri, rules.TestUrl);
        Assert.Equal("Matches the test link", rules.Editor.TestVerdict);
        Assert.True(Shows(window, "New rule"));

        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save rule"));
        Settle(window);

        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.Equal("*.acme.io", Assert.Single(rule.Conditions).Values.Single());
        Assert.Equal("acme.io", rule.Name);
        Assert.Equal(app.Chrome.Id, rule.BrowserId);

        // The next link like it routes by itself.
        Assert.Equal(RouteKind.Launched, app.Router.Route("https://wiki.acme.io/").Kind);
        Assert.Equal(app.Chrome, app.Launcher.Launched[^1].Browser);
    }

    [AvaloniaFact]
    public void Create_rule_matches_the_browser_by_name_for_entries_without_an_id()
    {
        var app = new Harness(LogPath, withRule: false);
        app.Log.Add(new ActivityEntry(DateTimeOffset.UtcNow, "https://news.example.org/a", ActivityOutcome.Chosen, BrowserName: "Chrome"));
        var window = Show(app, AppPage.Activity);

        Click(window, CreateRuleButtons(window)[0]);
        Settle(window);

        Assert.Equal(app.Chrome.Id, app.Main.Rules.Editor.Browser?.Id);
        Assert.Equal("*.example.org", app.Main.Rules.Editor.Conditions.OfType<ConditionEditorViewModel>().Single().FirstValue);
    }

    [AvaloniaFact]
    public void Rejected_links_offer_no_rule()
    {
        var app = new Harness(LogPath);
        app.Router.Route("javascript:alert(1)");
        app.Router.Route("https://fine.example.com/");
        var window = Show(app, AppPage.Activity);

        var items = app.Main.Activity.Entries;
        Assert.Equal(ActivityOutcome.Rejected, items[1].Outcome);
        Assert.False(items[1].CanCreateRule);
        Assert.True(items[0].CanCreateRule);
        Assert.Single(CreateRuleButtons(window));
    }

    // ---- Helpers ----

    /// <summary>Everything the main window needs, wired as in App.Compose, with the log file at <paramref name="logPath"/>.</summary>
    private sealed class Harness
    {
        public Harness(string logPath, Action<AppSettings>? settings = null, bool withRule = true)
        {
            Config.Current.Browsers.AddRange([Firefox, Chrome]);
            if (withRule)
                Config.Current.Rules.Add(new Rule
                {
                    Name = "Example sites",
                    Conditions = [RuleCondition.Link(MatchKind.Wildcard, "*.example.com")],
                    BrowserId = Firefox.Id,
                });
            settings?.Invoke(Config.Current.Settings);

            var dispatcher = new InlineDispatcher();
            var boot = new FakeBoot();
            var engine = new RuleEngine(boot);
            Log = ActivityLog.FollowingSettings(Config, new ActivityLogFile(logPath));
            Router = new LinkRouter(Config, engine, Launcher, Log);
            var windows = new FakeWindows();
            var chooser = new ChooserViewModel(Config, Router, boot, dispatcher);
            Settings = new SettingsViewModel(Config, new FakeRegistrar(), new NoTheme(), dispatcher, new FakeStartup());
            Main = new MainWindowViewModel(
                new RulesViewModel(Config, engine, boot, chooser, windows, dispatcher, Settings),
                new BrowsersViewModel(Config, new NoDiscovery(), Router, windows, dispatcher),
                new ActivityViewModel(Log, dispatcher, Settings, windows),
                Settings,
                Config);
            windows.Main = Main;
        }

        public MemoryConfigStore Config { get; } = new();
        public BrowserProfile Firefox { get; } = new() { Name = "Firefox", ExecutablePath = "/usr/bin/firefox" };
        public BrowserProfile Chrome { get; } = new() { Name = "Chrome", ExecutablePath = "/usr/bin/chrome" };
        public FakeLauncher Launcher { get; } = new();
        public ActivityLog Log { get; }
        public LinkRouter Router { get; }
        public SettingsViewModel Settings { get; }
        public MainWindowViewModel Main { get; }
    }

    private Window Show(Harness app, AppPage page)
    {
        app.Main.Navigate(page);
        var window = new MainWindow { DataContext = app.Main, Width = 1280, Height = 900 };
        _windows.Add(window);
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Shows(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static T Find<T>(Window window, string automationName) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == automationName);

    private static Button ButtonFor(Window window, System.Windows.Input.ICommand command) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);

    private static List<Button> CreateRuleButtons(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) == "Create rule from this link" && b.IsEffectivelyVisible)
            .ToList();

    /// <summary>Hovers first, as a user would: row actions only appear on hover.</summary>
    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }
}
