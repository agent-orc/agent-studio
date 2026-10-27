using System.Diagnostics;

namespace AgentStudio.Git;

/// <summary>
/// The one door every backend Git process passes through (AGT-3321, Git facts
/// S1). Before this, several sites started Git with their own
/// <see cref="Process.Start(ProcessStartInfo)"/>: their spawns were missing
/// from <see cref="GitProcessTelemetry"/>, the stale-lock guard could not see
/// them in <see cref="GitChildProcessRegistry"/>, and no read-request guard
/// could stop them. Each start here, in order:
/// <list type="number">
///   <item>asks <see cref="GitReadRequestGuard"/> whether the ambient request
///   may start Git (an unlisted GET throws in strict mode);</item>
///   <item>registers the child under its working directory;</item>
///   <item>waits for a slot of the ambient <see cref="GitProcessBudget"/> and
///   links its deadline (a no-op outside an index run);</item>
///   <item>runs the process through <see cref="GitNetworkProcessRunner"/>,
///   which hardens the configuration and bounds time and cancellation;</item>
///   <item>records the subcommand and duration in the telemetry scope.</item>
/// </list>
/// A caller that must own the <see cref="Process"/> itself (line streaming)
/// opens a <see cref="Begin"/> span around its own start instead.
/// <c>GitProcessBoundaryGuardTests</c> rejects any other Git start.
/// </summary>
internal static class GitProcessBoundary
{
    /// <summary>
    /// Bound for call sites that used to wait for Git without any limit (local
    /// index writes, clones). Long enough never to cut a healthy operation,
    /// finite so a hung child cannot hold its caller forever.
    /// </summary>
    internal static readonly TimeSpan LongOperationTimeout = TimeSpan.FromMinutes(30);

    internal static GitProcessResult Run(
        ProcessStartInfo startInfo,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var span = Open(startInfo);
        using var slot = GitProcessBudget.Acquire();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, GitProcessBudget.Token);
        var result = GitNetworkProcessRunner.Run(startInfo, stdin, timeout, linked.Token);
        span.Complete(result.ExitCode, result.FailureKind == GitProcessFailureKind.TimedOut);
        return result;
    }

    internal static GitBinaryProcessResult RunBinary(
        ProcessStartInfo startInfo,
        string? stdin,
        long maxOutputBytes,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var span = Open(startInfo);
        using var slot = GitProcessBudget.Acquire();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, GitProcessBudget.Token);
        var result = GitNetworkProcessRunner.RunBinary(startInfo, stdin, maxOutputBytes, timeout, linked.Token);
        span.Complete(result.ExitCode, result.FailureKind == GitProcessFailureKind.TimedOut);
        return result;
    }

    internal static async Task<GitProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        using var span = Open(startInfo);
        using var slot = await GitProcessBudget.AcquireAsync().ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, GitProcessBudget.Token);
        var result = await GitNetworkProcessRunner.RunAsync(startInfo, stdin, timeout, linked.Token, startProcess)
            .ConfigureAwait(false);
        span.Complete(result.ExitCode, result.FailureKind == GitProcessFailureKind.TimedOut);
        return result;
    }

    /// <summary>
    /// Opens a span for a Git process the caller starts itself. The span hardens
    /// <paramref name="startInfo"/>, applies the read-request guard, registers the
    /// child and records telemetry on <see cref="GitProcessSpan.Complete"/> or
    /// dispose. Start the process only after this returns.
    /// </summary>
    internal static GitProcessSpan Begin(ProcessStartInfo startInfo)
    {
        HostGitProcessHardening.Apply(startInfo);
        return Open(startInfo);
    }

    private static GitProcessSpan Open(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var command = CommandLabel(startInfo);
        GitReadRequestGuard.OnGitProcessStarting(command);
        return new GitProcessSpan(command, GitChildProcessRegistry.Track(startInfo.WorkingDirectory));
    }

    /// <summary>
    /// A short, low-cardinality label for one Git spawn: the subcommand (for
    /// example <c>status</c>, <c>merge-base</c>, <c>rev-parse</c>). Global
    /// options such as <c>-C &lt;path&gt;</c> and <c>-c key=value</c> are skipped, so
    /// a label never leaks a path, branch name or commit message into the logs.
    /// </summary>
    internal static string CommandLabel(ProcessStartInfo startInfo)
    {
        IReadOnlyList<string> args = startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList
            : (startInfo.Arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg is "-C" or "-c" or "--git-dir" or "--work-tree") { index++; continue; }
            if (arg.StartsWith('-')) continue;
            return arg;
        }
        return "git";
    }
}

/// <summary>One Git child between its guarded start and its recorded completion.</summary>
internal sealed class GitProcessSpan : IDisposable
{
    private readonly IDisposable _tracked;
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _completed;

    internal GitProcessSpan(string command, IDisposable tracked)
    {
        Command = command;
        _tracked = tracked;
    }

    internal string Command { get; }

    /// <summary>Records the finished process once; later calls are ignored.</summary>
    internal void Complete(int exitCode, bool timedOut = false)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        GitProcessTelemetry.Record(Command, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            exitCode, timedOut);
    }

    /// <summary>A span disposed without completion records the start as failed (exit -1).</summary>
    public void Dispose()
    {
        Complete(-1);
        _tracked.Dispose();
    }
}
