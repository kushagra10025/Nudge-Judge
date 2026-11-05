using Avalonia.Headless.XUnit;
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
        await importing.ImportDataCommand.ExecuteAsync(null);
        Assert.False(importing.TransferFailed);
        Assert.Equal("Imported 1 rule and 1 browser.", importing.TransferMessage);
        Assert.Equal("GitHub", Assert.Single(to.Current.Rules).Name);
        Assert.Equal(browser.Id, Assert.Single(to.Current.Browsers).Id);
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
