using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Git;
using AgentStudio.Shared;

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
    private readonly ManagedRepositoryMutationService _repositoryMutations;
    private readonly AgentStudio.Docs.WorkbenchChangeNotifier _workbenchChanges;
    private readonly ILogger<ConceptPromotionService> _logger;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly object _gate = new();
    internal Action<string>? FailurePoint { get; set; }

    public ConceptPromotionService(
        AgentStudio.Tasks.TaskScannerService scanner,
        AgentStudio.Tasks.TaskMutationService mutations,
        ManagedRepositoryMutationService repositoryMutations,
        AgentStudio.Docs.WorkbenchChangeNotifier workbenchChanges,
        ILogger<ConceptPromotionService> logger,
        PipelineExecutionLog? pipelineLog = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _repositoryMutations = repositoryMutations;
        _workbenchChanges = workbenchChanges;
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
        var selected = request.ItemIndexes is { Count: > 0 }
            ? request.ItemIndexes.Distinct().ToList()
            : Enumerable.Range(0, plan.Items.Count).ToList();
        if (selected.Any(index => index < 0 || index >= plan.Items.Count))
            throw new ArgumentOutOfRangeException(
                nameof(request.ItemIndexes), "A selected implementation item does not exist.");

        var predecessors = Enumerable.Range(0, plan.Items.Count)
            .Select(index => DossierImplementationCardPolicy.PredecessorIndexes(plan.Items, index))
            .ToList();
        var indexes = WithPredecessors(selected, predecessors).ToList();
        var spawns = indexes.Select(index =>
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

        lock (_gate)
        {
            var previous = ConceptPromotionRecoveryStore.Read(source.FolderPath);
            ConceptPromotionRecoveryStore.Write(source.FolderPath, new ConceptPromotionRecovery
            {
                Reasons = (previous?.Reasons ?? []).Concat(spawns.Select(spawn => spawn.Reason))
                    .Distinct(StringComparer.Ordinal).ToList(),
            });
            var created = CreateCards(source, spawns)
                .Select((card, position) => card with
                {
                    ItemIndex = indexes[position],
                    State = _scanner.FindJob(card.JobId, source.WatchPath)?.State,
                })
                .ToList();
            ConceptPromotionRecoveryStore.Write(source.FolderPath, GetRecovery(source, "cards-recorded"));
            FailurePoint?.Invoke("after-cards-recorded");
            RecordPromotionStep(source, created.Count);
            ConceptPromotionRecoveryStore.Write(source.FolderPath, GetRecovery(source, "completed"));
            return new PromoteConceptTasksResponse
            {
                Source = plan.Source,
                Created = created,
            };
        }
    }

    public ConceptPromotionRecovery GetRecovery(TaskInfo source, string? phase = null)
    {
        var state = ConceptPromotionRecoveryStore.Read(source.FolderPath) ?? new ConceptPromotionRecovery();
        var ledger = SpawnedTaskLedger.Read(source.FolderPath, _logger);
        var created = new List<PromotedConceptTask>();
        foreach (var reason in state.Reasons)
        {
            var recorded = ledger.FirstOrDefault(row => row.Reason == reason);
            var card = _scanner.FindJob(recorded?.TargetJobId ?? recorded?.TargetKey ?? ChildId(source, reason), source.WatchPath);
            if (card is null || (recorded is null && card.CreationSource != reason)) continue;
            created.Add(new PromotedConceptTask { JobId = card.Id, TaskKey = card.Key, Title = card.Title });
        }
        return state with { Phase = phase ?? state.Phase, Created = created };
    }

    public void RecordPhase(TaskInfo source, string phase)
        => ConceptPromotionRecoveryStore.Write(source.FolderPath, GetRecovery(source, phase));

    private static string ChildId(TaskInfo source, string reason)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason)))[..16].ToLowerInvariant();
        return $"concept-{source.Id.ToLowerInvariant()}-{digest}";
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
    /// Promotes one proposal that is not in the Dossier's implementation list,
    /// such as an accepted persona proposal, through the same spawn ledger.
    /// The reason keys the ledger, so a repeated accept returns the same card.
    /// </summary>
    public PromotedConceptTask PromoteProposal(
        TaskInfo source, PromoteConceptResponse plan, ConceptImplementationTask item, string reason)
    {
        var request = CodingRequest(plan.Source, item) with
        {
            Dossier = TaskDossierProvenance.FromImplementation(plan.Source.RepoRelativePath, item, 0)
                with { ImplementationIndex = null },
        };
        return CreateCards(source, [new ConceptCardSpawn(reason, request)]).Single();
    }

    // The Dossier lifecycle follows concept acceptance, never card creation.
    public void MarkDossierAccepted(TaskInfo source, PromoteConceptResponse plan)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(candidate =>
            WatchPathComparison.PathsEqual(candidate.Path, source.WatchPath));
        var root = !string.IsNullOrWhiteSpace(entry?.RepositoryPath)
            ? entry.RepositoryPath : entry?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The concept Dossier repository is unavailable.");
        var relative = Path.GetDirectoryName(plan.Source.RepoRelativePath)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidOperationException("The concept Dossier path is unavailable.");
        var descriptorRelative = relative + "/workbench.json";
        var actor = source.Key ?? source.Id;
        var checkoutReview = ConceptWorkbenchContract.ReviewDirectory(
            root, relative, actor, allowAccepted: true);
        if (!checkoutReview.IsComplete)
        {
            // A Remote merge lands in the integration worktree. Until the
            // developer checkout reaches that commit, promotion reads the
            // Dossier copy placed on the card and records acceptance there.
            var placedCopy = ConceptWorkbenchStore.SnapshotRoot(source.FolderPath);
            var placedDescriptor = Path.Combine(placedCopy, descriptorRelative);
            if (!File.Exists(placedDescriptor))
                throw new InvalidOperationException("The placed concept Dossier copy is unavailable.");
            (bool Changed, string? WorkbenchId, string PreviousStatus) placed;
            lock (_gate)
                placed = WriteAcceptedDescriptor(placedDescriptor, actor);
            PublishAcceptance(source.ProjectName, placed);
            return;
        }

        var descriptorPath = Path.Combine(root, descriptorRelative);
        (bool Changed, string? WorkbenchId, string PreviousStatus) accepted = default;
        var result = _repositoryMutations.Execute(
            source.ProjectName,
            root,
            $"concept-accepted-{actor}",
            $"chore(dossier): accept concept {actor}",
            [descriptorRelative],
            () => accepted = WriteAcceptedDescriptor(descriptorPath, actor));
        if (!result.Success)
            throw new InvalidOperationException(
                result.Error ?? "The concept Dossier could not be marked in implementation.");
        PublishAcceptance(source.ProjectName, accepted);
    }

    private static (bool Changed, string? WorkbenchId, string PreviousStatus) WriteAcceptedDescriptor(
        string path, string actor)
    {
        var descriptor = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("The concept Dossier descriptor is invalid.");
        var workbenchId = descriptor["id"]?.GetValue<string>();
        var schema = descriptor["schemaVersion"]?.GetValue<int>() ?? 1;
        var previousStatus = schema >= 2
            ? "active" : descriptor["status"]?.GetValue<string>() ?? "decision-pending";
        var alreadyAccepted = schema >= 2
            ? descriptor["lifecycleState"]?.GetValue<string>() == "decided"
            : descriptor["status"]?.GetValue<string>() == "decided";
        if (alreadyAccepted) return (false, workbenchId, previousStatus);
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (schema >= 2)
        {
            descriptor["lifecycleState"] = "decided";
            descriptor["editedAt"] = now;
            descriptor["editedBy"] = actor;
            if (descriptor["lifecycleHistory"] is not JsonArray history)
            {
                history = [];
                descriptor["lifecycleHistory"] = history;
            }
            history.Add(AgentStudio.Docs.DecisionRecordService.HistoryEntry(
                "decided", actor, now, "Concept accepted and implementation slices created."));
        }
        else
        {
            descriptor["status"] = "decided";
            descriptor["updatedAt"] = now;
            descriptor["editedBy"] = actor;
        }
        File.WriteAllText(path, descriptor.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return (true, workbenchId, previousStatus);
    }

    private void PublishAcceptance(
        string project, (bool Changed, string? WorkbenchId, string PreviousStatus) accepted)
    {
        if (accepted.Changed && !string.IsNullOrWhiteSpace(accepted.WorkbenchId))
            _workbenchChanges.PublishDecisionRecorded(
                project, accepted.WorkbenchId, accepted.PreviousStatus, "decided");
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
                    var prior = _scanner.FindJob(existing.TargetJobId ?? existing.TargetKey ?? "", source.WatchPath)
                        ?? throw new InvalidOperationException("A recorded implementation card is missing. Restore it before retrying promotion.");
                    if (spawn.Request.Dossier is { } dossier
                        && prior.Dossier is null
                        && !_mutations.SetJobDossier(prior.Id, dossier, prior.WatchPath))
                        throw new IOException("The implementation card's Dossier relation could not be persisted.");
                    EnsureRelations(source, prior, PredecessorKeys(ledger, spawn));
                    promoted.Add(new PromotedConceptTask
                    {
                        JobId = prior.Id,
                        TaskKey = prior.Key,
                        Title = title,
                    });
                    continue;
                }

                var childId = ChildId(source, spawn.Reason);
                var recovered = _scanner.FindJob(childId, source.WatchPath);
                if (recovered is not null && recovered.CreationSource != spawn.Reason)
                    throw new InvalidOperationException("The promotion child identity belongs to another card.");
                var jobId = recovered?.Id ?? _mutations.CreateJob(spawn.Request with
                {
                    Id = childId,
                    CreationSource = spawn.Reason,
                    Title = title,
                    WatchPath = source.WatchPath,
                    Dossier = spawn.Request.Dossier ?? source.Dossier,
                });
                // Another promotion request may have created the stable child
                // while this call waited for the task create lock.
                if (jobId is null)
                {
                    recovered = _scanner.FindJob(childId, source.WatchPath);
                    if (recovered?.CreationSource == spawn.Reason) jobId = recovered.Id;
                }
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException(
                        $"Could not create implementation card {position + 1}.");

                if (ConceptPromotionRecoveryStore.Read(source.FolderPath) is not null)
                    ConceptPromotionRecoveryStore.Write(source.FolderPath, GetRecovery(source));
                FailurePoint?.Invoke("after-card-created");
                var created = _scanner.FindJob(jobId, source.WatchPath);
                if (created?.CreationSource != spawn.Reason)
                    throw new InvalidOperationException("The implementation card could not be verified for recovery.");
                if (created != null)
                {
                    EnsureRelations(source, created, PredecessorKeys(ledger, spawn));
                    created = _scanner.FindJob(jobId, source.WatchPath) ?? created;
                }

                var targetKey = created?.Key ?? jobId;
                if (!SpawnedTaskLedger.Append(source.FolderPath, new SpawnedTaskRecord
                    {
                        At = DateTime.UtcNow,
                        SourceKey = source.Key ?? source.Id,
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

    private void EnsureRelations(TaskInfo source, TaskInfo card, IReadOnlyList<TaskDependencyReference> predecessors)
    {
        var sourceKey = source.Key ?? source.Id;
        var missingSource = !card.References.RelatedTo.Contains(sourceKey, StringComparer.OrdinalIgnoreCase);
        var missingPredecessors = predecessors.Where(edge => card.References.DependsOn.All(current =>
            !string.Equals(current.Key, edge.Key, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!missingSource && missingPredecessors.Count == 0) return;
        var updated = card.References with
        {
            RelatedTo = missingSource ? [.. card.References.RelatedTo, sourceKey] : card.References.RelatedTo,
            DependsOn = [.. card.References.DependsOn, .. missingPredecessors],
        };
        if (!_mutations.SetTaskReferences(card.Id, updated, card.WatchPath))
            throw new IOException($"Could not record relations for implementation card {card.Key ?? card.Id}.");
    }

    /// <summary>
    /// Plain delivery edges to the predecessor cards: a predecessor releases its
    /// dependents once its delivery is integrated, not only when it completes.
    /// </summary>
    private static List<TaskDependencyReference> PredecessorKeys(
        IReadOnlyList<SpawnedTaskRecord> ledger, ConceptCardSpawn spawn)
    {
        var keys = new List<TaskDependencyReference>();
        foreach (var reason in spawn.DependsOnReasons ?? [])
        {
            var record = ledger.FirstOrDefault(entry => string.Equals(entry.Reason, reason, StringComparison.Ordinal));
            if (record?.TargetKey is { Length: > 0 } key) keys.Add(new TaskDependencyReference(key));
            else throw new InvalidDataException(
                $"Implementation card {spawn.Reason} has no promoted predecessor for {reason}.");
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
