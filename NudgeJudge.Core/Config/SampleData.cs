using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Config;

/// <summary>
/// First-run sample configuration, so the UI has something to show before browser discovery
/// exists. Everything here is marked <see cref="BrowserSource.Sample"/> and is safe to delete.
/// </summary>
public static class SampleData
{
    public static AppConfig Create()
    {
        var win = OperatingSystem.IsWindows();
        var firefox = new BrowserProfile
        {
            Name = "Firefox",
            ExecutablePath = win ? @"C:\Program Files\Mozilla Firefox\firefox.exe" : "firefox",
            Source = BrowserSource.Sample,
        };
        var firefoxPrivate = new BrowserProfile
        {
            Name = "Firefox Private",
            ExecutablePath = firefox.ExecutablePath,
            Arguments = "--private-window {url}",
            IsPrivate = true,
            Source = BrowserSource.Sample,
        };
        var chrome = new BrowserProfile
        {
            Name = "Chrome",
            ExecutablePath = win ? @"C:\Program Files\Google\Chrome\Application\chrome.exe" : "google-chrome",
            Source = BrowserSource.Sample,
        };
        var chromeWork = new BrowserProfile
        {
            Name = "Chrome Work",
            ExecutablePath = chrome.ExecutablePath,
            Arguments = "--profile-directory=\"Profile 1\" {url}",
            ProfileLabel = "Profile 1",
            Source = BrowserSource.Sample,
        };

        return new AppConfig
        {
            Settings = new AppSettings { PrimaryBrowserId = firefox.Id },
            Browsers = [firefox, chrome, chromeWork, firefoxPrivate],
            Rules =
            [
                new Rule { Name = "Work tracker", MatchKind = MatchKind.Wildcard, Pattern = "*.atlassian.net", BrowserId = chromeWork.Id },
                new Rule { Name = "Company repos", MatchKind = MatchKind.PathPrefix, Pattern = "github.com/acme", BrowserId = chromeWork.Id },
                new Rule { Name = "Banking", MatchKind = MatchKind.Regex, Pattern = @"^https://([a-z0-9-]+\.)*mybank\.example/", BrowserId = firefoxPrivate.Id },
                new Rule { Name = "Video", MatchKind = MatchKind.Domain, Pattern = "www.youtube.com", BrowserId = firefox.Id },
            ],
        };
    }
}
