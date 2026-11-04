using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace NudgeJudge.Services;

/// <summary>A newer release than the one running.</summary>
public sealed record AvailableUpdate(string Version, string? Notes);

/// <summary>Finds, downloads and installs new releases. View models use this; only the implementation knows Velopack.</summary>
public interface IUpdateService
{
    /// <summary>False when the app wasn't installed by its installer (a dev build, a plain publish folder): nothing to update.</summary>
    bool IsSupported { get; }

    /// <summary>The running version, for About.</summary>
    string CurrentVersion { get; }

    /// <summary>The newer release, or null when this is the latest. Throws on network or feed errors.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the release last returned by <see cref="CheckAsync"/>, reporting 0–100.</summary>
    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default);

    /// <summary>Closes the app, installs the downloaded release and starts it again. Does not return when it works.</summary>
    void ApplyAndRestart(AvailableUpdate update);
}

public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/kushagra10025/Nudge-Judge";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";

    /// <summary>The build's own version (set by <c>-p:Version=…</c> at publish), without the source-revision suffix.</summary>
    public static string BuildVersion { get; } = ReadBuildVersion();

    private static string ReadBuildVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

/// <summary>Updates from the project's GitHub Releases, as packed and uploaded by <c>vpk</c>.</summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    private readonly UpdateManager _manager = new(new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false));
    private UpdateInfo? _pending;

    public bool IsSupported => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppInfo.BuildVersion;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _pending = info;
        if (info is null) return null;
        var target = info.TargetFullRelease;
        return new AvailableUpdate(target.Version.ToString(), target.NotesMarkdown);
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
    {
        var info = Pending(update);
        return _manager.DownloadUpdatesAsync(info, progress, cancellationToken);
    }

    public void ApplyAndRestart(AvailableUpdate update) => _manager.ApplyUpdatesAndRestart(Pending(update).TargetFullRelease);

    private UpdateInfo Pending(AvailableUpdate update) =>
        _pending is { } info && info.TargetFullRelease.Version.ToString() == update.Version
            ? info
            : throw new InvalidOperationException("That update is no longer the latest; check again.");
}
