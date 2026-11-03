using Avalonia.Controls;
using Avalonia.Threading;
using NudgeJudge.ViewModels;

namespace NudgeJudge.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();
        EditorDock.Attach(Root, Scrim, 360, () => (DataContext as RulesViewModel)?.Editor.CancelCommand.Execute(null));

        // Opening the tester is a request to type a link, so put the caret there.
        TesterDock.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && TesterDock.IsVisible)
                Dispatcher.UIThread.Post(() => TestUrlBox.Focus(), DispatcherPriority.Loaded);
        };
    }
}
