using Avalonia.Controls;
using Avalonia.Threading;

namespace NudgeJudge.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();

        // Opening the tester is a request to type a link, so put the caret there.
        TesterDock.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && TesterDock.IsVisible)
                Dispatcher.UIThread.Post(() => TestUrlBox.Focus(), DispatcherPriority.Loaded);
        };
    }
}
