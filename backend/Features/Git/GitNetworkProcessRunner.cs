using System.ComponentModel;
using System.Diagnostics;

namespace AgentStudio.Git;

internal enum GitProcessFailureKind
{
    None,
    TimedOut,
    Cancelled,
    StartFailure,
    ResourceExhaustion,
}

internal sealed record GitProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    GitProcessFailureKind FailureKind)
{
    public bool Success => FailureKind == GitProcessFailureKind.None && ExitCode == 0;
}

/// <summary>
/// Owns bounded backend Git child-process execution. Output pipes are drained
/// concurrently, and every timeout or cancellation terminates the full process
/// tree before returning. This prevents a DNS or remote stall from retaining
/// Git processes and their inherited pipe handles for the backend lifetime.
/// </summary>
internal static class GitNetworkProcessRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminationDrainTimeout = TimeSpan.FromSeconds(3);

    internal static GitProcessResult Run(
        ProcessStartInfo startInfo,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ApplyWindowsLongPathConfig(startInfo);
        var boundedTimeout = timeout ?? DefaultTimeout;
        if (boundedTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Git process timeout must be positive.");
        if (cancellationToken.IsCancellationRequested)
            return Failed(GitProcessFailureKind.Cancelled, "git operation cancelled");

        Process? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        Task? input = null;
        var elapsed = Stopwatch.StartNew();

        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return Failed(GitProcessFailureKind.StartFailure, "git process did not start");
            GitBackgroundPriority.Apply(process);

            // GitService is predominantly synchronous. Blocking that path on
            // RunAsync().GetResult() made process-exit and pipe continuations
            // compete with parallel xUnit/Kestrel work for ThreadPool threads.
            // Dedicated readers preserve concurrent pipe draining without any
            // ThreadPool continuation dependency in the synchronous boundary.
            stdout = StartDedicatedRead(process.StandardOutput);
            stderr = StartDedicatedRead(process.StandardError);
            if (stdin is not null)
                input = StartDedicatedWrite(process.StandardInput, stdin);

            while (!process.WaitForExit(WaitSliceMilliseconds))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TerminateAndDrain(process, stdout, stderr, input);
                    return Failed(GitProcessFailureKind.Cancelled, "git operation cancelled");
                }
                if (elapsed.Elapsed >= boundedTimeout)
                {
                    TerminateAndDrain(process, stdout, stderr, input);
                    return Failed(
                        GitProcessFailureKind.TimedOut,
                        $"git operation timed out after {boundedTimeout.TotalSeconds:0.###} seconds");
                }
            }

            var remaining = boundedTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero
                || !WaitForIo([stdout, stderr, input], remaining, cancellationToken))
            {
                TerminateAndDrain(process, stdout, stderr, input);
                if (cancellationToken.IsCancellationRequested)
                    return Failed(GitProcessFailureKind.Cancelled, "git operation cancelled");
                return Failed(
                    GitProcessFailureKind.TimedOut,
                    $"git operation timed out after {boundedTimeout.TotalSeconds:0.###} seconds");
            }

            return new GitProcessResult(
                process.ExitCode,
                stdout.GetAwaiter().GetResult(),
                stderr.GetAwaiter().GetResult(),
                GitProcessFailureKind.None);
        }
        catch (Exception ex)
        {
            TerminateAndDrain(process, stdout, stderr, input);
            var exhausted = IsResourceExhaustion(ex);
            return Failed(
                exhausted ? GitProcessFailureKind.ResourceExhaustion : GitProcessFailureKind.StartFailure,
                exhausted
                    ? $"git process resource exhaustion: {ex.Message}"
                    : $"git process failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static async Task<GitProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ApplyWindowsLongPathConfig(startInfo);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Git process timeout must be positive.");
        if (cancellationToken.IsCancellationRequested)
            return Failed(GitProcessFailureKind.Cancelled, "git operation cancelled");

        Process? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        try
        {
            process = startProcess is null
                ? Process.Start(startInfo)
                : startProcess(startInfo);
            if (process is null)
                return Failed(GitProcessFailureKind.StartFailure, "git process did not start");

            stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            stderr = process.StandardError.ReadToEndAsync(bounded.Token);

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), bounded.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(bounded.Token).ConfigureAwait(false);
            return new GitProcessResult(
                process.ExitCode,
                stdout.Result,
                stderr.Result,
                GitProcessFailureKind.None);
        }
        catch (OperationCanceledException)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            return Failed(
                cancelled ? GitProcessFailureKind.Cancelled : GitProcessFailureKind.TimedOut,
                cancelled
                    ? "git operation cancelled"
                    : $"git operation timed out after {timeout.TotalSeconds:0.###} seconds");
        }
        catch (Exception ex)
        {
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            var exhausted = IsResourceExhaustion(ex);
            return Failed(
                exhausted ? GitProcessFailureKind.ResourceExhaustion : GitProcessFailureKind.StartFailure,
                exhausted
                    ? $"git process resource exhaustion: {ex.Message}"
                    : $"git process failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Every backend git spawn on Windows runs with <c>core.longpaths=true</c>
    /// (injected as command-line config via the <c>GIT_CONFIG_*</c> environment,
    /// appended after any pairs the caller already set). Without it, Git for
    /// Windows enforces MAX_PATH on every file it touches, and the runner's
    /// long refs break real operations: fetching a collision branch
    /// (<c>runner/&lt;host&gt;/&lt;key&gt;-collision-&lt;sha&gt;-&lt;sha&gt;</c>)
    /// into <c>refs/remotes/origin/...</c> of an ordinary checkout dies with
    /// "cannot lock ref ... Filename too long", and even reading such a ref back
    /// fails - which turned a provably delivered AGT-2494 salvage into an
    /// escalated "unverified delivery". The refs the flag lets git write are
    /// long-path files that non-long-path tools on the same checkout may not
    /// handle, which is the lesser evil against delivery verification failing
    /// outright; retention prunes those refs. No-op off Windows.
    /// </summary>
    private static void ApplyWindowsLongPathConfig(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows()) return;

        var count = 0;
        if (startInfo.Environment.TryGetValue("GIT_CONFIG_COUNT", out var existing)
            && int.TryParse(existing, out var parsed)
            && parsed > 0)
        {
            count = parsed;
        }

        startInfo.Environment[$"GIT_CONFIG_KEY_{count}"] = "core.longpaths";
        startInfo.Environment[$"GIT_CONFIG_VALUE_{count}"] = "true";
        startInfo.Environment["GIT_CONFIG_COUNT"] =
            (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static GitProcessResult Failed(GitProcessFailureKind kind, string error)
        => new(-1, string.Empty, error, kind);

    private const int WaitSliceMilliseconds = 25;

    private static Task<string> StartDedicatedRead(StreamReader reader)
        => Task.Factory.StartNew(
            reader.ReadToEnd,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static Task StartDedicatedWrite(StreamWriter writer, string value)
        => Task.Factory.StartNew(
            () =>
            {
                writer.Write(value);
                writer.Close();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static bool WaitForIo(
        IEnumerable<Task?> tasks,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var pending = tasks.OfType<Task>().ToArray();
        if (pending.Length == 0) return true;

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            var remainingMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(Math.Min(
                    WaitSliceMilliseconds,
                    (timeout - elapsed.Elapsed).TotalMilliseconds)));
            if (Task.WaitAll(pending, remainingMilliseconds)) return true;
        }
        return false;
    }

    private static void TerminateAndDrain(
        Process? process,
        Task<string>? stdout,
        Task<string>? stderr,
        Task? input)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: synchronous process-tree termination");
        }

        try
        {
            process.WaitForExit((int)TerminationDrainTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: synchronous exit wait");
        }

        try
        {
            WaitForIo([stdout, stderr, input], TerminationDrainTimeout);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: bounded synchronous post-termination drain");
        }
    }

    private static async Task TerminateAndDrainAsync(
        Process? process,
        Task<string>? stdout,
        Task<string>? stderr)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: process-tree termination");
        }

        var drains = new List<Task>(3);
        try { drains.Add(process.WaitForExitAsync(CancellationToken.None)); }
        catch (Exception ex) { SilentCatch.Note(ex, "GitNetworkProcessRunner: exit wait setup"); }
        if (stdout is not null) drains.Add(stdout);
        if (stderr is not null) drains.Add(stderr);
        if (drains.Count == 0) return;

        try
        {
            await Task.WhenAll(drains)
                .WaitAsync(TerminationDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: bounded post-termination drain");
        }
    }

    private static bool IsResourceExhaustion(Exception exception)
    {
        if (exception is OutOfMemoryException) return true;
        if (exception is Win32Exception win32
            && win32.NativeErrorCode is 4 or 8 or 14 or 18 or 23 or 24)
            return true;

        var message = exception.Message;
        return message.Contains("too many open files", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("resource temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("insufficient system resources", StringComparison.OrdinalIgnoreCase);
    }
}
