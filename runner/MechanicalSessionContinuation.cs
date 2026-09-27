using AgentStudio.CliHosting;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal static class MechanicalSessionContinuation
{
    internal static async Task<(string? SessionId, string? Reason, string? DeltaPrompt)> DecideAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        string provider,
        string? contextMode,
        string host,
        CancellationToken ct)
    {
        var delta = slot.MechanicalDelta is null ? null
            : slot.MechanicalDelta with
            { BaseSha = workspace.BaseSha ?? slot.MechanicalDelta.BaseSha };
        if (delta is null) return (null, null, null);
        if (!string.Equals(contextMode, "clean", StringComparison.OrdinalIgnoreCase))
            return (null, "context-mode-mismatch", null);
        bool homeExists;
        string? home;
        try { homeExists = TaskCleanContextStore.TryGetExistingHome(provider, slot.TaskKey, out home); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, "clean-context-unavailable", null);
        }
        var identity = homeExists ? host + "|" + home : null;
        var sessionId = slot.PreviousSession?.CapturedSessionId;
        var sessionPresent = homeExists && SessionExists(home!, sessionId);
        var reason = MechanicalSessionResumePolicy.RejectionReason(
            slot.PreviousSession,
            delta,
            slot.TaskKey,
            provider,
            workspace.RepositoryUrl ?? string.Empty,
            workspace.RepoPath,
            workspace.WorkBranch,
            identity,
            sessionPresent);
        if (reason is not null) return (null, reason, null);
        if (string.IsNullOrWhiteSpace(workspace.BaseSha)) return (null, "missing-current-base", null);
        ProcessResult remote;
        try
        {
            remote = await ProcessRunner.RunAsync(
                "git", ["ls-remote", "origin", delta.DeliveryRef], workspace.RepoPath, ct: ct);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return (null, "branch-lineage-unavailable", null);
        }
        var remoteSha = remote.Success
            ? remote.StdOut.Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        if (!string.Equals(remoteSha, delta.DeliverySha, StringComparison.OrdinalIgnoreCase))
            return (null, "branch-lineage-mismatch", null);
        var prompt = $"Continue the same task's integration recovery. Current base SHA: {delta.BaseSha}. "
            + $"Delivery: {delta.DeliveryRef} at {delta.DeliverySha}. "
            + $"Conflict paths: {(delta.ConflictPaths.Count == 0 ? "none recorded" : string.Join(", ", delta.ConflictPaths))}.\n\n"
            + delta.Steer.Trim() + "\n\nVerification plan: " + delta.VerificationPlan.Trim()
            + "\nFinish with the required task terminal sentinel.";
        return (sessionId, null, prompt);
    }

    private static bool SessionExists(string home, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Length > 128
            || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        try
        {
            return Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories)
                .Any(path => Path.GetFileName(path).Contains(sessionId, StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
