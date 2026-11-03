using System.ComponentModel;
using System.Diagnostics;

namespace NudgeJudge.Core.Platform;

/// <param name="Started">False when the program couldn't be started at all (usually: not installed).</param>
public sealed record CommandResult(bool Started, int ExitCode, string Output, string Error)
{
    public bool Succeeded => Started && ExitCode == 0;

    public static CommandResult NotFound { get; } = new(false, -1, "", "");

    /// <summary>The first non-empty line of stderr, else stdout, for showing in a message.</summary>
    public string FirstErrorLine =>
        (Error + "\n" + Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? "";
}

/// <summary>Runs OS tools (xdg-mime and friends). Faked in tests.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Starts a program with an explicit argv (no shell) and collects its output.</summary>
public sealed class ProcessCommandRunner(TimeSpan? timeout = null) : ICommandRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            return CommandResult.NotFound;
        }
        if (process is null) return CommandResult.NotFound;

        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return new CommandResult(true, -1, "", $"{Path.GetFileName(program)} didn't finish within {_timeout.TotalSeconds:0} seconds.");
            }

            return new CommandResult(true, process.ExitCode, await output, await error);
        }
    }
}
