using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// The Compose-render step one gate run owes (AGT-2981): the triggering
/// paths, the commands it appends to the verify plan, the owed render scripts
/// the tested checkout lacks, and, when it owes nothing despite a trigger, why.
/// </summary>
public sealed record ComposeRenderScope(
    IReadOnlyList<string> Triggers,
    IReadOnlyList<VerifyCommand> Commands,
    string? CoveredReason = null)
{
    public static ComposeRenderScope None { get; } = new([], []);

    /// <summary>
    /// Owed render scripts absent from the tested checkout. Non-empty fails
    /// the gate closed; the commands are never dropped for a missing script.
    /// </summary>
    public IReadOnlyList<string> MissingScripts { get; init; } = [];

    /// <summary>True when the gate must render Compose on this host.</summary>
    public bool Required => Commands.Count > 0;
}

/// <summary>
/// Plans the diff-triggered Compose-render step for the in-process build/test
/// gate. The trigger paths, commands, and host requirement live in
/// <see cref="ComposeRenderGatePolicy"/> so the Remote Review and Remote Gate
/// planes derive exactly the same step.
/// </summary>
public static class ComposeRenderGate
{
    /// <summary>Coverage scope stamped on the appended commands.</summary>
    public const string TestScope = ComposeRenderGatePolicy.RequirementName;

    /// <summary>
    /// The step for this diff and checkout. An unknown diff triggers nothing:
    /// the step exists for a known Compose change, and the Remote Review plan
    /// is derived from the same delivery diff. A requirement listed in
    /// <paramref name="coveredRequirements"/> was already proved by the reused
    /// Remote Review verdict on a host that has it, so the step is not
    /// repeated here. Whether the repository owes the step is read from the
    /// tested <paramref name="workspace"/>, the <paramref name="repositoryPath"/>
    /// checkout, and the diff, so a delivery that deletes or renames the render
    /// scripts still owes them and fails closed on
    /// <see cref="ComposeRenderScope.MissingScripts"/>.
    /// </summary>
    public static ComposeRenderScope Plan(
        string workspace,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyCollection<string>? coveredRequirements,
        string? repositoryPath = null)
    {
        var triggers = ComposeRenderGatePolicy.Triggers(changedFiles);
        if (triggers.Count == 0) return ComposeRenderScope.None;
        if (coveredRequirements?.Contains(ComposeRenderGatePolicy.Requirement) == true)
        {
            return new ComposeRenderScope(triggers, [],
                "compose-render covered by the reused Remote Review verdict");
        }

        var reason = "compose-render: diff touches " + string.Join(", ", triggers);
        var commands = ComposeRenderGatePolicy.Commands([workspace, repositoryPath], changedFiles)
            .Select(command => new VerifyCommand(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", command)
            {
                Shell = VerifyCommandShell.Bash,
                TestScope = TestScope,
                SelectionReason = reason,
            })
            .ToArray();
        return commands.Length == 0
            ? new ComposeRenderScope(triggers, [], "compose-render scripts are not part of this repository")
            : new ComposeRenderScope(triggers, commands)
            {
                MissingScripts = ComposeRenderGatePolicy.MissingScripts(workspace),
            };
    }

    /// <summary>The verify commands followed by the render commands not already planned.</summary>
    public static IReadOnlyList<VerifyCommand> Append(
        IReadOnlyList<VerifyCommand> commands,
        ComposeRenderScope scope)
        => scope.Required
            ? commands.Concat(scope.Commands.Where(render => !commands.Any(command =>
                    string.Equals(command.Command, render.Command, StringComparison.Ordinal))))
                .ToList()
            : commands;

    /// <summary>Records the step's selection in the gate's selection audit.</summary>
    public static StagedVerifyPlan Annotate(StagedVerifyPlan staged, ComposeRenderScope scope)
    {
        if (scope.Triggers.Count == 0) return staged;
        var audit = staged.Audit;
        return staged with
        {
            Audit = audit with
            {
                SelectedCommands = audit.SelectedCommands
                    .Concat(scope.Commands.Select(TestSelectionPlanner.Describe))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Reasons = audit.Reasons
                    .Append(scope.CoveredReason
                            ?? "compose-render: diff touches " + string.Join(", ", scope.Triggers))
                    .ToArray(),
            },
        };
    }

    /// <summary>
    /// Fail-closed verdict for a delivery whose checkout lacks an owed render
    /// script. Charged to the delivery as a code failure: the script is part
    /// of the gate contract, and rerouting cannot restore it.
    /// </summary>
    public static BuildTestGateResult MissingScriptVerdict(ComposeRenderScope scope)
    {
        var reason = ComposeRenderGatePolicy.MissingScriptVerdict(scope.MissingScripts);
        return new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            null,
            0,
            "# " + reason,
            reason,
            false,
            false)
        {
            FailureKind = BuildTestGateFailureKind.Code,
            FailureFingerprint = BuildTestGateRunner.Fingerprint(
                BuildTestGateFailureKind.Code,
                ComposeRenderGatePolicy.MissingScriptsPrefix + ":" + string.Join(",", scope.MissingScripts)),
            Requirements = [ComposeRenderGatePolicy.Requirement],
        };
    }

    /// <summary>
    /// Fail-closed routing verdict for a gate host that cannot render Compose.
    /// Classified as an environment failure: the card is not charged, the
    /// coding reissue budget is untouched, and the delivery stays replayable
    /// once the gate runs on a host with the requirement.
    /// </summary>
    public static BuildTestGateResult HostVerdict(ComposeRenderScope scope)
    {
        var reason = ComposeRenderGatePolicy.HostVerdict(scope.Triggers);
        return new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            null,
            0,
            "# " + reason,
            reason,
            false,
            false)
        {
            FailureKind = BuildTestGateFailureKind.Environment,
            FailureFingerprint = BuildTestGateRunner.Fingerprint(
                BuildTestGateFailureKind.Environment,
                ComposeRenderGatePolicy.HostCannotRender),
            Requirements = [ComposeRenderGatePolicy.Requirement],
            UnmetRequirements = [ComposeRenderGatePolicy.Requirement],
        };
    }
}

/// <summary>
/// Whether this gate host can render Compose: a <c>docker</c> launcher on PATH
/// (with its Windows extensions) whose <c>docker compose version</c> exits 0.
/// No Docker daemon is contacted.
/// </summary>
public static class ComposeRenderHostProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        var docker = ResolveDocker();
        if (docker is null) return false;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = docker,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("compose");
            process.StartInfo.ArgumentList.Add("version");
            if (!process.Start()) return false;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException ex)
                {
                    SilentCatch.Note(ex, "ComposeRenderHostProbe: probe exited before the timeout kill");
                }
                return false;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static string? ResolveDocker()
    {
        // On Windows the launcher is docker.exe; a bare "docker" there is not
        // startable, so the PATHEXT names come first.
        var names = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => "docker" + extension.ToLowerInvariant())
                .ToArray()
            : ["docker"];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}
