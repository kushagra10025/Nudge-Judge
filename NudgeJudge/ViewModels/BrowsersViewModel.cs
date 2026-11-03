using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NudgeJudge.Core.Browsers;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Services;

namespace NudgeJudge.ViewModels;

public sealed partial class BrowsersViewModel : ViewModelBase
{
    private static readonly Uri TryUrl = new("https://example.com/");

    private readonly IConfigStore _config;
    private readonly IBrowserDiscovery _discovery;
    private readonly LinkRouter _router;

    public BrowsersViewModel(IConfigStore config, IBrowserDiscovery discovery, LinkRouter router,
        IDialogService dialogs, IUiDispatcher dispatcher)
    {
        _config = config;
        _discovery = discovery;
        _router = router;
        Editor = new BrowserEditorViewModel(config, dialogs, this);
        Undo = new UndoBar(dispatcher);
        config.Changed += (_, _) => dispatcher.Post(Reload);
        Reload();
    }

    public ObservableCollection<BrowserItemViewModel> Browsers { get; } = [];
    public BrowserEditorViewModel Editor { get; }
    public UndoBar Undo { get; }
    public bool HasBrowsers => Browsers.Count > 0;
    public bool NoBrowsers => !HasBrowsers;
    public bool HasSampleBrowsers => Browsers.Any(b => b.IsSample);

    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial bool StatusIsProblem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsScanning { get; set; }

    [RelayCommand]
    private void Add()
    {
        Editor.OpenNew();
        SyncEditingFlags();
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!_discovery.IsSupported)
        {
            SetStatus("Automatic detection isn't available on this system yet. Add browsers manually.", problem: false);
            return;
        }

        IsScanning = true;
        try
        {
            IReadOnlyList<BrowserProfile> found;
            try
            {
                found = await _discovery.DiscoverAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SetStatus("Scanning failed: " + e.Message, problem: true);
                return;
            }

            // Same command as an existing entry means it's already known. A matching sample has now
            // been confirmed as really installed, so it stops being labelled a sample.
            var current = _config.Current.Browsers;
            var fresh = found.Where(f => !current.Any(b => SameCommand(b, f))).ToList();
            var confirmed = current.Where(b => b.Source == BrowserSource.Sample && found.Any(f => SameCommand(b, f)))
                .Select(b => b.Id).ToHashSet();

            var samplesRemoved = false;
            if (fresh.Count > 0 || confirmed.Count > 0)
            {
                _config.Update(c =>
                {
                    // Checked again here in case the update picked up browsers another process just added.
                    c.Browsers.AddRange(fresh.Where(f => !c.Browsers.Any(b => SameCommand(b, f))));
                    foreach (var b in c.Browsers.Where(b => confirmed.Contains(b.Id)))
                        b.Source = BrowserSource.Discovered;
                    // Real browsers have arrived, so the sample browsers and rules have done their job.
                    if (fresh.Count > 0)
                        samplesRemoved = SampleData.RemoveSamples(c);
                    if (c.FindBrowser(c.Settings.PrimaryBrowserId) is null)
                        c.Settings.PrimaryBrowserId = c.Browsers.FirstOrDefault(b => !b.IsPrivate)?.Id ?? c.Browsers.FirstOrDefault()?.Id;
                });
            }

            var message = fresh.Count switch
            {
                0 when found.Count == 0 => "No browsers found. Add one manually.",
                0 => "No new browsers found. Everything installed is already listed.",
                1 => "Found 1 new browser.",
                var n => $"Found {n} new browsers.",
            };
            SetStatus(samplesRemoved ? message + " Removed the sample browsers and rules." : message, problem: false);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static bool SameCommand(BrowserProfile a, BrowserProfile b) =>
        string.Equals(a.ExecutablePath.Trim(), b.ExecutablePath.Trim(),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        string.Equals(NormalizeArguments(a.Arguments), NormalizeArguments(b.Arguments), StringComparison.Ordinal);

    /// <summary>A trailing <c>{url}</c> means the same as none: the launcher appends the URL either way.</summary>
    private static string NormalizeArguments(string args)
    {
        args = args.Trim();
        return args.EndsWith(BrowserLauncher.UrlPlaceholder, StringComparison.Ordinal)
            ? args[..^BrowserLauncher.UrlPlaceholder.Length].TrimEnd()
            : args;
    }

    private bool CanScan() => !IsScanning;

    internal void Edit(BrowserItemViewModel item)
    {
        if (_config.Current.FindBrowser(item.Id) is not { } b) return;
        Editor.OpenExisting(b);
        SyncEditingFlags();
    }

    internal void Delete(BrowserItemViewModel item)
    {
        if (Editor.EditingId == item.Id) Editor.Close();
        var index = _config.Current.Browsers.FindIndex(b => b.Id == item.Id);
        if (index < 0) return;
        var removed = _config.Current.Browsers[index].Clone();
        var wasPrimary = _config.Current.Settings.PrimaryBrowserId == item.Id;
        // By id, not index: the update may first pick up another process's changes and shift the list.
        _config.Update(c =>
        {
            c.Browsers.RemoveAll(b => b.Id == removed.Id);
            if (c.Settings.PrimaryBrowserId == removed.Id) c.Settings.PrimaryBrowserId = c.Browsers.FirstOrDefault()?.Id;
        });
        Undo.Show($"Removed {removed.Name}", () => _config.Update(c =>
        {
            if (c.Browsers.Any(b => b.Id == removed.Id)) return;
            c.Browsers.Insert(Math.Min(index, c.Browsers.Count), removed);
            if (wasPrimary) c.Settings.PrimaryBrowserId = removed.Id;
        }));
    }

    internal void MakePrimary(BrowserItemViewModel item) =>
        _config.Update(c => c.Settings.PrimaryBrowserId = item.Id);

    internal void Try(BrowserItemViewModel item)
    {
        if (_config.Current.FindBrowser(item.Id) is not { } b) return;
        var result = _router.OpenChosen(TryUrl, b);
        SetStatus(result.Kind == RouteKind.Launched ? $"Opened example.com in {b.Name}." : result.Message ?? "Couldn't start the browser.",
            problem: result.Kind != RouteKind.Launched);
    }

    internal void SyncEditingFlags()
    {
        foreach (var b in Browsers) b.IsEditing = Editor.IsOpen && Editor.EditingId == b.Id;
    }

    private void SetStatus(string message, bool problem)
    {
        StatusMessage = message;
        StatusIsProblem = problem;
    }

    private void Reload()
    {
        var cfg = _config.Current;
        Browsers.Clear();
        foreach (var b in cfg.Browsers)
        {
            var uses = cfg.Rules.Count(r => r.BrowserId == b.Id);
            Browsers.Add(new BrowserItemViewModel(this, b, uses, cfg.Settings.PrimaryBrowserId == b.Id));
        }
        OnPropertyChanged(nameof(HasBrowsers));
        OnPropertyChanged(nameof(NoBrowsers));
        OnPropertyChanged(nameof(HasSampleBrowsers));
        SyncEditingFlags();
    }
}

public sealed partial class BrowserItemViewModel(BrowsersViewModel owner, BrowserProfile browser, int ruleCount, bool isPrimary) : ViewModelBase
{
    public Guid Id { get; } = browser.Id;
    public string Name { get; } = browser.Name;
    public string ExecutablePath { get; } = browser.ExecutablePath;
    public string Arguments { get; } = browser.Arguments;
    public bool HasArguments => !string.IsNullOrWhiteSpace(Arguments);
    public string ArgumentsSuffix => HasArguments ? "  " + Arguments : "";
    public bool IsPrivate { get; } = browser.IsPrivate;
    public string? ProfileLabel { get; } = string.IsNullOrWhiteSpace(browser.ProfileLabel) ? null : browser.ProfileLabel;
    public bool HasProfile => ProfileLabel is not null && !Name.Contains(ProfileLabel, StringComparison.OrdinalIgnoreCase);
    public bool IsSample { get; } = browser.Source == BrowserSource.Sample;
    public bool IsDiscovered { get; } = browser.Source == BrowserSource.Discovered;
    public bool IsPrimary { get; } = isPrimary;
    public bool IsNotPrimary => !IsPrimary;
    public int RuleCount { get; } = ruleCount;
    public bool CanDelete => RuleCount == 0;
    public string UsageText => RuleCount switch { 0 => "No rules", 1 => "1 rule", var n => $"{n} rules" };
    public string DeleteHint => CanDelete ? "Remove browser" : $"Used by {UsageText}. Change or delete those rules first.";

    [ObservableProperty] public partial bool IsEditing { get; set; }

    [RelayCommand] private void Edit() => owner.Edit(this);
    [RelayCommand] private void Delete() => owner.Delete(this);
    [RelayCommand] private void MakePrimary() => owner.MakePrimary(this);
    [RelayCommand] private void Try() => owner.Try(this);
}

public sealed partial class BrowserEditorViewModel(IConfigStore config, IDialogService dialogs, BrowsersViewModel owner) : ViewModelBase
{
    private static readonly Uri PreviewUrl = new("https://example.com/");

    public Guid? EditingId { get; private set; }

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsNew { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(PathNote))]
    public partial string ExecutablePath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial string Arguments { get; set; } = "";

    [ObservableProperty] public partial bool IsPrivate { get; set; }
    [ObservableProperty] public partial string ProfileLabel { get; set; } = "";

    private BrowserSource _source = BrowserSource.Manual;

    public string Title => IsNew ? "Add browser" : "Edit browser";

    /// <summary>How the command will actually be run, so flags and quoting can be checked.</summary>
    public string Preview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExecutablePath)) return "";
            var args = BrowserLauncher.BuildArguments(Arguments, PreviewUrl).Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            return string.Join(' ', [Quote(ExecutablePath.Trim()), .. args]);
        }
    }

    public string? PathNote
    {
        get
        {
            var p = ExecutablePath.Trim();
            if (p.Length == 0) return null;
            if (!Path.IsPathRooted(p)) return "Will be looked up on your PATH.";
            return File.Exists(p) ? null : "No file at this path yet. It will fail to launch until it exists.";
        }
    }

    public void OpenNew()
    {
        EditingId = null;
        IsNew = true;
        Name = ExecutablePath = Arguments = ProfileLabel = "";
        IsPrivate = false;
        _source = BrowserSource.Manual;
        IsOpen = true;
    }

    public void OpenExisting(BrowserProfile b)
    {
        EditingId = b.Id;
        IsNew = false;
        Name = b.Name;
        ExecutablePath = b.ExecutablePath;
        Arguments = b.Arguments;
        IsPrivate = b.IsPrivate;
        ProfileLabel = b.ProfileLabel ?? "";
        _source = b.Source;
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        EditingId = null;
        owner.SyncEditingFlags();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await dialogs.PickExecutableAsync() is { } path)
        {
            ExecutablePath = path;
            if (string.IsNullOrWhiteSpace(Name)) Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    [RelayCommand] private void Cancel() => Close();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var id = EditingId;
        config.Update(c =>
        {
            var b = c.FindBrowser(id);
            if (b is null)
            {
                b = new BrowserProfile();
                c.Browsers.Add(b);
                c.Settings.PrimaryBrowserId ??= b.Id;
            }
            b.Name = Name.Trim();
            b.ExecutablePath = ExecutablePath.Trim();
            b.Arguments = Arguments.Trim();
            b.IsPrivate = IsPrivate;
            b.ProfileLabel = string.IsNullOrWhiteSpace(ProfileLabel) ? null : ProfileLabel.Trim();
            // Editing a sample makes it the user's own.
            b.Source = _source == BrowserSource.Sample ? BrowserSource.Manual : _source;
        });
        Close();
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(ExecutablePath);

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
