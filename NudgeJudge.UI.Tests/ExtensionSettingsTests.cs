using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.Core.NativeMessaging;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.UI.Tests;

/// <summary>Settings → Browser extension: the switch that writes the native host launcher and manifests.</summary>
public sealed class ExtensionSettingsTests
{
    private sealed class FakeExtension : INativeHostRegistration
    {
        public bool IsSupported { get; init; } = true;
        public bool Enabled { get; set; }
        public Exception? Fail { get; set; }
        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabled)
        {
            if (Fail is not null) throw Fail;
            Enabled = enabled;
        }

        public void Refresh() { }
    }

    private static SettingsViewModel ViewModel(FakeExtension extension) =>
        new(new MemoryConfigStore(), new FakeRegistrar(), new NoTheme(), new InlineDispatcher(), new FakeStartup(), extension: extension);

    private static ToggleSwitch Toggle(SettingsViewModel vm)
    {
        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Take links from the browser extension");
    }

    [AvaloniaFact]
    public void Switching_on_connects_and_off_disconnects()
    {
        var extension = new FakeExtension();
        var vm = ViewModel(extension);
        var toggle = Toggle(vm);
        Assert.False(toggle.IsChecked);

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(extension.Enabled);

        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(extension.Enabled);
    }

    [AvaloniaFact]
    public void Reflects_what_is_on_disk()
    {
        Assert.True(ViewModel(new FakeExtension { Enabled = true }).ExtensionConnected);
    }

    [AvaloniaFact]
    public void A_failed_change_springs_back_and_says_why()
    {
        var extension = new FakeExtension { Fail = new IOException("Disk full.") };
        var vm = ViewModel(extension);

        vm.ExtensionConnected = true;

        Assert.False(vm.ExtensionConnected);
        Assert.Contains("Disk full.", vm.ExtensionMessage);
    }

    [AvaloniaFact]
    public void Unsupported_systems_disable_the_switch()
    {
        var vm = ViewModel(new FakeExtension { IsSupported = false });
        Assert.False(Toggle(vm).IsEnabled);
    }
}
