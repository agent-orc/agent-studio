namespace AgentStudio.Pipeline;

/// <summary>
/// Creates coding cards from the implementation proposals embedded in a
/// validated concept Workbench. The per-source spawn ledger makes each
/// descriptor item idempotent across repeated operator requests.
/// </summary>
public sealed class ConceptPromotionService
{
    private const string ReasonPrefix = "concept-promotion:";

    private readonly AgentStudio.Tasks.TaskScannerService _scanner;
    private readonly AgentStudio.Tasks.TaskMutationService _mutations;
    private readonly ILogger<ConceptPromotionService> _logger;
    private readonly object _gate = new();

    public ConceptPromotionService(
        AgentStudio.Tasks.TaskScannerService scanner,
        AgentStudio.Tasks.TaskMutationService mutations,
        ILogger<ConceptPromotionService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _logger = logger;
    }

    public PromoteConceptTasksResponse Promote(
        TaskInfo source,
        PromoteConceptResponse plan,
        PromoteConceptRequest request)
    {
        var requested = request.ItemIndexes is { Count: > 0 }
            ? request.ItemIndexes.Distinct().OrderBy(index => index).ToList()
            : Enumerable.Range(0, plan.Items.Count).ToList();
        if (requested.Any(index => index < 0 || index >= plan.Items.Count))
            throw new ArgumentOutOfRangeException(
                nameof(request.ItemIndexes), "A selected implementation item does not exist.");

        var promoted = new List<PromotedConceptTask>();
        lock (_gate)
        {
            foreach (var index in requested)
            {
                var reason = ReasonFor(plan.Source.RepoRelativePath, index);
                var existing = SpawnedTaskLedger.Read(source.FolderPath, _logger)
                    .FirstOrDefault(record => string.Equals(record.Reason, reason, StringComparison.Ordinal));
                if (existing != null)
                {
                    promoted.Add(new PromotedConceptTask
                    {
                        JobId = existing.TargetJobId ?? "",
                        TaskKey = existing.TargetKey,
                        Title = plan.Items[index].Title,
                    });
                    continue;
                }

                var item = plan.Items[index];
                var acceptanceScope = DossierImplementationCardPolicy.AcceptanceScopeFor(item);
                var jobId = _mutations.CreateJob(new CreateTaskRequest
                {
                    Title = item.Title.Trim(),
                    PromptMarkdown = BuildPrompt(plan.Source, item),
                    AcceptanceScope = acceptanceScope,
                    WatchPath = source.WatchPath,
                    Mode = TaskModes.Coding,
                    TargetState = TaskStates.Preparation,
                });
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException(
                        $"Could not create implementation card {index + 1}.");

                var created = _scanner.FindJob(jobId, source.WatchPath);
                if (!string.IsNullOrWhiteSpace(source.Key) && created != null)
                {
                    _mutations.SetTaskReferences(
                        jobId,
                        new TaskReferences { RelatedTo = [source.Key!] },
                        created.WatchPath);
                    created = _scanner.FindJob(jobId, source.WatchPath) ?? created;
                }

                var targetKey = created?.Key ?? jobId;
                if (!SpawnedTaskLedger.Append(source.FolderPath, new SpawnedTaskRecord
                    {
                        At = DateTime.UtcNow,
                        SourceKey = source.Key,
                        TargetProject = source.ProjectName,
                        TargetKey = targetKey,
                        TargetJobId = jobId,
                        Reason = reason,
                    }, _logger))
                {
                    throw new IOException(
                        "The implementation card was created, but its promotion ledger could not be persisted.");
                }

                promoted.Add(new PromotedConceptTask
                {
                    JobId = jobId,
                    TaskKey = targetKey,
                    Title = item.Title.Trim(),
                });
            }
        }

        return new PromoteConceptTasksResponse
        {
            Source = plan.Source,
            Created = promoted,
        };
    }

    private static string ReasonFor(string sourcePath, int index)
        => $"{ReasonPrefix}{sourcePath}:{index}";

    internal static string BuildPrompt(
        ConceptSourceDocument source,
        ConceptImplementationTask item)
    {
        var decisions = source.Decisions.Count == 0
            ? "The Dossier has no machine-readable decision options. Follow its written recommendations."
            : string.Join("\n", source.Decisions.Select(decision =>
                $"- {decision.Label} [{decision.Id}]: {decision.OptionLabel} [{decision.OptionId}] " +
                (decision.OperatorSelected ? "(operator-selected working assumption)"
                    : decision.IsWorkingAssumption ? "(recommended working assumption)" : "(alternative)")));
        return $"""
           Implement the approved concept described in `{source.RepoRelativePath}`.

           The concept document is the source of truth. Preserve its stated
           constraints, recommendation, evidence, and open-decision outcomes.

           Dossier decisions for this implementation:
           {decisions}

           Implement each recommended working assumption unless the operator recorded
           another choice in the Dossier's decision block. Record the choices implemented.
           Any Dossier or item instruction to wait for an operator answer to a
           recommended decision is superseded by this working-assumption rule.
           Sight review and operator acceptance follow delivery in the pipeline; do not
           stop this run to request those approvals.

           {item.PromptMarkdown.Trim()}
           """;
    }
}
