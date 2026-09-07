using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public enum ExecutionAttemptKind
{
    Coding,
    Review,
}

public enum ExecutionOutcomeKind
{
    AuthenticationFailure,
    QuotaExceeded,
    InvalidModelOrConfiguration,
    LaunchFailure,
    CliCrash,
    Timeout,
    OutOfMemory,
    TransportLoss,
    HostShutdown,
    LeaseLoss,
    OperatorCancellation,
    InvalidSession,
    ExplicitAgentBlocker,
    SuccessfulCompletion,
    ProtocolInconclusive,
}

public enum ExecutionRecoveryAction
{
    ResumeSameSession,
    RetryHandoff,
    RetryReviewAttemptOnSameSubject,
    StartFreshAttemptFromSalvage,
    WaitForCapabilityRecovery,
    AskForHumanInput,
    TerminateHonestly,
}

public enum ExecutionTransportState
{
    Connected,
    Degraded,
    Lost,
}

public enum ExecutionSessionState
{
    Unsupported,
    Unknown,
    Active,
    Resumable,
    Invalid,
}

public enum DurableOutputState
{
    Missing,
    LocalOnly,
    Published,
    Acknowledged,
}

public enum OutcomeConfidence
{
    Low,
    Medium,
    High,
}

public sealed record ImmutableReviewSubject(
    string RepositoryIdentity,
    string? ResultSha = null,
    string? ArtifactDigest = null)
{
    private static readonly Regex GitObjectId = new(
        @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContentDigest = new(
        @"\A(?:sha256:)?[0-9a-f]{64}\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(RepositoryIdentity)
        && (GitObjectId.IsMatch(ResultSha ?? string.Empty)
            || ContentDigest.IsMatch(ArtifactDigest ?? string.Empty));
}

public sealed record ExecutionRawFacts(
    string AttemptId,
    ExecutionAttemptKind AttemptKind,
    string? ProviderTerminalEvent = null,
    string? FinalAssistantOutput = null,
    string? StdOut = null,
    string? StdErr = null,
    int? ExitCode = null,
    int? Signal = null,
    bool LaunchFailed = false,
    bool TimedOut = false,
    bool OomKilled = false,
    bool OperatorCancelled = false,
    bool HostShutdown = false,
    bool LeaseLost = false,
    ExecutionTransportState TransportState = ExecutionTransportState.Connected,
    ExecutionSessionState SessionState = ExecutionSessionState.Unsupported,
    string? SessionId = null,
    DurableOutputState DurableOutputState = DurableOutputState.Missing,
    string? DurableOutputReference = null,
    int SameSessionResumeAttempts = 0,
    int FreshSalvageAttempts = 0,
    ImmutableReviewSubject? ReviewSubject = null);

public sealed record ExecutionOutcomeDecision(
    string ClassifierVersion,
    ExecutionOutcomeKind Outcome,
    ExecutionRecoveryAction RecoveryAction,
    OutcomeConfidence Confidence,
    string? Ambiguity,
    bool IsInfrastructureOutcome,
    bool ConsumesProductDefectBudget,
    bool ConsumesCompletionBudget,
    bool ConsumesCodingReworkBudget,
    bool InvokesCodingModel,
    ExecutionRawFacts RawFacts,
    string? Detail = null);

public sealed record ProviderOutputEvidence(
    string? TerminalEvent,
    string? FinalAssistantOutput,
    string? SessionId,
    bool ProviderReportedCompletion,
    bool ProviderReportedFailure,
    string? FailureMessage = null);

/// <summary>
/// Shared terminal-outcome adapter for Remote coding and review execution.
/// It treats sentinels and exit codes as evidence among the complete process,
/// provider, transport, session, lease, and durable-output facts.
/// </summary>
public static class ExecutionOutcomeAdapter
{
    public const string Version = "execution-outcome/v1";
    public const int MaxSameSessionResumeAttempts = 1;
    public const int MaxFreshSalvageAttempts = 1;

    private static readonly Regex Sentinel = new(
        @"\[\[\s*TASK[\s_-]*(?<keyword>DONE|BLOCKED|NEEDS[\s_-]*INPUT|NOOP)\s*(?::\s*(?<reason>[^\]]*?))?\s*\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvalidConfiguration = new(
        @"(?:invalid|unknown|unsupported)\s+(?:model|configuration|config)|model\s+(?:not\s+found|does\s+not\s+exist)|configuration\s+error",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvalidSession = new(
        @"(?:session|thread)\s+(?:is\s+)?(?:invalid|expired|not\s+found|cannot\s+be\s+resumed)|invalid\s+(?:session|thread)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OutOfMemory = new(
        @"(?:out\s+of\s+memory|oom(?:\s*killed)?|memory\s+limit\s+exceeded|cannot\s+allocate\s+memory)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProviderCompleted = new(
        @"""type""\s*:\s*""(?:result|turn\.completed|response\.completed)""|""subtype""\s*:\s*""success""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProviderFailed = new(
        @"""type""\s*:\s*""(?:error|turn\.failed|response\.failed)""|""subtype""\s*:\s*""error[^""]*""|""is_error""\s*:\s*true|""status""\s*:\s*""(?:error|failed|failure)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ExecutionOutcomeDecision Classify(ExecutionRawFacts facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.AttemptId);
        if (facts.SameSessionResumeAttempts < 0 || facts.FreshSalvageAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(facts), "Recovery attempt counters cannot be negative.");

        var providerFailed = ProviderFailed.IsMatch(facts.ProviderTerminalEvent ?? string.Empty);
        var providerFailureEvent = providerFailed
            ? facts.ProviderTerminalEvent
            : null;
        var failedStdOut = (providerFailed || facts.ExitCode is < 0 or > 0)
                           && string.IsNullOrWhiteSpace(facts.FinalAssistantOutput)
            ? facts.StdOut
            : null;
        var diagnostic = Join(providerFailureEvent, facts.StdErr, failedStdOut);
        var allOutput = Join(diagnostic, facts.FinalAssistantOutput, facts.StdOut);
        var sentinel = LastSentinel(
            string.IsNullOrWhiteSpace(facts.FinalAssistantOutput)
                ? facts.StdOut ?? string.Empty
                : facts.FinalAssistantOutput);

        // An honest terminal (exit 0 + any explicit sentinel) must never be
        // overridden by regex matches on diagnostic narrative. Agent CLIs stream
        // their working text over stderr, so a run that merely discusses "out of
        // memory" or "quota" would otherwise be misclassified as infrastructure.
        // Hard facts such as OomKilled, LeaseLost, timeout, and transport loss
        // still win because they are observations rather than text.
        var honestTerminal = facts.ExitCode == 0 && sentinel is not null;

        if (facts.LeaseLost)
            return Decide(facts, ExecutionOutcomeKind.LeaseLoss, OutcomeConfidence.High,
                "The current fence no longer owns the attempt.", infrastructure: true);
        if (facts.HostShutdown)
            return Decide(facts, ExecutionOutcomeKind.HostShutdown, OutcomeConfidence.High, null, infrastructure: true);
        if (facts.OperatorCancelled)
            return Decide(facts, ExecutionOutcomeKind.OperatorCancellation, OutcomeConfidence.High, null, infrastructure: true);
        if (facts.TransportState == ExecutionTransportState.Lost)
            return Decide(facts, ExecutionOutcomeKind.TransportLoss, OutcomeConfidence.High, null, infrastructure: true);
        if (facts.TimedOut)
            return Decide(facts, ExecutionOutcomeKind.Timeout, OutcomeConfidence.High, null, infrastructure: true);
        if (facts.OomKilled || (!honestTerminal && OutOfMemory.IsMatch(diagnostic)))
            return Decide(facts, ExecutionOutcomeKind.OutOfMemory, OutcomeConfidence.High, null, infrastructure: true);
        if (facts.SessionState == ExecutionSessionState.Invalid || (!honestTerminal && InvalidSession.IsMatch(diagnostic)))
            return Decide(facts, ExecutionOutcomeKind.InvalidSession, OutcomeConfidence.High, null, infrastructure: true);
        var providerAccess = ProviderAccessClassifier.Classify(
            facts.ExitCode ?? (providerFailed ? 1 : 0),
            failedStdOut,
            Join(providerFailureEvent, facts.StdErr));
        if (!honestTerminal && providerAccess.Kind == ProviderAccessEvidenceKind.RateLimited)
            return Decide(facts, ExecutionOutcomeKind.QuotaExceeded, OutcomeConfidence.High, null, infrastructure: true);
        if (!honestTerminal && providerAccess.Kind == ProviderAccessEvidenceKind.AuthenticationFailure)
            return Decide(facts, ExecutionOutcomeKind.AuthenticationFailure, OutcomeConfidence.High, null, infrastructure: true);
        if (!honestTerminal && InvalidConfiguration.IsMatch(diagnostic))
            return Decide(facts, ExecutionOutcomeKind.InvalidModelOrConfiguration, OutcomeConfidence.High, null, infrastructure: true);
        if (facts.LaunchFailed)
            return Decide(facts, ExecutionOutcomeKind.LaunchFailure, OutcomeConfidence.High, null, infrastructure: true);

        if (sentinel?.Keyword is "BLOCKED" or "NEEDS_INPUT")
            return Decide(
                facts,
                ExecutionOutcomeKind.ExplicitAgentBlocker,
                OutcomeConfidence.High,
                null,
                infrastructure: false,
                detail: sentinel.Reason);

        if (sentinel?.Keyword is "DONE" or "NOOP")
            return Decide(facts, ExecutionOutcomeKind.SuccessfulCompletion, OutcomeConfidence.High, null, infrastructure: false);

        var providerCompleted = !providerFailed
                                && ProviderCompleted.IsMatch(facts.ProviderTerminalEvent ?? string.Empty);
        if (facts.ExitCode == 0 && providerCompleted && !string.IsNullOrWhiteSpace(facts.FinalAssistantOutput))
        {
            return Decide(
                facts,
                ExecutionOutcomeKind.SuccessfulCompletion,
                OutcomeConfidence.Medium,
                "No terminal sentinel was present; provider completion plus a final assistant result supplied the terminal evidence.",
                infrastructure: false);
        }

        if (providerFailed || facts.Signal is not null || facts.ExitCode is < 0 or > 0)
            return Decide(facts, ExecutionOutcomeKind.CliCrash, OutcomeConfidence.High, null, infrastructure: true);

        return Decide(
            facts,
            ExecutionOutcomeKind.ProtocolInconclusive,
            string.IsNullOrWhiteSpace(allOutput) ? OutcomeConfidence.High : OutcomeConfidence.Low,
            facts.ExitCode == 0
                ? "The process exited zero without a terminal marker or authoritative provider completion."
                : "No terminal fact established success, a blocker, or a typed infrastructure failure.",
            infrastructure: false);
    }

    private static ExecutionOutcomeDecision Decide(
        ExecutionRawFacts facts,
        ExecutionOutcomeKind outcome,
        OutcomeConfidence confidence,
        string? ambiguity,
        bool infrastructure,
        string? detail = null)
    {
        var recovery = SelectRecovery(facts, outcome);
        var invokesCoding = facts.AttemptKind == ExecutionAttemptKind.Coding
                            && recovery is ExecutionRecoveryAction.ResumeSameSession
                                or ExecutionRecoveryAction.StartFreshAttemptFromSalvage;
        return new ExecutionOutcomeDecision(
            Version,
            outcome,
            recovery,
            confidence,
            ambiguity,
            infrastructure,
            ConsumesProductDefectBudget: false,
            ConsumesCompletionBudget: false,
            ConsumesCodingReworkBudget: false,
            InvokesCodingModel: invokesCoding,
            RawFacts: facts,
            Detail: detail);
    }

    private static ExecutionRecoveryAction SelectRecovery(
        ExecutionRawFacts facts,
        ExecutionOutcomeKind outcome)
    {
        if (outcome == ExecutionOutcomeKind.SuccessfulCompletion)
        {
            return facts.TransportState != ExecutionTransportState.Connected
                   || facts.DurableOutputState is DurableOutputState.LocalOnly or DurableOutputState.Published
                ? ExecutionRecoveryAction.RetryHandoff
                : ExecutionRecoveryAction.TerminateHonestly;
        }

        if (outcome == ExecutionOutcomeKind.ExplicitAgentBlocker)
            return ExecutionRecoveryAction.AskForHumanInput;

        if (outcome == ExecutionOutcomeKind.ProtocolInconclusive)
            return ExecutionRecoveryAction.AskForHumanInput;

        if (outcome is ExecutionOutcomeKind.AuthenticationFailure
            or ExecutionOutcomeKind.QuotaExceeded
            or ExecutionOutcomeKind.InvalidModelOrConfiguration
            or ExecutionOutcomeKind.LaunchFailure)
            return ExecutionRecoveryAction.WaitForCapabilityRecovery;

        if (outcome is ExecutionOutcomeKind.LeaseLoss or ExecutionOutcomeKind.OperatorCancellation)
            return ExecutionRecoveryAction.TerminateHonestly;

        if (facts.AttemptKind == ExecutionAttemptKind.Review)
        {
            return facts.ReviewSubject?.IsValid == true
                ? ExecutionRecoveryAction.RetryReviewAttemptOnSameSubject
                : ExecutionRecoveryAction.AskForHumanInput;
        }

        if (outcome == ExecutionOutcomeKind.TransportLoss
            && facts.DurableOutputState is DurableOutputState.Published or DurableOutputState.Acknowledged)
            return ExecutionRecoveryAction.RetryHandoff;

        if (outcome != ExecutionOutcomeKind.InvalidSession
            && facts.SessionState == ExecutionSessionState.Resumable
            && !string.IsNullOrWhiteSpace(facts.SessionId)
            && facts.SameSessionResumeAttempts < MaxSameSessionResumeAttempts)
            return ExecutionRecoveryAction.ResumeSameSession;

        if (!string.IsNullOrWhiteSpace(facts.DurableOutputReference)
            && facts.DurableOutputState is DurableOutputState.Published or DurableOutputState.Acknowledged
            && facts.FreshSalvageAttempts < MaxFreshSalvageAttempts)
            return ExecutionRecoveryAction.StartFreshAttemptFromSalvage;

        return ExecutionRecoveryAction.TerminateHonestly;
    }

    private static SentinelEvidence? LastSentinel(string text)
    {
        Match? last = null;
        foreach (Match match in Sentinel.Matches(text)) last = match;
        if (last is null) return null;
        var keyword = Regex.Replace(last.Groups["keyword"].Value, @"[\s_-]+", "_").ToUpperInvariant();
        var reason = last.Groups["reason"].Success && last.Groups["reason"].Value.Length > 0
            ? last.Groups["reason"].Value.Trim()
            : null;
        return new SentinelEvidence(keyword, reason);
    }

    private static string Join(params string?[] values)
        => string.Join('\n', values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private sealed record SentinelEvidence(string Keyword, string? Reason);
}

/// <summary>Extracts provider terminal, final reply, and session evidence without interpreting product prose.</summary>
public static class ProviderOutputEvidenceExtractor
{
    private static readonly string[] SessionKeys = ["session_id", "sessionId", "thread_id", "threadId"];

    public static ProviderOutputEvidence Extract(string? stdout)
    {
        string? terminal = null;
        string? final = null;
        string? sessionId = null;
        var completed = false;
        var failed = false;
        string? failureMessage = null;

        foreach (var line in (stdout ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                var type = StringProperty(root, "type");
                var terminalFailed = IsFailure(root, type);
                if (terminalFailed)
                {
                    terminal = line;
                    failed = true;
                    completed = false;
                    failureMessage = ExtractFailureMessage(root) ?? failureMessage;
                }
                else if (type is "result" or "turn.completed" or "response.completed")
                {
                    terminal = line;
                    completed = true;
                    failed = false;
                }

                sessionId ??= FindString(root, SessionKeys);
                var candidate = ExtractAssistantText(root, type);
                if (!string.IsNullOrWhiteSpace(candidate)) final = candidate;
            }
        }

        return new ProviderOutputEvidence(
            terminal,
            final,
            sessionId,
            completed,
            failed,
            failureMessage);
    }

    private static string? ExtractFailureMessage(JsonElement root)
        => FindString(root, ["message", "error", "reason"]);

    private static bool IsFailure(JsonElement root, string? type)
    {
        if (type is "error" or "turn.failed" or "response.failed") return true;
        if (TryProperty(root, "is_error", out var isError)
            && isError.ValueKind is JsonValueKind.True)
            return true;
        var subtype = StringProperty(root, "subtype");
        if (subtype?.StartsWith("error", StringComparison.OrdinalIgnoreCase) == true) return true;
        var status = StringProperty(root, "status");
        return status is not null
               && (status.Equals("error", StringComparison.OrdinalIgnoreCase)
                   || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                   || status.Equals("failure", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractAssistantText(JsonElement root, string? type)
    {
        if (type == "result") return StringProperty(root, "result");
        if (type == "item.completed"
            && TryProperty(root, "item", out var item)
            && string.Equals(StringProperty(item, "type"), "agent_message", StringComparison.OrdinalIgnoreCase))
            return StringProperty(item, "text");
        if (type is "turn.completed" or "response.completed")
            return FindString(root, ["output_text", "text", "result"]);
        return null;
    }

    private static string? FindString(JsonElement element, IReadOnlyList<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in keys)
                if (TryProperty(element, key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindString(property.Value, keys);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, keys);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static string? StringProperty(JsonElement element, string name)
        => TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
