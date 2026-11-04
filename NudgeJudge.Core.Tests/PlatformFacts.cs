namespace NudgeJudge.Core.Tests;

/// <summary>
/// A test of Linux-only behaviour (desktop entries, /proc, shell wrappers). Reported as skipped elsewhere rather
/// than passing without running, so a Windows run says what it didn't cover.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux-only behaviour";
    }
}

/// <inheritdoc cref="LinuxFactAttribute"/>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux-only behaviour";
    }
}
