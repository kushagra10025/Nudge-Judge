using NudgeJudge.Core.Platform;

namespace NudgeJudge.Core.Tests;

public sealed class DetachedProcessTests
{
    [LinuxFact]
    public async Task Runs_in_its_own_session_with_no_inherited_stdio()
    {
        var dir = Directory.CreateTempSubdirectory("nj-detached-").FullName;
        try
        {
            var done = Path.Combine(dir, "done");
            // Arguments with spaces and shell syntax reach the program untouched.
            var script = "readlink /proc/$$/fd/0 > \"$1/stdin\"; cut -d' ' -f6 /proc/$$/stat > \"$1/sid\"; printf %s \"$2\" > \"$1/arg\"; touch \"$1/done\"";
            DetachedProcess.Start(["/bin/sh", "-c", script, "sh", dir, "a b; $(rm -rf /) 'q'"]);

            for (var i = 0; i < 100 && !File.Exists(done); i++) await Task.Delay(50);
            Assert.True(File.Exists(done), "the detached program didn't run");

            Assert.Equal("/dev/null", File.ReadAllText(Path.Combine(dir, "stdin")).Trim());
            var ourSession = File.ReadAllText("/proc/self/stat").Split(' ')[5];
            Assert.NotEqual(ourSession, File.ReadAllText(Path.Combine(dir, "sid")).Trim());
            Assert.Equal("a b; $(rm -rf /) 'q'", File.ReadAllText(Path.Combine(dir, "arg")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(new[] { @"C:\Apps\NudgeJudge.Desktop.exe", "https://example.com/?q=1" }, @"C:\Apps\NudgeJudge.Desktop.exe https://example.com/?q=1")]
    [InlineData(new[] { @"C:\Program Files\NJ\nj.exe", "" }, @"""C:\Program Files\NJ\nj.exe"" """"")]
    [InlineData(new[] { @"a\b c\", @"say ""hi""" }, @"""a\b c\\"" ""say \""hi\""""")]
    [InlineData(new[] { @"x\\""y" }, @"""x\\\\\""y""")]
    public void Windows_command_lines_split_back_to_the_same_argv(string[] argv, string expected)
    {
        Assert.Equal(expected, DetachedProcess.WindowsCommandLine(argv));
    }
}
