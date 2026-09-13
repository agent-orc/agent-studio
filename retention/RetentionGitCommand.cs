using System.Diagnostics;

namespace AgentStudio.Retention;

public sealed record RetentionGitResult(int Code, string Output, string Error);

public static class RetentionGitCommand
{
    public static RetentionGitResult Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new RetentionGitResult(process.ExitCode, output, error);
    }
}
