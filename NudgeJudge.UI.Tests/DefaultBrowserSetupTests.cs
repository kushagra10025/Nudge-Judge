using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.Core.Platform;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.UI.Tests;

/// <summary>The "Default browser" section of Settings, driven through the real view.</summary>
public sealed class DefaultBrowserSetupTests
{
    private const string NotDefaultLabel = "NudgeJudge isn't your default browser yet";
    private const string DefaultLabel = "NudgeJudge is your default browser";
    private const string CheckingLabel = "Checking the default browser…";

    private static SettingsViewModel ViewModel(FakeRegistrar registrar) =>
        new(new MemoryConfigStore(), registrar, new NoTheme(), new InlineDispatcher());

    private static Window Show(SettingsViewModel vm)
    {
        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task Settle(Window window)
    {
        // Let async commands finish and bindings/layout catch up.
        for (var i = 0; i < 5; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
        window.UpdateLayout();
    }

    private static bool Shows(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static Button MakeDefault(Window window, SettingsViewModel vm) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.RegisterCommand));

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    [AvaloniaFact]
    public void Says_checking_until_the_os_has_answered()
    {
        var registrar = new FakeRegistrar();
        var vm = ViewModel(registrar);

        var window = Show(vm);

        Assert.Equal(0, registrar.StatusChecks); // nothing spawns until the window is actually used
        Assert.True(Shows(window, CheckingLabel));
        Assert.False(Shows(window, NotDefaultLabel));
        Assert.False(MakeDefault(window, vm).IsEffectivelyVisible);
        Assert.False(vm.NeedsSetup);
    }

    [AvaloniaFact]
    public async Task Not_registered_offers_make_default()
    {
        var registrar = new FakeRegistrar();
        var vm = ViewModel(registrar);
        var window = Show(vm);

        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);

        Assert.True(Shows(window, NotDefaultLabel));
        Assert.True(Shows(window, "Links open in another browser."));
        Assert.False(Shows(window, CheckingLabel));
        Assert.True(MakeDefault(window, vm).IsEffectivelyVisible);
        Assert.Equal("Not default yet", vm.SignalLabel);
    }

    [AvaloniaFact]
    public async Task Clicking_make_default_registers_and_hides_the_button()
    {
        var registrar = new FakeRegistrar();
        var vm = ViewModel(registrar);
        var window = Show(vm);
        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);

        Click(window, MakeDefault(window, vm));
        await Settle(window);

        Assert.Equal(1, registrar.Registrations);
        Assert.True(vm.IsRegistered);
        Assert.True(Shows(window, DefaultLabel));
        Assert.True(Shows(window, "Links come here first."));
        Assert.False(Shows(window, NotDefaultLabel));
        Assert.False(MakeDefault(window, vm).IsEffectivelyVisible);
        Assert.Equal("Routing on", vm.SignalLabel);
    }

    [AvaloniaFact]
    public async Task Button_is_disabled_while_registering()
    {
        var registrar = new FakeRegistrar { Gate = new TaskCompletionSource() };
        var vm = ViewModel(registrar);
        var window = Show(vm);
        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);
        var button = MakeDefault(window, vm);

        Click(window, button);
        await Settle(window);

        Assert.False(button.IsEffectivelyEnabled);
        Assert.True(Shows(window, "Registering…"));

        // A refresh triggered meanwhile (window activation) must not overwrite the in-progress state.
        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        Assert.Equal(1, registrar.StatusChecks);

        registrar.Gate.SetResult();
        await Settle(window);
        Assert.True(vm.IsRegistered);
    }

    [AvaloniaFact]
    public async Task Windows_style_handoff_completes_on_the_next_check()
    {
        // Windows can't be made default programmatically: register opens Default Apps and reports "not yet".
        var registrar = new FakeRegistrar
        {
            OnRegister = () => new(RegistrationState.NotRegistered, "Default apps is open. Choose NudgeJudge there."),
        };
        var vm = ViewModel(registrar);
        var window = Show(vm);
        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);

        Click(window, MakeDefault(window, vm));
        await Settle(window);

        Assert.True(Shows(window, "Default apps is open. Choose NudgeJudge there."));
        Assert.True(MakeDefault(window, vm).IsEffectivelyVisible);

        // The user picks NudgeJudge in Settings and comes back; activation re-checks.
        registrar.Status = new(RegistrationState.Registered, "Links come here first.");
        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);

        Assert.True(Shows(window, DefaultLabel));
    }

    [AvaloniaFact]
    public async Task Unsupported_platform_explains_without_a_button()
    {
        var registrar = new FakeRegistrar
        {
            IsSupported = false,
            Status = new(RegistrationState.Unsupported, "Not available on this system yet."),
        };
        var vm = ViewModel(registrar);
        var window = Show(vm);

        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);

        Assert.True(Shows(window, NotDefaultLabel));
        Assert.True(Shows(window, "Not available on this system yet."));
        Assert.False(MakeDefault(window, vm).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Registrar_failure_is_shown_not_thrown()
    {
        var registrar = new FakeRegistrar { OnRegister = () => throw new InvalidOperationException("boom") };
        var vm = ViewModel(registrar);
        var window = Show(vm);
        await vm.RefreshRegistrationCommand.ExecuteAsync(null);
        await Settle(window);

        Click(window, MakeDefault(window, vm));
        await Settle(window);

        Assert.True(Shows(window, "Registration failed: boom"));
        Assert.False(vm.IsRegistered);
    }
}
