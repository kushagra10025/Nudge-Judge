using Avalonia.Controls;
using NudgeJudge.ViewModels;

namespace NudgeJudge.Views;

public partial class BrowsersView : UserControl
{
    public BrowsersView()
    {
        InitializeComponent();
        EditorDock.Attach(Root, Scrim, 360, () => (DataContext as BrowsersViewModel)?.Editor.CancelCommand.Execute(null));
    }
}
