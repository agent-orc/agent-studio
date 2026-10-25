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

        var started = Stopwatch.StartNew();
        AgentStudio.Git.HostGitProcessHardening.Apply(psi);
        using var p = Process.Start(psi);
        if (p == null) return TaskHistoryGitResult.Failed("Failed to start git.");

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)Math.Ceiling(timeout.TotalMilliseconds)))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "TaskHistoryGitRunner:kill"); }
            GitProcessTelemetry.Record(args.Count > 0 ? args[0] : "", started.ElapsedMilliseconds, -1, timedOut: true);
            return TaskHistoryGitResult.Failed(TaskHistoryGitResult.TimedOut);
        }

        var result = new TaskHistoryGitResult(
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult(),
            p.ExitCode);
        GitProcessTelemetry.Record(args.Count > 0 ? args[0] : "", started.ElapsedMilliseconds, result.Code);
        return result;
    }
}
