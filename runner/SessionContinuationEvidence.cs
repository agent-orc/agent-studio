using System.Text.Json;
using AgentStudio.CliHosting;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal static class SessionContinuationEvidence
{
    internal static SessionContinuationLedgerEntry Build(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        WorktreeTeardownResult teardown,
        string host,
        string provider)
    {
        var usage = ReadWorkerEvidence(slot.WorkerDirectory,
            slot.InputSessionId is null ? 0 : slot.PreviousSession?.TotalTokens);
        bool hasHome;
        string? home;
        try { hasHome = TaskCleanContextStore.TryGetExistingHome(provider, slot.TaskKey, out home); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            hasHome = false;
            home = null;
        }
        var resumed = slot.InputSessionId is not null;
        var result = DurableAgentProcess.Attach(slot).ReadResult();
        var startedAt = slot.ProcessStartedAtUtc ?? slot.Lease.AcquiredAt;
        var finishedAt = result?.CompletedAtUtc ?? DateTime.UtcNow;
        return new SessionContinuationLedgerEntry(
            slot.AttemptId,
            slot.TaskKey,
            provider,
            workspace.RepositoryUrl ?? string.Empty,
            workspace.RepoPath,
            workspace.WorkBranch,
            teardown.ImmutableResultRef ?? teardown.Branch,
            teardown.ResultSha,
            hasHome ? host + "|" + home : null,
            slot.InputSessionId,
            usage.SessionId,
            slot.ResumeDecision ?? "fresh-run",
            slot.ResumeRejectionReason,
            slot.MechanicalDelta is not null,
            Math.Min(1, (slot.PreviousSession?.MechanicalResumesUsed ?? 0) + (resumed ? 1 : 0)),
            null,
            null,
            null,
            usage.TotalTokens,
            Math.Max(0, (finishedAt - startedAt).TotalSeconds),
            DateTime.UtcNow);
    }

    internal static (string? SessionId, long? TotalTokens) ReadWorkerEvidence(
        string workerDirectory, long? priorSessionTokens = 0)
    {
        var root = Path.GetFileName(workerDirectory).StartsWith("resume-", StringComparison.Ordinal)
            ? Path.GetDirectoryName(workerDirectory)! : workerDirectory;
        if (!Directory.Exists(root)) return (null, null);
        string? sessionId = null;
        long turns = 0;
        long cumulative = 0;
        long? baseline = priorSessionTokens;
        var observed = false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "output.jsonl", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var output = JsonSerializer.Deserialize<DetachedJobLogLine>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (output?.Stream != "stdout") continue;
                    sessionId = ProviderOutputEvidenceExtractor.Extract(output.Text).SessionId ?? sessionId;
                    if (MechanicalTokenUsage.TryRead(output.Text, out var tokens, out var isCumulative))
                    {
                        observed = true;
                        if (isCumulative)
                        {
                            baseline ??= tokens;
                            cumulative = Math.Max(cumulative, tokens - baseline.Value);
                        }
                        else turns += tokens;
                    }
                }
                catch (JsonException) { /* An incomplete final line cannot create usage. */ }
            }
        }
        catch (IOException) { return (sessionId, observed ? Math.Max(turns, cumulative) : null); }
        catch (UnauthorizedAccessException) { return (sessionId, observed ? Math.Max(turns, cumulative) : null); }
        return (sessionId, observed ? Math.Max(turns, cumulative) : null);
    }
}
