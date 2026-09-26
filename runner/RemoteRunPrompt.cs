namespace AgentRunner;

/// <summary>
/// Builds the prompt handed to a standalone remote-runner CLI.
/// <para>
/// The task server deliberately exposes the operator-authored <c>prompt.md</c>
/// verbatim. The local in-process runner adds standing model-routing and
/// contribution guidance plus its completion protocol while it renders
/// <c>runner-fresh-start.md</c>, so the standalone runner must add the same
/// instructions at its own execution boundary. Keeping them here makes one-shot
/// and daemon-claimed runs use exactly the same prompt.
/// </para>
/// </summary>
public static class RemoteRunPrompt
{
    private const string FollowUpMarkerPrefix = "agent-studio-follow-up-claim-sha256:";

    /// <summary>
    /// Adds a claimed follow-up as an identity-marked prompt block. Authored
    /// task text is deliberately not used for deduplication: a short follow-up
    /// such as "continue" may already occur in the task without having been
    /// delivered by this claim.
    /// </summary>
    public static ClaimedFollowUpApplication ApplyClaimedFollowUp(
        string taskPrompt,
        AgentStudio.TaskServer.Contracts.FollowUpDeliveryDto? followUp)
    {
        ArgumentNullException.ThrowIfNull(taskPrompt);
        if (followUp is null)
            return new ClaimedFollowUpApplication(taskPrompt, null);

        var block = ClaimedFollowUpBlock(followUp);
        if (taskPrompt.Contains(block, StringComparison.Ordinal))
            return new ClaimedFollowUpApplication(taskPrompt, followUp);

        var marker = ClaimedFollowUpMarker(followUp);
        if (taskPrompt.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Claimed follow-up '{followUp.ClaimId ?? followUp.PromptSha256}' has an incomplete prompt block.");
        }

        var composed = taskPrompt.TrimEnd()
            + Environment.NewLine + Environment.NewLine
            + "---" + Environment.NewLine + Environment.NewLine
            + block;
        return new ClaimedFollowUpApplication(
            composed,
            composed.Contains(block, StringComparison.Ordinal) ? followUp : null);
    }

    internal static bool ContainsClaimedFollowUp(
        string prompt,
        AgentStudio.TaskServer.Contracts.FollowUpDeliveryDto followUp)
        => prompt.Contains(ClaimedFollowUpBlock(followUp), StringComparison.Ordinal);

    private static string ClaimedFollowUpBlock(
        AgentStudio.TaskServer.Contracts.FollowUpDeliveryDto followUp)
    {
        var marker = ClaimedFollowUpMarker(followUp);
        return $"<!-- {marker} -->" + Environment.NewLine
            + $"## Follow-up for this run ({followUp.Mode})" + Environment.NewLine + Environment.NewLine
            + followUp.Prompt + Environment.NewLine
            + $"<!-- /{marker} -->" + Environment.NewLine;
    }

    private static string ClaimedFollowUpMarker(
        AgentStudio.TaskServer.Contracts.FollowUpDeliveryDto followUp)
    {
        var identity = string.IsNullOrWhiteSpace(followUp.ClaimId)
            ? $"legacy:{followUp.PromptSha256}"
            : followUp.ClaimId.Trim();
        return FollowUpMarkerPrefix
            + AgentStudio.TaskServer.Contracts.FollowUpPromptDigest.Compute(identity);
    }

    public const string ModelRoutingPolicyInstruction =
        "Consult `docs/system/domains/model-routing-policy.md` as the authoritative source whenever " +
        "you select, recommend, override, or explain a model and thinking level. Never let quota or " +
        "cost cross its correctness-risk floors.";

    public const string ContributionGuideInstruction =
        "Consult `docs/start/contribution-and-style-guide.html` and treat it as the authoritative " +
        "source for contribution and style conventions.";

    public const string CompletionProtocol =
        "Orchestrator note: your reply MUST end with exactly one of " +
        "`[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, " +
        "`[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]` as the final line. " +
        "Replace the example reason with the actual short reason; never emit the example text unchanged. " +
        "This is required, not optional. The orchestrator parses this token; " +
        "without it the run lands in review as missing-terminal-sentinel.";

    public static string Build(string taskPrompt) =>
        Build(taskPrompt, modeFraming: null, resultsDirectory: null);

    /// <summary>
    /// Builds the remote prompt with the server-composed per-mode framing block
    /// between the task body and the standing instructions. Prompt enrichment is
    /// one marked block inside this framing value, rather than a parallel prompt
    /// argument or a separately mutable runner-side channel.
    /// </summary>
    public static string Build(
        string taskPrompt,
        string? modeFraming,
        string? resultsDirectory = null,
        ArtifactTransferLimitsResponse? artifactLimits = null)
    {
        ArgumentNullException.ThrowIfNull(taskPrompt);
        var framingBlock = string.IsNullOrWhiteSpace(modeFraming)
            ? string.Empty
            : modeFraming.Trim() + Environment.NewLine + Environment.NewLine;
        var resultsBlock = string.IsNullOrWhiteSpace(resultsDirectory)
            ? string.Empty
            : "Run context: result files (reports, screenshots, evidence - e.g. `results/report.html`) must be "
              + $"written into the absolute directory `{resultsDirectory.Trim()}` (also exported as the "
              + "`JOB_RESULTS_DIR` environment variable). Only files in that directory are collected and shipped "
              + "to the reviewer; a relative `results/` path inside the repository checkout is NOT collected and "
              + "is discarded with the temporary worktree."
              + (artifactLimits is null
                  ? string.Empty
                  : $" Keep each result file at or below {ArtifactTransferPolicy.FormatMb(artifactLimits.MaxFileBytes)} MB "
                    + $"and all result files at or below {ArtifactTransferPolicy.FormatMb(artifactLimits.MaxTotalBytes)} MB total. "
                    + "Playwright traces and videos are not kept unless the task explicitly asks for them; "
                    + "do not copy node_modules or bin/obj output into results.")
              + Environment.NewLine + Environment.NewLine;
        return taskPrompt.TrimEnd() + Environment.NewLine + Environment.NewLine
            + "---" + Environment.NewLine + Environment.NewLine
            + framingBlock
            + resultsBlock
            + ModelRoutingPolicyInstruction + Environment.NewLine + Environment.NewLine
            + ContributionGuideInstruction + Environment.NewLine + Environment.NewLine
            + CompletionProtocol + Environment.NewLine;
    }
}

public sealed record ClaimedFollowUpApplication(
    string Prompt,
    AgentStudio.TaskServer.Contracts.FollowUpDeliveryDto? AcknowledgedFollowUp)
{
    public bool ShouldAcknowledge => AcknowledgedFollowUp is not null;
}
