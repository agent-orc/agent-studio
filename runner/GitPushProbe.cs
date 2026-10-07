namespace AgentRunner;

/// <summary>One startup proof that the daemon can publish run salvage before it accepts work.</summary>
public static class GitPushProbe
{
    public const string Ready = "ready";
    public const string ReadyNoWorkflowScope = "ready-no-workflow-scope";
    public const string ReadOnly = "read-only";
    public const string TokenRequirementsPath =
        "docs/operations/setup/linux-runner-host.md#token-requirements";

    public static GitPushProbeResult WithWorkspaceProof(
        GitPushProbeResult product, RepositoryAccessProof workspace)
        => workspace.Ready ? product : new(ReadOnly,
            $"Workspace repository access failed ({workspace.Status}); product fallback status was {product.Status}.");

    public static async Task<GitPushProbeResult> RunAsync(RunnerOptions options, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.GitRemote))
            return new(ReadOnly, "RUNNER_GIT_REMOTE is required for the startup push probe.");

        Directory.CreateDirectory(options.WorkDir);
        var probePath = Path.Combine(options.WorkDir, $".git-push-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var workflowProbeRef =
            $"refs/heads/runner-capability-probe/{GitWorkspace.SafeSegment(options.RunnerId)}/workflow-{Guid.NewGuid():N}";
        var workflowProbeMayExist = false;
        try
        {
            log($"runner-git-push-probe-started fetchRemote={options.GitRemote} pushRemote={(options.GitPushRemote ?? "same-as-fetch")}");
            var clone = await ProcessRunner.RunAsync("git", ["clone", "--no-checkout", "--depth", "1", options.GitRemote, probePath], options.WorkDir, ct: ct);
            if (!clone.Success) return Failed("fetch", clone);
            if (!string.IsNullOrWhiteSpace(options.GitPushRemote))
            {
                var pushUrl = await ProcessRunner.RunAsync("git", ["remote", "set-url", "--push", "origin", options.GitPushRemote], probePath, ct: ct);
                if (!pushUrl.Success) return Failed("pushurl", pushUrl);
            }

            var probeRef = $"refs/heads/runner-capability-probe/{GitWorkspace.SafeSegment(options.RunnerId)}";
            var push = await ProcessRunner.RunAsync("git", ["push", "--dry-run", "origin", $"HEAD:{probeRef}"], probePath, ct: ct);
            if (!push.Success) return Failed("push-dry-run", push);

            var checkout = await ProcessRunner.RunAsync(
                "git", ["checkout", "--detach", "HEAD"], probePath, ct: ct);
            if (!checkout.Success) return Failed("workflow-checkout", checkout);

            var workflowDirectory = Path.Combine(probePath, ".github", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            var workflowPath = Path.Combine(
                workflowDirectory,
                $"agent-studio-token-scope-probe-{Guid.NewGuid():N}.yml");
            await File.WriteAllTextAsync(
                workflowPath,
                """
                name: Agent Studio token scope probe
                on:
                  workflow_dispatch:
                jobs:
                  scope-probe:
                    if: ${{ false }}
                    runs-on: ubuntu-latest
                    steps:
                      - run: echo "scope probe"
                """,
                ct);
            var add = await ProcessRunner.RunAsync(
                "git", ["add", "--", ".github/workflows"], probePath, ct: ct);
            if (!add.Success) return Failed("workflow-add", add);
            var commit = await ProcessRunner.RunAsync(
                "git",
                [
                    "-c", "user.name=Agent Studio Runner",
                    "-c", "user.email=runner@agent-studio.invalid",
                    "commit", "-m", "chore(runner): probe workflow token scope [skip ci]"
                ],
                probePath,
                ct: ct);
            if (!commit.Success) return Failed("workflow-commit", commit);

            workflowProbeMayExist = true;
            var workflowPush = await ProcessRunner.RunAsync(
                "git", ["push", "origin", $"HEAD:{workflowProbeRef}"], probePath, ct: ct);
            if (!workflowPush.Success)
            {
                if (IsWorkflowScopeFailure(workflowPush))
                {
                    workflowProbeMayExist = false;
                    return new(
                        ReadyNoWorkflowScope,
                        WorkflowScopeFix(
                            $"contents push dry-run succeeded; workflow probe was rejected: {OneLine(workflowPush.StdErr)}"));
                }
                return Failed("workflow-push", workflowPush);
            }

            var delete = await ProcessRunner.RunAsync(
                "git", ["push", "origin", $":{workflowProbeRef}"], probePath, ct: ct);
            workflowProbeMayExist = !delete.Success;
            if (!delete.Success) return Failed("workflow-probe-cleanup", delete);

            return new(
                Ready,
                $"contents push dry-run succeeded for {probeRef}; workflow push succeeded and {workflowProbeRef} was deleted");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(ReadOnly, $"probe exception: {OneLine(ex.Message)}");
        }
        finally
        {
            if (workflowProbeMayExist && Directory.Exists(probePath))
            {
                var cleanup = await ProcessRunner.RunAsync(
                    "git", ["push", "origin", $":{workflowProbeRef}"], probePath, ct: CancellationToken.None);
                if (!cleanup.Success)
                    log($"runner-git-workflow-probe-cleanup-failed ref={workflowProbeRef} error={OneLine(cleanup.StdErr)}");
            }
            // probePath is a clone: read-only git objects defeat a plain delete.
            try { ResilientDirectory.Delete(probePath); }
            catch (Exception ex) { log($"runner-git-push-probe-cleanup-failed path={probePath} error={OneLine(ex.Message)}"); }
        }
    }

    private static GitPushProbeResult Failed(string stage, ProcessResult result)
        => new(ReadOnly, $"{stage} failed ({result.ExitCode}): {OneLine(result.StdErr)}");

    public static bool IsWorkflowScopeFailure(ProcessResult result)
        => result.ExitCode != 0 && IsWorkflowScopeFailure($"{result.StdErr}\n{result.StdOut}");

    public static bool IsWorkflowScopeFailure(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var mentionsWorkflowPath =
            value.Contains(".github/workflows", StringComparison.OrdinalIgnoreCase);
        var mentionsWorkflowScope =
            value.Contains("workflow scope", StringComparison.OrdinalIgnoreCase)
            || value.Contains("workflows permission", StringComparison.OrdinalIgnoreCase)
            || value.Contains("workflow permission", StringComparison.OrdinalIgnoreCase);
        var isRefusal =
            value.Contains("refusing to allow", StringComparison.OrdinalIgnoreCase)
            || value.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || value.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
            || value.Contains("denied", StringComparison.OrdinalIgnoreCase);
        return mentionsWorkflowScope || mentionsWorkflowPath && isRefusal;
    }

    public static string WorkflowScopeFix(string? prefix = null)
    {
        const string fix =
            "GitHub token can push repository contents but cannot modify .github/workflows. " +
            "Grant fine-grained Contents: Read and write plus Workflows: Read and write, " +
            "or classic repo plus workflow, update both credential URL forms, then restart the runner. " +
            $"See {TokenRequirementsPath}.";
        return string.IsNullOrWhiteSpace(prefix) ? fix : $"{prefix.Trim()} {fix}";
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record GitPushProbeResult(string Status, string Detail)
{
    public bool CanPush => Status is GitPushProbe.Ready or GitPushProbe.ReadyNoWorkflowScope;
    public bool CanPushWorkflows => Status == GitPushProbe.Ready;
}
