using System.Globalization;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Identifies the current OS boot so "until reboot" rules can expire.
/// Linux: the kernel's random boot_id. Elsewhere: boot time derived from the uptime counter,
/// compared with a tolerance because that derivation jitters slightly.
/// </summary>
public sealed class SystemBoot : ISystemBoot
{
    private const string LinuxBootIdPath = "/proc/sys/kernel/random/boot_id";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    public string CurrentBootId { get; } = ReadBootId();

    public bool IsCurrentBoot(string bootId)
    {
        if (string.Equals(bootId, CurrentBootId, StringComparison.Ordinal))
            return true;

        // Uptime-derived ids are unix seconds; accept small drift.
        return long.TryParse(bootId, NumberStyles.None, CultureInfo.InvariantCulture, out var a) &&
               long.TryParse(CurrentBootId, NumberStyles.None, CultureInfo.InvariantCulture, out var b) &&
               Math.Abs(a - b) <= Tolerance.TotalSeconds;
    }

    private static string ReadBootId()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var id = File.ReadAllText(LinuxBootIdPath).Trim();
                if (id.Length > 0) return id;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // GetTickCount64 on Windows includes sleep time, so this is stable across suspend there.
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return boot.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }
}
