using NudgeJudge.Core.Models;

namespace NudgeJudge.Services;

/// <summary>Window lifetime operations view models may request without touching views.</summary>
public interface IWindowService
{
    void ShowMainWindow(AppPage? page = null);
    void ShowChooser();
    void HideChooser();
    void Quit();
}

public enum AppPage { Rules, Browsers, Activity, Settings }

public interface IThemeService
{
    void Apply(ThemePreference preference);
}

public interface IDialogService
{
    /// <summary>Lets the user pick an executable. Returns its full path, or null if cancelled.</summary>
    Task<string?> PickExecutableAsync();

    /// <summary>Asks where to save a JSON file. Returns a local path, or null if cancelled.</summary>
    Task<string?> PickSaveJsonAsync(string title, string suggestedName) => Task.FromResult<string?>(null);

    /// <summary>Lets the user pick a JSON file to open. Returns a local path, or null if cancelled.</summary>
    Task<string?> PickOpenJsonAsync(string title) => Task.FromResult<string?>(null);
}

/// <summary>Marshals work onto the UI thread (core events may arrive from other threads).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
