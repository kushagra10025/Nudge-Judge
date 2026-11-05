using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NudgeJudge.Core.Platform;

/// <summary>
/// Starts a program that must outlive, and not be tied to, the process starting it. Used by the browser extension's
/// native host to start NudgeJudge: the host is a child of the browser, which may kill the host's process group
/// (Linux) or job (Windows, where Firefox puts native apps in a kill-on-close job) when it's done with it. NudgeJudge
/// then becomes the resident instance and has to survive that, and must never hold the browser's pipes.
/// </summary>
public static class DetachedProcess
{
    /// <summary>
    /// Unix: the shell points stdio at /dev/null, then becomes the program in a session of its own (setsid), so no
    /// signal sent to the browser's process group or session reaches it. Without setsid(1), it still runs, just not
    /// in its own session. The argv goes in as "$0" "$@", so nothing is ever parsed by the shell.
    /// </summary>
    internal const string UnixScript =
        "exec </dev/null >/dev/null 2>&1; if command -v setsid >/dev/null; then exec setsid \"$0\" \"$@\"; fi; exec \"$0\" \"$@\"";

    /// <exception cref="IOException">It couldn't be started.</exception>
    public static void Start(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0) throw new ArgumentException("Nothing to start.", nameof(argv));
        if (OperatingSystem.IsWindows()) StartWindows(argv);
        else StartUnix(argv);
    }

    [UnsupportedOSPlatform("windows")]
    private static void StartUnix(IReadOnlyList<string> argv)
    {
        // Redirected so the shell never even briefly holds our stdio (the browser's pipes); it swaps in /dev/null at once.
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(UnixScript);
        foreach (var arg in argv) start.ArgumentList.Add(arg);
        try
        {
            using var shell = Process.Start(start) ?? throw new IOException($"Couldn't start {argv[0]}.");
            shell.StandardInput.Close();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new IOException($"Couldn't start {argv[0]}: {e.Message}", e);
        }
    }

    private const uint DetachedProcessFlag = 0x00000008;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Windows: no inherited handles (so none of the browser's pipes), its own process group, and out of the caller's
    /// job when the job allows that. Firefox's does (BREAKAWAY_OK); a job that doesn't refuses the flag, and then the
    /// program starts inside it rather than not at all.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void StartWindows(IReadOnlyList<string> argv)
    {
        var commandLine = WindowsCommandLine(argv);
        if (TryCreate(commandLine, DetachedProcessFlag | CreateNewProcessGroup | CreateBreakawayFromJob, out var error)) return;
        if (error == ErrorAccessDenied && TryCreate(commandLine, DetachedProcessFlag | CreateNewProcessGroup, out error)) return;
        throw new IOException($"Couldn't start {argv[0]}: {new Win32Exception(error).Message}");
    }

    [SupportedOSPlatform("windows")]
    private static bool TryCreate(string commandLine, uint flags, out int error)
    {
        // CreateProcessW may write to the command line buffer, so it can't be a managed string.
        var buffer = Marshal.StringToHGlobalUni(commandLine);
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(0, buffer, 0, 0, false, flags, 0, 0, ref startup, out var info))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }
            CloseHandle(info.Process);
            CloseHandle(info.Thread);
            error = 0;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Joins argv the way the C runtime (and .NET) splits it back: arguments with spaces or quotes are quoted, a quote
    /// inside is escaped, and backslashes are only doubled where they come before a quote.
    /// </summary>
    internal static string WindowsCommandLine(IEnumerable<string> argv)
    {
        var sb = new StringBuilder();
        foreach (var arg in argv)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                sb.Append(arg);
                continue;
            }
            sb.Append('"');
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
        }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(nint applicationName, nint commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, nint currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
