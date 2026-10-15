using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// AGT-3063: polls shadow orchestrator decision jobs independently of coding
/// admission. A decision call is one bounded read-only turn, so the host runs
/// one at a time; the provider credential never leaves this host (AGT-W63).
/// </summary>
internal sealed class OrchestratorDecisionJobAdmission
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly Func<CancellationToken, Task<OrchestratorDecisionJobClaimResponse>> _claim;
    private readonly Func<OrchestratorDecisionJobWork, CancellationToken, Task<int>> _execute;
    private readonly Action<string> _log;

    public OrchestratorDecisionJobAdmission(RunnerOptions options, TaskServerClient client, Action<string> log)
        : this(
            ct => client.ClaimOrchestratorDecisionJobAsync(
                new OrchestratorDecisionJobClaimRequest(options.RunnerId, options.RunnerName), ct),
            (work, ct) => new OrchestratorDecisionJobRunner(options, client, log).RunAsync(work, ct),
            log) { }

    internal OrchestratorDecisionJobAdmission(
        Func<CancellationToken, Task<OrchestratorDecisionJobClaimResponse>> claim,
        Func<OrchestratorDecisionJobWork, CancellationToken, Task<int>> execute,
        Action<string> log)
    {
        _claim = claim;
        _execute = execute;
        _log = log;
    }

    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (!await PollOnceAsync(shutdown))
                    await Task.Delay(PollInterval, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log($"orchestrator-decision-poll-failed error={ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(PollInterval, shutdown); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>True when a job was claimed and executed.</summary>
    internal async Task<bool> PollOnceAsync(CancellationToken shutdown)
    {
        var claim = await _claim(shutdown);
        if (claim.Status != DecisionJobClaimStatuses.Claimed || claim.Work is null) return false;
        _log($"claimed orchestrator decision job {claim.Work.JobId} cli={claim.Work.Cli} model={claim.Work.Model}");
        var exitCode = await _execute(claim.Work, shutdown);
        _log($"orchestrator decision job {claim.Work.JobId} completed with exit code {exitCode}");
        return true;
    }
}

/// <summary>Runs one claimed decision job in an empty read-only working directory.</summary>
internal sealed class OrchestratorDecisionJobRunner(RunnerOptions options, TaskServerClient client, Action<string> log)
{
    public async Task<int> RunAsync(OrchestratorDecisionJobWork work, CancellationToken shutdown)
    {
        var startedAt = DateTime.UtcNow;
        // The model reads only the frozen grounds in its prompt, never a checkout.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "agent-decision", work.JobId);
        Directory.CreateDirectory(workingDirectory);
        var cli = string.IsNullOrWhiteSpace(work.Cli) ? "codex" : work.Cli.Trim().ToLowerInvariant();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, work.TimeoutSeconds)));
            var process = await ReadOnlyCarTurn.RunAsync(options, work.JobId, cli, work.Prompt,
                workingDirectory, work.Model, work.ThinkingLevel, "agent-decision-car",
                "orchestrator-decision", null, log, timeout.Token, shutdown);
            var parsed = cli == "claude"
                ? RemoteProjectChatRunner.ParseClaude(process, work.Model)
                : RemoteProjectChatRunner.ParseCodex(process, work.Model);
            return await CompleteAsync(work, Completion(work, options.RunnerId, cli, parsed, startedAt,
                DateTime.UtcNow), shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return await CompleteAsync(work, Failure(work, options.RunnerId, startedAt,
                $"decision call exceeded {work.TimeoutSeconds} s", transport: false), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log($"orchestrator-decision-failed job={work.JobId} error={ex.GetType().Name}: {ex.Message}");
            return await CompleteAsync(work, Failure(work, options.RunnerId, startedAt,
                ex.Message, transport: true), CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The CLI session or thread id identifies the provider call, so a repeated
    /// report of the same turn cannot count twice. A failure without any usage
    /// frame never reached a model response and counts as a transport failure.
    /// </summary>
    internal static OrchestratorDecisionJobCompletion Completion(OrchestratorDecisionJobWork work,
        string runnerId, string cli, RemoteProjectChatResult parsed, DateTime startedAt, DateTime endedAt)
    {
        var usage = parsed.TokenUsage;
        IReadOnlyList<DecisionUsageFrameV1> frames = usage is null ? [] :
        [
            new DecisionUsageFrameV1(
                parsed.ProviderThreadId is { Length: > 0 } thread ? $"{cli}:{thread}" : $"decision-job:{work.JobId}",
                usage.Model ?? work.Model, usage.InputTokens, usage.CacheReadTokens, usage.CacheCreationTokens,
                usage.OutputTokens, InputIncludesCached: false),
        ];
        return new(work.JobId, work.ClaimToken, runnerId, parsed.Success, parsed.ReplyText,
            usage?.Model ?? work.Model, frames, startedAt, endedAt, parsed.ErrorMessage,
            TransportFailure: !parsed.Success && usage is null);
    }

    private static OrchestratorDecisionJobCompletion Failure(OrchestratorDecisionJobWork work, string runnerId,
        DateTime startedAt, string error, bool transport)
        => new(work.JobId, work.ClaimToken, runnerId, false, null, work.Model, [], startedAt, DateTime.UtcNow,
            error, transport);

    private async Task<int> CompleteAsync(OrchestratorDecisionJobWork work,
        OrchestratorDecisionJobCompletion completion, CancellationToken ct)
    {
        if (!await client.CompleteOrchestratorDecisionJobAsync(completion, ct))
        {
            log($"orchestrator-decision-completion-rejected job={work.JobId} reason=stale-claim");
            return 3;
        }
        return completion.Success ? 0 : 1;
    }
}
