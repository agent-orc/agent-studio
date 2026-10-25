using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Runner;

/// <summary>Result of running one dry-run command.</summary>
public sealed record BuildCommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
    public string? FailureCode { get; init; }
}

/// <summary>
/// Runs a single shell command for the validation dry-run. Injected so the
/// orchestration in <see cref="BuildProfileValidationService"/> is testable
/// without spawning real package managers.
/// </summary>
public interface IBuildCommandRunner
{
    Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IBuildCommandRunner"/>: runs the command through git-bash
/// (<c>bash -lc</c>, located by <see cref="BashExecutable"/>) in the given
/// working directory, matching the repo's shell policy (sh, not PowerShell).
/// Captures combined stdout+stderr (tail-bounded).
/// </summary>
public sealed class ProcessBuildCommandRunner : IBuildCommandRunner
{
    private const int MaxCapturedChars = 8000;

    public async Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BashExecutable.Path,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi };
        var sb = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            return new BuildCommandResult(-1, $"failed to launch '{command}': {ex.Message}");
        }

        var output = sb.ToString();
        if (output.Length > MaxCapturedChars)
            output = output[^MaxCapturedChars..];
        return new BuildCommandResult(proc.ExitCode, output);
    }
}

/// <summary>Outcome of a full validation dry-run.</summary>
public sealed record DryRunValidationResult(bool Green, string Status, string Summary, string? FailedCommand)
{
    public string? FailureCode { get; init; }
}

/// <summary>Resolves local validation only to a real project source workspace.</summary>
public static class BuildProfileValidationWorkspace
{
    public static string? Resolve(WatchPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrWhiteSpace(entry.RepositoryPath)
            && Directory.Exists(entry.RepositoryPath))
            return Path.GetFullPath(entry.RepositoryPath);
        if (!string.IsNullOrWhiteSpace(entry.RootPath)
            && Directory.Exists(entry.RootPath))
            return Path.GetFullPath(entry.RootPath);
        return null;
    }
}

/// <summary>
/// Drives the onboarding validation dry-run (Slice P / ASS-1663): mark the
/// project's build profile <c>validating</c>, run the planned install + build
/// commands in order, stop at the first non-zero exit, then flip the profile to
/// <c>pipeline-ready</c> (green) or <c>validation-failed</c> (red). Only on green
/// does <see cref="BuildProfileGate"/> let the runner auto-pick the project.
///
/// <para>
/// The command execution is delegated to an injected <see cref="IBuildCommandRunner"/>
/// so the orchestration + status transitions are unit-testable without a real
/// package manager. The dry-run executes in the supplied working directory; the
/// endpoint resolves it from the project's registered RepositoryPath or
/// RootPath and never substitutes the task-store workspace.
/// </para>
/// </summary>
public sealed class BuildProfileValidationService
{
    private readonly ProjectSettingsService _settings;
    private readonly IBuildCommandRunner _runner;
    private readonly ILogger<BuildProfileValidationService> _logger;

    public BuildProfileValidationService(
        ProjectSettingsService settings,
        IBuildCommandRunner runner,
        ILogger<BuildProfileValidationService> logger)
    {
        _settings = settings;
        _runner = runner;
        _logger = logger;
    }

    public async Task<DryRunValidationResult> ValidateAsync(string projectName, string workingDir, CancellationToken ct)
    {
        var profile = _settings.Get(projectName).BuildProfile;
        if (profile is null)
            return new DryRunValidationResult(false, BuildProfileStatuses.Declared, "no build profile declared", null);

        var steps = BuildProfileDryRunPlanner.Plan(profile);
        if (steps.Count == 0)
        {
            // Nothing to install/build is treated as trivially green - the project
            // declared a profile with no commands, so there is nothing the dry-run
            // can disprove. Pipeline-ready opens the gate.
            _settings.MarkBuildProfileValidated(projectName);
            _logger.LogInformation("Build-profile validation for {Project}: no commands to run -> pipeline-ready", projectName);
            return new DryRunValidationResult(true, BuildProfileStatuses.PipelineReady, "no install/build commands; trivially green", null);
        }

        _settings.MarkBuildProfileValidating(projectName);
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Build-profile validation dry-run started for {Project}: {Count} step(s) in {Dir}",
            projectName, steps.Count, workingDir);

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _runner.RunAsync(workingDir, step.Command, ct);
            _logger.LogInformation("Build-profile dry-run {Kind} `{Command}` exited {Code} for {Project}",
                step.Kind, step.Command, result.ExitCode, projectName);
            if (!result.Succeeded)
            {
                var tail = Tail(result.Output);
                var error = $"{step.Kind.ToString().ToLowerInvariant()} step `{step.Command}` exited {result.ExitCode}{(string.IsNullOrWhiteSpace(tail) ? "" : $": {tail}")}";
                _settings.MarkBuildProfileValidationFailed(projectName, error);
                _logger.LogWarning("Build-profile validation FAILED for {Project} after {Elapsed}ms: {Error}",
                    projectName, sw.ElapsedMilliseconds, error);
                return new DryRunValidationResult(false, BuildProfileStatuses.ValidationFailed, error, step.Command)
                {
                    FailureCode = result.FailureCode
                };
            }
        }

        _settings.MarkBuildProfileValidated(projectName);
        _logger.LogInformation("Build-profile validation GREEN for {Project} after {Elapsed}ms ({Count} step(s)) -> pipeline-ready",
            projectName, sw.ElapsedMilliseconds, steps.Count);
        return new DryRunValidationResult(true, BuildProfileStatuses.PipelineReady,
            $"{steps.Count} step(s) green in {sw.ElapsedMilliseconds}ms", null);
    }

    private static string Tail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join(" | ", lines[^Math.Min(3, lines.Length)..]);
        return tail.Length > 300 ? tail[^300..] : tail;
    }
}
