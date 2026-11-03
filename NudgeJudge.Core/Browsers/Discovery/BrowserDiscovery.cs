namespace NudgeJudge.Core.Browsers.Discovery;

public static class BrowserDiscovery
{
    /// <summary>The discovery implementation for the OS this process runs on.</summary>
    public static IBrowserDiscovery ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsBrowserDiscovery();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new LinuxBrowserDiscovery();
        return new StubBrowserDiscovery();
    }
}
