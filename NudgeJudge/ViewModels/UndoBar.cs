using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

/// <summary>
/// "Deleted · Undo" toast for a page. Destructive actions apply immediately and offer a short
/// window to take them back, instead of asking for confirmation first.
/// </summary>
public sealed partial class UndoBar(IUiDispatcher dispatcher) : ViewModelBase
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(8);

    private Action? _undo;
    private int _generation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial string? Message { get; set; }

    public bool IsOpen => Message is not null;

    public void Show(string message, Action undo)
    {
        _undo = undo;
        Message = message;
        var generation = ++_generation;
        _ = Task.Delay(Window).ContinueWith(_ => dispatcher.Post(() =>
        {
            if (generation == _generation) Dismiss();
        }), TaskScheduler.Default);
    }

    [RelayCommand]
    private void Undo()
    {
        var undo = _undo;
        Dismiss();
        undo?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _generation++;
        _undo = null;
        Message = null;
    }
}
