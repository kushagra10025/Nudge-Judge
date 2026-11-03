using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using NudgeJudge.Core.Models;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.Services;

/// <summary>Owns the app's windows. The only place that creates or closes them.</summary>
internal sealed class DesktopWindowService(IClassicDesktopStyleApplicationLifetime lifetime) : IWindowService, IDialogService
{
    private MainWindow? _main;
    private ChooserWindow? _chooser;
    private bool _quitting;

    public MainWindowViewModel? Main { get; set; }
    public ChooserViewModel? Chooser { get; set; }
    public Func<bool> KeepRunningInBackground { get; set; } = () => true;

    /// <summary>True once the main window has been shown in this process.</summary>
    public bool MainWindowShown { get; private set; }

    /// <summary>Raised when the chooser closes and nothing else is open.</summary>
    public event EventHandler? AllWindowsClosed;

    public void ShowMainWindow(AppPage? page = null)
    {
        if (Main is null) return;
        if (page is { } p) Main.Navigate(p);

        if (_main is null)
        {
            _main = new MainWindow { DataContext = Main };
            _main.Closing += OnMainClosing;
            _main.Activated += (_, _) => Main?.OnActivated();
            _main.Closed += (_, _) => _main = null;
            lifetime.MainWindow = _main;
        }

        MainWindowShown = true;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowChooser()
    {
        if (Chooser is null) return;
        if (_chooser is null)
        {
            var window = new ChooserWindow { DataContext = Chooser };
            window.Closed += (_, _) =>
            {
                if (_chooser == window) _chooser = null;
                if (Chooser.Tabs.Count > 0) Chooser.DismissAllCommand.Execute(null);
                if (_main is not { IsVisible: true }) AllWindowsClosed?.Invoke(this, EventArgs.Empty);
            };
            _chooser = window;
        }
        _chooser.Show();
        _chooser.Activate();
    }

    public void HideChooser()
    {
        var window = _chooser;
        _chooser = null;
        window?.Close();
    }

    public void Quit()
    {
        _quitting = true;
        lifetime.Shutdown();
    }

    public async Task<string?> PickExecutableAsync()
    {
        var owner = (TopLevel?)_main ?? _chooser;
        if (owner is null) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a browser program",
            AllowMultiple = false,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || !KeepRunningInBackground()) return;
        e.Cancel = true;
        _main?.Hide();
    }
}

internal sealed class AvaloniaThemeService : IThemeService
{
    public void Apply(ThemePreference preference)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
