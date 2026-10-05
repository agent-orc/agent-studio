using System.Diagnostics;
using System.Text;

namespace AgentStudio.Pipeline;

/// <summary>
/// Verdict returned by <see cref="LintScssRunner"/> for one pipeline run.
/// </summary>
public enum LintScssVerdict
{
    /// <summary>Step was skipped (mode = off or no <c>frontend/</c>).</summary>
    Skipped,
    /// <summary>stylelint returned exit 0.</summary>
    Ok,
    /// <summary>stylelint returned non-zero, but mode != fail so no reissue.</summary>
    Warn,
    /// <summary>stylelint returned non-zero and mode = fail; caller should reissue.</summary>
    Fail,
}

/// <summary>
/// Outcome of a single <see cref="LintScssRunner.RunAsync"/> call. The
/// <see cref="Output"/> field carries the first ~50 lines of stylelint
/// stdout/stderr (truncated) and is the body of the post-step log file.
/// <see cref="ExitCode"/> is null when the runner never managed to start
/// the subprocess (missing <c>npx</c>, missing <c>frontend/</c>, etc.).
/// </summary>
public sealed record LintScssResult(
    LintScssVerdict Verdict,
    int? ExitCode,
    long DurationMs,
    string Output,
    string Reason)
{
    public string? InfrastructureFailureCode { get; init; }
}

/// <summary>
/// Deterministic post-step that runs <c>npx stylelint</c> against the
/// repository's <c>frontend/</c> tree. Lives behind a small interface so
/// tests can substitute a fake runner without spawning a process; the
/// concrete implementation always shells out to <c>npx</c> so the lint
/// behaviour matches what the contributor sees from
/// <c>npm run lint:scss</c>.
/// </summary>
public interface ILintScssRunner
{
    Task<LintScssResult> RunAsync(
        string repositoryPath,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Production implementation: shells out to <c>npx stylelint</c> from
/// <c>{repositoryPath}/frontend</c>. The post-step gate enforces the
/// same rules as the developer's <c>npm run lint:scss</c> script, so a
/// fail here matches a fail in local dev exactly.
///
/// <para>
/// The runner is intentionally coarse: it lints the entire SCSS tree
/// every time the agent run finishes rather than try to detect which
/// files the agent touched. Two reasons. First, <c>stylelint</c> on the
/// ~95-file SCSS tree is sub-15 seconds even on cold caches, which is
/// noise next to the ~30-60s aspect Claude calls that ran just before
/// this step. Second, the gate is meant to catch hex/rgba regressions in
/// files the agent did NOT directly touch (e.g. a token rename that
/// breaks a sibling component); a file-scoped lint would silently miss
/// those. ASS-92 + ASS-563.
/// </para>
/// </summary>
public sealed class LintScssRunner : ILintScssRunner
{
    /// <summary>
    /// Maximum lines of stylelint output captured in
    /// <see cref="LintScssResult.Output"/>. Matches the spec's "first 50
    /// lines" of the truncated log so the timeline event stays readable.
    /// The full output is preserved in the post-step log file written by
    /// the orchestrator (this runner returns the truncated form).
    /// </summary>
    public const int MaxOutputLines = 50;

    private readonly ILogger<LintScssRunner> _logger;

    public LintScssRunner(ILogger<LintScssRunner> logger)
    {
        _logger = logger;
    }

    public async Task<LintScssResult> RunAsync(
        string repositoryPath,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (mode == PostStepMode.Off)
        {
            return new LintScssResult(LintScssVerdict.Skipped, null, 0, "", "mode=off");
        }

        var frontend = FrontendStylelintCommand.ResolveWorkspace(repositoryPath);
        if (frontend is null)
        {
            // Watched projects without a frontend tree (e.g. pure-CLI
            // tooling repos) skip silently; the post-step is a no-op for
            // them rather than a permanent failure.
            return new LintScssResult(LintScssVerdict.Skipped, null, 0, "",
                $"no frontend/ or root Angular workspace at {repositoryPath}");
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, output) = await InvokeStylelintAsync(frontend, timeout, ct);
        sw.Stop();

        if (exitCode == null)
        {
            // Could not start the process (npx missing, frontend tree
            // broken, etc.). Treat as "skipped with reason" so a broken
            // local toolchain never blocks the auto-review pipeline.
            _logger.LogWarning(
                "LintScssRunner: failed to invoke stylelint at {Frontend} (duration={Ms}ms)",
                frontend, sw.ElapsedMilliseconds);
            return new LintScssResult(LintScssVerdict.Skipped, null, sw.ElapsedMilliseconds, output,
                "stylelint did not run (npx unavailable or process start failed)");
        }

        if (exitCode == 0)
        {
            return new LintScssResult(LintScssVerdict.Ok, exitCode, sw.ElapsedMilliseconds,
                "stylelint passed", "0 errors");
        }

        var verdict = mode == PostStepMode.Fail ? LintScssVerdict.Fail : LintScssVerdict.Warn;
        return new LintScssResult(verdict, exitCode, sw.ElapsedMilliseconds, output,
            $"stylelint exit {exitCode}");
    }

    private async Task<(int? ExitCode, string Output)> InvokeStylelintAsync(
        string frontendDir,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            // On Windows, "npx" is a shim shell-script and won't start
            // directly via Process.Start. Use the shell so the platform
            // resolves the right executable (cmd /c npx on Windows,
            // /bin/sh -c npx everywhere else).
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = frontendDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        AddStylelintArguments(psi, OperatingSystem.IsWindows());

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LintScssRunner: Process.Start failed");
            return (null, ex.Message);
        }
        if (p == null) return (null, "Process.Start returned null");

        var outputBuilder = new StringBuilder();
        var lineCount = 0;
        var lineLock = new object();
        void AppendLine(string? line)
        {
            if (line == null) return;
            lock (lineLock)
            {
                if (lineCount >= MaxOutputLines) return;
                outputBuilder.AppendLine(line);
                lineCount++;
            }
        }

        p.OutputDataReceived += (_, e) => AppendLine(e.Data);
        p.ErrorDataReceived += (_, e) => AppendLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, outputBuilder.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "LintScssRunner: best effort"); /* best effort */ }
            return (null, $"stylelint timed out after {timeout.TotalSeconds:F0}s");
        }
    }

    internal static void AddStylelintArguments(ProcessStartInfo psi, bool isWindows)
    {
        if (isWindows)
        {
            // Keep every token separate. Passing the entire command as one
            // ArgumentList item makes ProcessStartInfo quote it for cmd.exe,
            // which turns the glob into the literal ""src/**/*.scss"".
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("npx");
            psi.ArgumentList.Add("stylelint");
            psi.ArgumentList.Add("src/**/*.scss");
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("npx stylelint \"src/**/*.scss\"");
        }
    }
}
