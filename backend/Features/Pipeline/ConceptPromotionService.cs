namespace AgentStudio.Pipeline;

/// <summary>
/// Creates coding cards from the implementation proposals embedded in a
/// validated concept Workbench. The per-source spawn ledger makes each
/// descriptor item idempotent across repeated operator requests.
/// Promotion never moves the source concept card: accepting the concept is
/// the separate Human review to Accepted move (AGT-3701).
/// </summary>
public sealed class ConceptPromotionService
{
    private const string ReasonPrefix = "concept-promotion:";

    private readonly AgentStudio.Tasks.TaskScannerService _scanner;
    private readonly AgentStudio.Tasks.TaskMutationService _mutations;
    private readonly ILogger<ConceptPromotionService> _logger;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly object _gate = new();

    public ConceptPromotionService(
        AgentStudio.Tasks.TaskScannerService scanner,
        AgentStudio.Tasks.TaskMutationService mutations,
        ILogger<ConceptPromotionService> logger,
        PipelineExecutionLog? pipelineLog = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _logger = logger;
        _pipelineLog = pipelineLog;
    }

    /// <summary>Whether the concept card already promoted at least one Dossier slice.</summary>
    public static bool HasPromotedSlices(string sourceFolderPath, ILogger? logger = null)
        => SpawnedTaskLedger.Read(sourceFolderPath, logger)
            .Any(record => record.Reason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) == true);

    /// <summary>
    /// The plan with every slice that already has a card, so the owner sees
    /// which slices a new request would still create.
    /// </summary>
    public PromoteConceptResponse WithPromotedSlices(TaskInfo source, PromoteConceptResponse plan)
    {
        var ledger = SpawnedTaskLedger.Read(source.FolderPath, _logger);
        var promoted = new List<PromotedConceptTask>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var reason = ReasonFor(plan.Source.RepoRelativePath, index);
            var record = ledger.FirstOrDefault(candidate =>
                string.Equals(candidate.Reason, reason, StringComparison.Ordinal));
            if (record is null) continue;
            var card = _scanner.FindJob(record.TargetJobId ?? record.TargetKey ?? "", source.WatchPath);
            promoted.Add(new PromotedConceptTask
            {
                JobId = record.TargetJobId ?? card?.Id ?? "",
                TaskKey = card?.Key ?? record.TargetKey,
                Title = card?.Title ?? plan.Items[index].Title.Trim(),
                ItemIndex = index,
                State = card?.State,
            });
        }
        return plan with { Promoted = promoted };
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

        var spawns = requested.Select(index =>
        {
            var item = plan.Items[index];
            return new ConceptCardSpawn(
                ReasonFor(plan.Source.RepoRelativePath, index),
                (item.Decision is null ? CodingRequest(plan.Source, item) : DecisionRequest(plan.Source, item)) with
                {
                    Dossier = TaskDossierProvenance.FromImplementation(
                        plan.Source.RepoRelativePath, item, index),
                });
        }).ToList();

        var created = CreateCards(source, spawns)
            .Select((card, position) => card with
            {
                ItemIndex = requested[position],
                State = _scanner.FindJob(card.JobId, source.WatchPath)?.State,
            })
            .ToList();
        RecordPromotionStep(source, created.Count);
        return new PromoteConceptTasksResponse
        {
            Source = plan.Source,
            Created = created,
        };
    }

    private void RecordPromotionStep(TaskInfo source, int createdCount)
    {
        if (_pipelineLog is null || createdCount == 0) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(source.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptPromotionStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "implementation-cards-created",
            VerdictSummary = $"Created or confirmed {createdCount} implementation card(s) from the Dossier.",
        });
    }

    /// <summary>
    /// The promotion mechanism: creates one card per spawn, links it to the
    /// source card, and records it in the source's spawn ledger so a repeated
    /// request returns the existing card instead of creating a second one.
    /// Concept promotion and the decision apply step both create cards here.
    /// </summary>
    public IReadOnlyList<PromotedConceptTask> CreateCards(TaskInfo source, IReadOnlyList<ConceptCardSpawn> spawns)
    {
        var promoted = new List<PromotedConceptTask>();
        lock (_gate)
        {
            foreach (var (spawn, position) in spawns.Select((spawn, position) => (spawn, position)))
            {
                var title = spawn.Request.Title?.Trim() ?? "";
                var existing = SpawnedTaskLedger.Read(source.FolderPath, _logger)
                    .FirstOrDefault(record => string.Equals(record.Reason, spawn.Reason, StringComparison.Ordinal));
                if (existing != null)
                {
                    if (spawn.Request.Dossier is { } dossier)
                    {
                        var prior = _scanner.FindJob(existing.TargetJobId ?? existing.TargetKey ?? "", source.WatchPath);
                        if (prior is { Dossier: null })
                            _mutations.SetJobDossier(prior.Id, dossier, prior.WatchPath);
                    }
                    promoted.Add(new PromotedConceptTask
                    {
                        JobId = existing.TargetJobId ?? "",
                        TaskKey = existing.TargetKey,
                        Title = title,
                    });
                    continue;
                }

                var jobId = _mutations.CreateJob(spawn.Request with
                {
                    Title = title,
                    WatchPath = source.WatchPath,
                    Dossier = spawn.Request.Dossier ?? source.Dossier,
                });
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException(
                        $"Could not create implementation card {position + 1}.");

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
                        Reason = spawn.Reason,
                    }, _logger))
                {
                    throw new IOException(
                        "The implementation card was created, but its promotion ledger could not be persisted.");
                }

                promoted.Add(new PromotedConceptTask
                {
                    JobId = jobId,
                    TaskKey = targetKey,
                    Title = title,
                });
            }
        }
        return promoted;
    }

    private static CreateTaskRequest CodingRequest(ConceptSourceDocument source, ConceptImplementationTask item) => new()
    {
        Title = item.Title.Trim(),
        PromptMarkdown = BuildPrompt(source, item),
        AcceptanceScope = DossierImplementationCardPolicy.AcceptanceScopeFor(item),
        Mode = TaskModes.Coding,
        TargetState = TaskStates.Preparation,
    };

    /// <summary>A single fork surfaced by the Dossier becomes a decision card, not a prose request.</summary>
    private static CreateTaskRequest DecisionRequest(ConceptSourceDocument source, ConceptImplementationTask item) => new()
    {
        Title = item.Title.Trim(),
        Kind = TaskKinds.Decision,
        Decision = item.Decision,
        PromptMarkdown = BuildDecisionPrompt(source, item),
        TargetState = TaskStates.Preparation,
    };

    private static string ReasonFor(string sourcePath, int index)
        => $"{ReasonPrefix}{sourcePath}:{index}";

    internal static string BuildDecisionPrompt(ConceptSourceDocument source, ConceptImplementationTask item)
    {
        var context = string.IsNullOrWhiteSpace(item.PromptMarkdown) ? "" : "\n\n" + item.PromptMarkdown.Trim();
        return $"Decision surfaced by the Dossier `{source.RepoRelativePath}`. The question and options are "
            + "fields on this card; the chosen option's requirements become implementation cards." + context;
    }

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

/// <summary>One card to create through <see cref="ConceptPromotionService.CreateCards"/>, keyed by its ledger reason.</summary>
public sealed record ConceptCardSpawn(string Reason, CreateTaskRequest Request);
