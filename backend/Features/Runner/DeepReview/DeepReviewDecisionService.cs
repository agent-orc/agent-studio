using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>The deep-review aspect: a read-only, review-plane agent pass frozen into the plan.</summary>
public static class DeepReviewAspect
{
    public const string Id = DeepReviewDecisionPolicy.AspectId;
    public const string StepId = "aspect-" + Id;
    /// <summary>The decision row between pipeline steps; deterministic, it calls no model.</summary>
    public const string DecisionStepId = "post-deep-review-decision";
    public const string PromptTemplate = "review-aspect-deep-review.md";

    public static readonly AspectDefinition Definition = new(
        Id: Id,
        ConcernNamespace: "quality",
        PromptTemplate: PromptTemplate,
        Title: "Deep review",
        FallbackSystem: "Review the diff for defects the bounded aspects miss: privilege escalation, data loss, unbounded resource use, endless loops, broken fencing or leases, and concurrency races. Block only a concrete defect and cite the exact file and line.");

    public static bool Matches(string? aspectOrStepId)
        => string.Equals(aspectOrStepId, Id, StringComparison.OrdinalIgnoreCase)
           || string.Equals(aspectOrStepId, StepId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Collects the grounds of a deep-review decision for one delivery, asks the
/// shared decision library, and freezes the receipt in the card's
/// deep-review ledger and as a decision row between the pipeline steps. It
/// never moves the card; the plan builder adds the review command only for a
/// dispatched decision.
/// </summary>
public sealed class DeepReviewDecisionService
{
    public const string Section = "ReviewDecisionOrchestrator:DeepReview";
    private const int BriefCharacters = 64_000;
    private const int NearCapMarginPercent = 10;
    private static readonly TimeSpan QuotaFreshness = TimeSpan.FromHours(6);

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ModelRoutingPolicyRegistry? _routing;
    private readonly GitService? _git;
    private readonly DecisionRuleVersionStore? _rules;
    private readonly AgentStudio.Registry.ProjectRegistry? _projects;
    private readonly QuotaCacheStore? _quota;
    private readonly CliQuotaCapsService? _caps;
    private readonly PipelineExecutionLog? _pipeline;
    private readonly ILogger<DeepReviewDecisionService>? _logger;

    public DeepReviewDecisionService(
        IConfiguration configuration,
        TimeProvider time,
        ModelRoutingPolicyRegistry? routing = null,
        GitService? git = null,
        DecisionRuleVersionStore? rules = null,
        AgentStudio.Registry.ProjectRegistry? projects = null,
        QuotaCacheStore? quota = null,
        CliQuotaCapsService? caps = null,
        PipelineExecutionLog? pipeline = null,
        ILogger<DeepReviewDecisionService>? logger = null)
    {
        _configuration = configuration;
        _time = time;
        _routing = routing;
        _git = git;
        _rules = rules;
        _projects = projects;
        _quota = quota;
        _caps = caps;
        _pipeline = pipeline;
        _logger = logger;
    }

    public string Mode => DeepReviewModes.Normalize(_configuration[$"{Section}:Mode"]);

    public string CliType => ReviewDecisionOrchestrator.NormalizeReviewCliType(
        _configuration[$"{Section}:Cli"] ?? CliTypes.Codex);

    public string Model => string.IsNullOrWhiteSpace(_configuration[$"{Section}:Model"])
        ? ModelIds.Gpt6Sol : _configuration[$"{Section}:Model"]!.Trim();

    public int MaximumRounds => Math.Clamp(
        _configuration.GetValue($"{Section}:MaxRounds", DeepReviewDecisionPolicy.DefaultMaximumRounds), 1, 10);

    /// <summary>
    /// The frozen decision for this delivery, deciding it first when none
    /// exists. Null when the step is off, the delivery has no exact SHA, or the
    /// ledger cannot be read; review creation never fails on this step.
    /// </summary>
    public DeepReviewDecisionEntry? Decide(
        TaskInfo task,
        string? repositoryPath,
        string? integrationRef,
        string? resultRef,
        string? resultSha,
        IReadOnlyList<string>? changedPaths)
    {
        if (Mode == DeepReviewModes.Off || !ReviewSubjectStore.IsValidResultSha(resultSha)) return null;
        try
        {
            var ledger = DeepReviewLedgerStore.Read(task.FolderPath);
            var frozen = ledger.Decisions.FirstOrDefault(item =>
                string.Equals(item.ResultSha, resultSha, StringComparison.OrdinalIgnoreCase));
            if (frozen is not null) return frozen;

            var decidedAt = _time.GetUtcNow().UtcDateTime;
            var facts = CollectFacts(task, repositoryPath, integrationRef, resultRef, resultSha!, changedPaths,
                ledger, decidedAt);
            var decision = MonolithTransitionAdapter.DeepReview(facts);
            var entry = DeepReviewLedgerStore.Freeze(task.FolderPath,
                new DeepReviewDecisionEntry(resultSha!, decidedAt, CliType, decision), out var created);
            if (created) RecordDecisionStep(task, entry);
            _logger?.LogInformation(
                "deep-review-decision task={TaskKey} resultSha={ResultSha} mode={Mode} action={Action} dispatch={Dispatch} model={Model} level={Level} reason={Reason}",
                task.Key ?? task.Id, resultSha, entry.Decision.Mode, entry.Decision.Action, entry.Decision.Dispatch,
                entry.Decision.Model, entry.Decision.ThinkingLevel, entry.Decision.Reason);
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger?.LogError(exception,
                "deep-review-decision-unavailable task={TaskKey} resultSha={ResultSha}", task.Key ?? task.Id, resultSha);
            return null;
        }
    }

    /// <summary>
    /// The deep-review finding of this report that blocked again after its
    /// previous fix round, read against the card's deep-review ledger. Null
    /// when nothing repeats or the ledger cannot be read; the ordinary card
    /// round budget still bounds the loop then.
    /// </summary>
    public static DeepReviewRepeatedBlock? RepeatedBlock(string folder, string attemptId,
        IEnumerable<ReviewVerdictDto> verdicts, ILogger? logger = null)
    {
        DeepReviewLedger ledger;
        try
        {
            ledger = DeepReviewLedgerStore.Read(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or System.Text.Json.JsonException)
        {
            logger?.LogError(exception, "deep-review-ledger-unreadable folder={Folder} attempt={AttemptId}", folder, attemptId);
            return null;
        }
        return MonolithTransitionAdapter.DeepReviewDecisionPolicyRepeatedBlock(
            verdicts.Select(verdict => new ReviewFollowUpFinding(verdict.Aspect, verdict.Status, verdict.Summary,
                verdict.EvidenceChecked, verdict.Missing, verdict.Classification)),
            ledger.RoundRecords, attemptId);
    }

    internal DeepReviewFacts CollectFacts(
        TaskInfo task,
        string? repositoryPath,
        string? integrationRef,
        string? resultRef,
        string resultSha,
        IReadOnlyList<string>? changedPaths,
        DeepReviewLedger ledger,
        DateTime at)
    {
        var briefPath = Path.Combine(task.FolderPath, "prompt.md");
        var brief = File.Exists(briefPath) ? BoundedFileRead.ReadHeadText(briefPath, BriefCharacters) : string.Empty;
        var risk = _routing?.CorrectnessRisk(task.TaskType, task.Title, brief)
                   ?? (DeepReviewRiskClasses.Standard, null, "routing policy unavailable");
        var source = resultRef ?? resultSha;
        var lines = _git is null || string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(integrationRef)
            ? null
            : _git.ChangedLinesAgainstMergeBase(repositoryPath, integrationRef, source);
        var delivered = ReviewRoundBudgetStore.Read(task.FolderPath).Rounds;
        var (quota, quotaReason) = Quota(at);
        var (rules, ruleVersion) = Rules(task);
        return new DeepReviewFacts(
            Mode,
            task.Key ?? task.Id,
            resultSha,
            risk.RiskClass,
            risk.Reason,
            changedPaths,
            lines,
            task.Model,
            task.ThinkingLevel,
            ledger.RoundRecords,
            delivered.Count,
            delivered.Count(round => round.BlockingAspects.Count > 0),
            delivered.Count(round => round.BlockingAspects.Any(aspect =>
                aspect is "build-tests" or "review-verification")),
            quota,
            quotaReason,
            Model,
            rules,
            ruleVersion,
            MaximumRounds);
    }

    private (string State, string? Reason) Quota(DateTime at)
    {
        if (_quota is null || _caps is null) return (DeepReviewQuotaStates.Unknown, "no quota source");
        var snapshot = _quota.Read().FirstOrDefault(item =>
            string.Equals(item.CliType, CliType, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null) return (DeepReviewQuotaStates.Unknown, $"no {CliType} quota snapshot");
        if (at - snapshot.FetchedAt > QuotaFreshness)
            return (DeepReviewQuotaStates.Unknown, $"{CliType} quota snapshot older than {QuotaFreshness.TotalHours:0} h");
        var evaluation = _caps.Evaluate(snapshot);
        if (evaluation.Blocked) return (DeepReviewQuotaStates.Capped, evaluation.DescribeReason());
        var near = snapshot.Windows.FirstOrDefault(window => window.UsedPct is { } used
            && !string.IsNullOrWhiteSpace(window.Label)
            && used >= _caps.GetCap(snapshot.CliType, window.Label) - NearCapMarginPercent);
        return near is null
            ? (DeepReviewQuotaStates.Comfortable, null)
            : (DeepReviewQuotaStates.NearCap, $"{CliType} {near.Label} at {near.UsedPct:0.#}%");
    }

    private (IReadOnlyList<DecisionRuleV1> Rules, string Version) Rules(TaskInfo task)
    {
        var project = _projects?.FindByIdOrDisplayName(task.ProjectName);
        if (_rules is null || project?.WorkspaceId is not { } workspaceId)
            return ([], DeepReviewDecisionPolicy.PlatformRuleVersion);
        var workspaceVersion = _rules.ActiveVersionId(workspaceId);
        var projectVersion = _rules.ActiveVersionId(workspaceId, project.Id);
        var rules = _rules.EffectiveRules(workspaceId, project.Id)
            .Where(rule => rule.Trigger == DecisionRuleTriggers.DeepReview).ToArray();
        return (rules, (workspaceVersion ?? DeepReviewDecisionPolicy.PlatformRuleVersion)
            + (projectVersion is null ? string.Empty : "+" + projectVersion));
    }

    private void RecordDecisionStep(TaskInfo task, DeepReviewDecisionEntry entry)
    {
        if (_pipeline?.Read(task.FolderPath) is not { } record) return;
        var decision = entry.Decision;
        var verdict = decision.Action == DeepReviewActions.Run && !decision.Dispatch
            ? "shadow-run" : decision.Action;
        var route = decision.Model is null ? string.Empty : $"{decision.Model}/{decision.ThinkingLevel}: ";
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = DeepReviewAspect.DecisionStepId,
            Kind = StepKind.Orchestrator,
            Attempt = record.Attempt,
            Status = PipelineStepStatus.Passed,
            StartedAt = entry.DecidedAt,
            CompletedAt = entry.DecidedAt,
            DurationMs = 0,
            CostBasis = StepCostBasis.Deterministic,
            Verdict = verdict,
            VerdictSummary = route + decision.Reason,
            Reason = $"{decision.Mode} mode, {decision.PolicyVersion}, rules {decision.RuleVersion}",
            EvidenceRef = DeepReviewLedgerStore.FileName,
        });
    }
}
