using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NudgeJudge.Core.NativeMessaging;

namespace NudgeJudge.UI.Tests;

/// <summary>
/// Runs the real app the way a browser does for the extension. In host mode stdout belongs to the browser: one stray
/// line (a log message, a library warning) and every extension call fails, while the protocol's unit tests stay green.
/// </summary>
public sealed class NativeHostProcessTests
{
    [Fact(Timeout = 60_000)]
    public async Task Host_mode_writes_protocol_frames_and_nothing_else()
    {
        var dll = typeof(NativeHostProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DesktopDll").Value!;
        Assert.True(File.Exists(dll), $"Build NudgeJudge.Desktop first: {dll} is missing.");

        // Linux: a throwaway config and runtime folder. Windows has no such override; there the real config folder is
        // read (and created on a fresh machine), as any launch of the app would.
        var dir = Directory.CreateTempSubdirectory("nj-host-").FullName;
        try
        {
            var start = new ProcessStartInfo(Muxer())
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { dll, NativeHost.Flag, "chrome-extension://test/" }) start.ArgumentList.Add(arg);
            start.Environment["XDG_CONFIG_HOME"] = dir;
            start.Environment["XDG_RUNTIME_DIR"] = dir;

            using var host = Process.Start(start)!;
            var stdout = new MemoryStream();
            var reading = host.StandardOutput.BaseStream.CopyToAsync(stdout, TestContext.Current.CancellationToken);
            var errors = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

            var stdin = host.StandardInput.BaseStream;
            Frame(stdin, """{"v":1,"type":"ping"}""");
            Frame(stdin, """{"v":1,"type":"open","url":"javascript:alert(1)"}""");
            // The browser closing the pipe is what ends a session.
            host.StandardInput.Close();

            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
            await reading;
            Assert.True(host.ExitCode == 0, $"Exit code {host.ExitCode}. stderr: {await errors}");

            stdout.Position = 0;
            var replies = new List<JsonElement>();
            while (NativeMessageFraming.Read(stdout) is { } frame) replies.Add(JsonDocument.Parse(frame).RootElement.Clone());

            Assert.Equal(2, replies.Count);
            Assert.Equal("pong", replies[0].GetProperty("action").GetString());
            Assert.False(replies[1].GetProperty("ok").GetBoolean());
            // Reading stopped at a clean end: no bytes beyond the two frames.
            Assert.Equal(stdout.Length, stdout.Position);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Frame(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>The dotnet host of the runtime these tests run on: <c>…/shared/Microsoft.NETCore.App/10.0.x/</c> → <c>…/dotnet</c>.</summary>
    private static string Muxer()
    {
        var root = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/', '\\')).Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }
}
