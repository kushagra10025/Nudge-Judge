using NudgeJudge.Core;
using NudgeJudge.Core.Ipc;

namespace NudgeJudge.Tray;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        // Started by the main program, which lets go of the single-instance lock for us to take over, then sends
        // whatever its launch brought. Already taken (another tray host won): nothing to do.
        using var claim = SingleInstance.ClaimExclusive(InstanceEndpoint.ForCurrentUser());
        if (claim is null) return 0;
        return new ResidentHost(claim, CoreServices.Load()).Run();
    }
}
