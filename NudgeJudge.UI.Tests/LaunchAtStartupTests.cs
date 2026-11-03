using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.UI.Tests;

/// <summary>The "Launch at startup" switch in Settings.</summary>
public sealed class LaunchAtStartupTests
{
    private static SettingsViewModel ViewModel(FakeStartup startup) =>
        new(new MemoryConfigStore(), new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), startup);

    private static ToggleSwitch Toggle(SettingsViewModel vm)
    {
        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Launch at startup");
    }

    [AvaloniaFact]
    public void Reflects_the_os_entry()
    {
        Assert.False(ViewModel(new FakeStartup()).LaunchAtStartup);
        Assert.True(ViewModel(new FakeStartup { Enabled = true }).LaunchAtStartup);
    }

    [AvaloniaFact]
    public void Switching_on_adds_the_entry_and_off_removes_it()
    {
        var startup = new FakeStartup();
        var vm = ViewModel(startup);
        var toggle = Toggle(vm);

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(startup.Enabled);
        Assert.True(vm.LaunchAtStartup);

        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(startup.Enabled);
    }

    [AvaloniaFact]
    public void A_failed_change_springs_back_and_says_why()
    {
        var startup = new FakeStartup { Fail = new UnauthorizedAccessException("Access denied.") };
        var vm = ViewModel(startup);

        vm.LaunchAtStartup = true;

        Assert.False(vm.LaunchAtStartup);
        Assert.True(vm.HasStartupMessage);
        Assert.Contains("Access denied.", vm.StartupMessage);

        startup.Fail = null;
        vm.LaunchAtStartup = true;
        Assert.True(vm.LaunchAtStartup);
        Assert.False(vm.HasStartupMessage);
    }

    [AvaloniaFact]
    public void Unsupported_systems_disable_the_switch()
    {
        var vm = ViewModel(new FakeStartup { IsSupported = false });
        var toggle = Toggle(vm);

        Assert.False(vm.CanLaunchAtStartup);
        Assert.False(toggle.IsEnabled);
    }
}
