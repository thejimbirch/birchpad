using System.Runtime.InteropServices;
using Birchpad.Core.Mac;

namespace Birchpad.Core.Tests;

public class ChildProcessTests
{
    [Fact]
    public void Run_captures_output_error_and_exit_code()
    {
        var (exitCode, output, error) = ProcessRunner.Run("/bin/sh", ["-c", "echo out; echo err >&2; exit 3"], TimeSpan.FromSeconds(10));

        Assert.Equal(3, exitCode);
        Assert.Equal("out\n", output);
        Assert.Equal("err\n", error);
    }

    [Fact]
    public void Child_leads_its_own_process_group()
    {
        var (_, output, _) = ProcessRunner.Run("/bin/sh", ["-c", "echo $$; ps -o pgid= -p $$"], TimeSpan.FromSeconds(10));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(lines[0], lines[1]);
        Assert.NotEqual(getpgrp().ToString(), lines[1]);
    }

    [Fact]
    public void A_hangup_sent_to_the_childs_group_does_not_reach_the_caller()
    {
        var hungUp = false;
        using var registration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            hungUp = true;
            context.Cancel = true;
        });

        var (exitCode, _, _) = ProcessRunner.Run("/bin/sh", ["-c", "kill -HUP 0; sleep 5"], TimeSpan.FromSeconds(10));
        Thread.Sleep(200);

        Assert.Equal(128 + 1, exitCode);
        Assert.False(hungUp);
    }

    [Fact]
    public void Timeout_kills_the_whole_group()
    {
        using var process = ChildProcess.Start("/bin/sh", ["-c", "sleep 30 & echo $!; wait"]);
        var grandchild = int.Parse(process.StandardOutput.ReadLine()!);

        Assert.False(process.WaitForExit(TimeSpan.FromMilliseconds(200)));
        process.Kill();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (kill(grandchild, 0) == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.True(process.HasExited);
        Assert.NotEqual(0, kill(grandchild, 0));
    }

    [Fact]
    public void Input_reaches_the_child_and_closing_it_ends_the_child()
    {
        using var process = ChildProcess.Start("/bin/cat", [], redirectInput: true, captureError: false);
        process.StandardInput!.WriteLine("ping");
        process.StandardInput.Flush();

        Assert.Equal("ping", process.StandardOutput.ReadLine());
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Child_starts_in_the_working_directory_with_stdin_on_dev_null()
    {
        var directory = Directory.CreateTempSubdirectory("birchpad-child-");
        try
        {
            var (_, output, _) = ProcessRunner.Run("/bin/sh", ["-c", "pwd -P; cat; echo done"], TimeSpan.FromSeconds(10), directory.FullName);

            Assert.EndsWith(directory.Name + "\ndone\n", output);
        }
        finally
        {
            directory.Delete();
        }
    }

    [Fact]
    public void Missing_program_throws()
    {
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => ProcessRunner.Run("/nonexistent/birchpad", [], TimeSpan.FromSeconds(1)));
    }

    [DllImport("libc")]
    private static extern int getpgrp();

    [DllImport("libc")]
    private static extern int kill(int pid, int signal);
}
