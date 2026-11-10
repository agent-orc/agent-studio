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
        var selected = request.ItemIndexes is { Count: > 0 }
            ? request.ItemIndexes.Distinct().ToList()
            : Enumerable.Range(0, plan.Items.Count).ToList();
        if (selected.Any(index => index < 0 || index >= plan.Items.Count))
            throw new ArgumentOutOfRangeException(
                nameof(request.ItemIndexes), "A selected implementation item does not exist.");

        var predecessors = Enumerable.Range(0, plan.Items.Count)
            .Select(index => DossierImplementationCardPolicy.PredecessorIndexes(plan.Items, index))
            .ToList();
        var spawns = WithPredecessors(selected, predecessors).Select(index =>
        {
            var item = plan.Items[index];
            return new ConceptCardSpawn(
                ReasonFor(plan.Source.RepoRelativePath, index),
                (item.Decision is null ? CodingRequest(plan.Source, item) : DecisionRequest(plan.Source, item)) with
                {
                    Dossier = TaskDossierProvenance.FromImplementation(
                        plan.Source.RepoRelativePath, item, index, plan.Items.Count),
                },
                predecessors[index].Select(predecessor => ReasonFor(plan.Source.RepoRelativePath, predecessor)).ToList());
        }).ToList();

        return new PromoteConceptTasksResponse
        {
            Source = plan.Source,
            Created = CreateCards(source, spawns).ToList(),
        };
    }

    /// <summary>
    /// A slice cannot start before the slices it depends on exist, so selecting
    /// a slice also promotes its predecessors. Predecessors are earlier items,
    /// so ascending descriptor order creates every predecessor first.
    /// </summary>
    private static IEnumerable<int> WithPredecessors(IEnumerable<int> selected, IReadOnlyList<IReadOnlyList<int>> predecessors)
    {
        var included = new SortedSet<int>();
        var pending = new Stack<int>(selected);
        while (pending.TryPop(out var index))
        {
            if (!included.Add(index)) continue;
            foreach (var predecessor in predecessors[index]) pending.Push(predecessor);
        }
        return included;
    }

    /// <summary>
    /// The promotion mechanism: creates one card per spawn, links it to the
    /// source card, and records it in the source's spawn ledger so a repeated
    /// request returns the existing card instead of creating a second one.
    /// A spawn's predecessor reasons become plain <c>dependsOn</c> edges to the
    /// cards the ledger recorded for them, so spawns must list predecessors first.
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
                var ledger = SpawnedTaskLedger.Read(source.FolderPath, _logger);
                var existing = ledger
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
                var dependsOn = PredecessorKeys(ledger, spawn);
                if (created != null && (!string.IsNullOrWhiteSpace(source.Key) || dependsOn.Count > 0))
                {
                    _mutations.SetTaskReferences(
                        jobId,
                        new TaskReferences
                        {
                            DependsOn = dependsOn,
                            RelatedTo = string.IsNullOrWhiteSpace(source.Key) ? [] : [source.Key!],
                        },
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

    /// <summary>
    /// Plain delivery edges to the predecessor cards: a predecessor releases its
    /// dependents once its delivery is integrated, not only when it completes.
    /// </summary>
    private List<TaskDependencyReference> PredecessorKeys(
        IReadOnlyList<SpawnedTaskRecord> ledger, ConceptCardSpawn spawn)
    {
        var keys = new List<TaskDependencyReference>();
        foreach (var reason in spawn.DependsOnReasons ?? [])
        {
            var record = ledger.FirstOrDefault(entry => string.Equals(entry.Reason, reason, StringComparison.Ordinal));
            if (record?.TargetKey is { Length: > 0 } key) keys.Add(new TaskDependencyReference(key));
            else _logger.LogWarning("concept-promotion-predecessor-missing reason={Reason} for={Spawn}", reason, spawn.Reason);
        }
        return keys;
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

/// <summary>
/// One card to create through <see cref="ConceptPromotionService.CreateCards"/>, keyed by its ledger reason.
/// <paramref name="DependsOnReasons"/> names the ledger reasons of the cards this one depends on.
/// </summary>
public sealed record ConceptCardSpawn(
    string Reason, CreateTaskRequest Request, IReadOnlyList<string>? DependsOnReasons = null);
