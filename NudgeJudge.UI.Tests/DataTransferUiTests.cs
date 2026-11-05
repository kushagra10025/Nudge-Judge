using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.Views;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.ViewModels;

namespace NudgeJudge.UI.Tests;

public sealed class DataTransferUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nj-transfer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SettingsViewModel Settings(IConfigStore config, FakeWindows dialogs) =>
        new(config, new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), new FakeStartup(), dialogs: dialogs);

    [AvaloniaFact]
    public async Task Export_from_one_setup_and_import_into_another()
    {
        var from = new MemoryConfigStore();
        var browser = new BrowserProfile { Name = "Chrome", ExecutablePath = "/usr/bin/chrome" };
        from.Current.Browsers.Add(browser);
        from.Current.Rules.Add(new Rule { Name = "GitHub", Conditions = [RuleCondition.Link(MatchKind.Domain, "github.com")], BrowserId = browser.Id });
        var path = Path.Combine(_dir, "export.json");

        var exporting = Settings(from, new FakeWindows { SavePath = path });
        await exporting.ExportDataCommand.ExecuteAsync(null);
        Assert.False(exporting.TransferFailed);
        Assert.Equal("Exported 1 rule and 1 browser to export.json.", exporting.TransferMessage);

        var to = new MemoryConfigStore();
        var importing = Settings(to, new FakeWindows { OpenPath = path });
        importing.ProgramExists = _ => true;
        await importing.ImportDataCommand.ExecuteAsync(null);

        // Nothing changes until the review is confirmed.
        var review = Assert.IsType<ImportReviewViewModel>(importing.ImportReview);
        Assert.Empty(to.Current.Rules);
        Assert.True(review.IsAddAsNew); // someone else's file
        Assert.Equal("Chrome", Assert.Single(review.Browsers).Name);
        Assert.Equal("Chrome", Assert.Single(review.Rules).Destination);

        review.ConfirmCommand.Execute(null);
        Assert.Null(importing.ImportReview);
        Assert.False(importing.TransferFailed);
        Assert.Equal("Imported 1 rule and 1 browser.", importing.TransferMessage);
        Assert.Equal("GitHub", Assert.Single(to.Current.Rules).Name);
        var imported = Assert.Single(to.Current.Browsers);
        Assert.NotEqual(browser.Id, imported.Id);
        Assert.False(imported.NeedsReview);
    }

    [AvaloniaFact]
    public async Task A_tampered_backup_shows_the_program_change_and_needs_it_allowed()
    {
        var config = new MemoryConfigStore();
        var chrome = new BrowserProfile { Name = "Chrome", ExecutablePath = "/usr/bin/google-chrome" };
        config.Current.Browsers.Add(chrome);
        config.Current.Rules.Add(new Rule { Name = "GitHub", Conditions = [RuleCondition.Link(MatchKind.Domain, "github.com")], BrowserId = chrome.Id });
        var export = ConfigTransfer.Read(ConfigTransfer.Export(config.Current));
        export.Browsers[0].ExecutablePath = "/home/me/evil.sh";
        var path = Path.Combine(_dir, "backup.json");
        await File.WriteAllBytesAsync(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export, ExportJson.Options));

        var vm = Settings(config, new FakeWindows { OpenPath = path });
        vm.ProgramExists = _ => true;
        await vm.ImportDataCommand.ExecuteAsync(null);
        var review = vm.ImportReview!;

        Assert.True(review.IsRestore); // shares ids: looks like a backup
        Assert.True(review.HasAttention);
        var row = Assert.Single(review.Browsers);
        Assert.True(row.ChangesProgram);
        Assert.Equal("/usr/bin/google-chrome", row.CurrentProgram);
        Assert.Contains("script", row.Warnings[0]);
        Assert.Equal("Added beside yours, yours unchanged", row.ActionText);
        var rule = Assert.Single(review.Rules);
        Assert.Equal("Chrome (imported)", rule.Destination);
        Assert.True(rule.DestinationNeedsReview);
        Assert.NotNull(rule.Redirect);
        Assert.Equal("Opens somewhere else", rule.ActionText);
        // Counted, so the notice says what to look at.
        Assert.Equal("1 browser would run a different program · 1 browser needs review · 1 rule will open somewhere else", review.AttentionText);
        Assert.Same(row, Assert.Single(review.AttentionBrowsers));

        // Allowing the change updates the rows in place; nothing moves.
        row.AllowProgramChange = true;
        Assert.Same(rule, review.Rules[0]);
        Assert.Equal("Chrome", rule.Destination);
        Assert.Null(rule.Redirect);
        Assert.Equal("Unchanged", rule.ActionText);
        Assert.Equal("1 browser needs review", review.AttentionText);
        row.AllowProgramChange = false;

        review.ConfirmCommand.Execute(null);

        Assert.Equal("/usr/bin/google-chrome", config.Current.FindBrowser(chrome.Id)!.ExecutablePath);
        Assert.True(config.Current.Browsers.Single(b => b.Id != chrome.Id).NeedsReview);
        Assert.Contains("needs review", vm.TransferMessage);
    }

    [AvaloniaFact]
    public async Task A_big_import_scrolls_inside_the_review_instead_of_growing_the_page()
    {
        var from = new MemoryConfigStore();
        var firefox = new BrowserProfile { Name = "Firefox", ExecutablePath = "/usr/bin/firefox" };
        from.Current.Browsers.Add(firefox);
        for (var i = 0; i < 40; i++)
            from.Current.Browsers.Add(new BrowserProfile { Name = $"Browser {i}", ExecutablePath = $"/usr/bin/firefox{i}" });
        for (var i = 0; i < 300; i++)
            from.Current.Rules.Add(new Rule { Name = $"Rule {i}", Conditions = [RuleCondition.Link(MatchKind.Domain, $"site{i}.io")], BrowserId = firefox.Id });
        var path = Path.Combine(_dir, "big.json");
        await File.WriteAllBytesAsync(path, ConfigTransfer.Export(from.Current));

        var vm = Settings(new MemoryConfigStore(), new FakeWindows { OpenPath = path });
        vm.ProgramExists = _ => true;
        await vm.ImportDataCommand.ExecuteAsync(null);
        var window = new Window { Content = new SettingsView { DataContext = vm }, Width = 1000, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        try
        {
            // Nothing here needs a look, so it all folds away: the panel stays short.
            var review = vm.ImportReview!;
            Assert.Empty(review.AttentionRules);
            Assert.Equal("300 rules, nothing to check", review.OtherRulesLabel);
            var folded = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => AutomationProperties.GetName(s) == "Other rules");
            Assert.False(folded.IsVisible);

            // Opened, the long list scrolls inside the review instead of stretching the page.
            review.ToggleOtherRulesCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(folded.Bounds.Height <= 320, $"Other rules is {folded.Bounds.Height}px tall");
            Assert.True(folded.Extent.Height > folded.Viewport.Height, "Other rules should scroll");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_the_review_changes_nothing()
    {
        var from = new MemoryConfigStore();
        from.Current.Rules.Add(new Rule { Name = "r", Conditions = [RuleCondition.Link(MatchKind.Domain, "a.io")] });
        var path = Path.Combine(_dir, "e.json");
        await File.WriteAllBytesAsync(path, ConfigTransfer.Export(from.Current));

        var config = new MemoryConfigStore();
        var vm = Settings(config, new FakeWindows { OpenPath = path });
        await vm.ImportDataCommand.ExecuteAsync(null);
        vm.ImportReview!.CancelCommand.Execute(null);

        Assert.Null(vm.ImportReview);
        Assert.Null(vm.TransferMessage);
        Assert.Empty(config.Current.Rules);
    }

    [AvaloniaFact]
    public async Task A_bad_file_says_so_and_changes_nothing()
    {
        var path = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(path, "{\"hello\": 1}");
        var config = new MemoryConfigStore();
        var vm = Settings(config, new FakeWindows { OpenPath = path });

        await vm.ImportDataCommand.ExecuteAsync(null);

        Assert.True(vm.TransferFailed);
        Assert.Contains("isn't a NudgeJudge export", vm.TransferMessage);
        Assert.Empty(config.Current.Rules);
    }

    [AvaloniaFact]
    public async Task Cancelling_a_picker_does_nothing()
    {
        var vm = Settings(new MemoryConfigStore(), new FakeWindows());
        await vm.ExportDataCommand.ExecuteAsync(null);
        await vm.ImportDataCommand.ExecuteAsync(null);
        Assert.Null(vm.TransferMessage);
    }
}

/// <summary>Writes a tampered export the way the app would (camelCase), without the app's internal JSON context.</summary>
internal static class ExportJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}
