using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NudgeJudge.ViewModels;

namespace NudgeJudge.Views;

public partial class ChooserWindow : Window
{
    public ChooserWindow()
    {
        InitializeComponent();
        // Tunnel so digits reach us before buttons; skipped while typing in a text box.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChooserViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            vm.DismissCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None || FocusManager?.GetFocusedElement() is TextBox) return;

        var number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad0,
            _ => 0,
        };
        if (number > 0)
        {
            vm.ChooseNumberCommand.Execute(number);
            e.Handled = true;
        }
    }
}
