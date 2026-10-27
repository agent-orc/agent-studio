using System.Diagnostics;
using System.Text;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>
/// Starts the git processes of <see cref="TaskFileHistoryService"/>. A seam so
/// tests can count every spawn a request makes, or fail one call on purpose,
/// without a slow or broken repository (AGT-3086).
/// </summary>
public interface ITaskHistoryGitRunner
{
    /// <summary>
    /// Runs <c>git</c> with <paramref name="args"/> in <paramref name="cwd"/> and
    /// kills it after <paramref name="timeout"/>. A process that could not be
    /// started or was killed reports <see cref="TaskHistoryGitResult.RunnerFailed"/>.
    /// </summary>
    TaskHistoryGitResult Run(string cwd, IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>
/// Outcome of one git call. <see cref="Code"/> is git's exit code, or a negative
/// value when git never produced one (not started, timed out, budget spent).
/// </summary>
public sealed record TaskHistoryGitResult(string Out, string Err, int Code)
{
    public const string TimedOut = "git timed out.";

    /// <summary>
    /// True when git gave no answer at all. A non-zero exit is still an answer
    /// (for example "this path is not in that commit"); this is not.
    /// </summary>
    public bool RunnerFailed => Code < 0;

    public static TaskHistoryGitResult Failed(string error) => new("", error, -1);
}

public sealed class ProcessTaskHistoryGitRunner : ITaskHistoryGitRunner
{
    public TaskHistoryGitResult Run(string cwd, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var result = GitProcessBoundary.Run(psi, timeout: timeout);
        return result.FailureKind switch
        {
            GitProcessFailureKind.None => new TaskHistoryGitResult(result.StandardOutput, result.StandardError, result.ExitCode),
            GitProcessFailureKind.TimedOut => TaskHistoryGitResult.Failed(TaskHistoryGitResult.TimedOut),
            _ => TaskHistoryGitResult.Failed(string.IsNullOrWhiteSpace(result.StandardError)
                ? "Failed to start git." : result.StandardError),
        };
    }
}
