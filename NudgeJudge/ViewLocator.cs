using Avalonia.Controls;
using Avalonia.Controls.Templates;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge;

/// <summary>Maps page view models to their views. Explicit, so it survives trimming.</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(GettingStartedViewModel)] = () => new GettingStartedView(),
        [typeof(RulesViewModel)] = () => new RulesView(),
        [typeof(BrowsersViewModel)] = () => new BrowsersView(),
        [typeof(ActivityViewModel)] = () => new ActivityView(),
        [typeof(SettingsViewModel)] = () => new SettingsView(),
    };

    public Control? Build(object? param) =>
        param is not null && Views.TryGetValue(param.GetType(), out var factory)
            ? factory()
            : new TextBlock { Text = "No view for " + param?.GetType().Name };

    public bool Match(object? data) => data is ViewModelBase && Views.ContainsKey(data.GetType());
}
