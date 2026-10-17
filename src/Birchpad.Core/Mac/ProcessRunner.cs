namespace Birchpad.Core.Mac;

/// <summary>Runs a program with arguments, captures its output, and kills it after a timeout.</summary>
public static class ProcessRunner
{
    public static (int ExitCode, string Output, string Error) Run(string file, IEnumerable<string> args, TimeSpan timeout, string? workingDirectory = null)
    {
        // Its own session, so nothing it starts can signal the plugin service's process group.
        using var process = ChildProcess.Start(file, args, workingDirectory: workingDirectory);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError!.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill();
            return (-1, string.Empty, $"{Path.GetFileName(file)} timed out after {timeout.TotalSeconds:0} seconds");
        }

        return (process.ExitCode, output.Result, error.Result);
    }
}
