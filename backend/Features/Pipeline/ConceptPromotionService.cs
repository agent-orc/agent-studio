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
/// </summary>
public sealed class ConceptPromotionService
{
    private const string ReasonPrefix = "concept-promotion:";

    private readonly AgentStudio.Tasks.TaskScannerService _scanner;
    private readonly AgentStudio.Tasks.TaskMutationService _mutations;
    private readonly ManagedRepositoryMutationService _repositoryMutations;
    private readonly AgentStudio.Docs.WorkbenchChangeNotifier _workbenchChanges;
    private readonly ILogger<ConceptPromotionService> _logger;
    private readonly object _gate = new();
    internal Action<string>? FailurePoint { get; set; }

    public ConceptPromotionService(
        AgentStudio.Tasks.TaskScannerService scanner,
        AgentStudio.Tasks.TaskMutationService mutations,
        ManagedRepositoryMutationService repositoryMutations,
        AgentStudio.Docs.WorkbenchChangeNotifier workbenchChanges,
        ILogger<ConceptPromotionService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _repositoryMutations = repositoryMutations;
        _workbenchChanges = workbenchChanges;
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

        lock (_gate)
        {
            var previous = ConceptPromotionRecoveryStore.Read(source.FolderPath);
            ConceptPromotionRecoveryStore.Write(source.FolderPath, new ConceptPromotionRecovery
            {
                Reasons = (previous?.Reasons ?? []).Concat(spawns.Select(spawn => spawn.Reason))
                    .Distinct(StringComparer.Ordinal).ToList(),
            });
            var created = CreateCards(source, spawns).ToList();
            ConceptPromotionRecoveryStore.Write(source.FolderPath, GetRecovery(source, "cards-recorded"));
            FailurePoint?.Invoke("after-cards-recorded");
            MarkDossierAccepted(source, plan);
            ConceptPromotionRecoveryStore.Write(source.FolderPath, GetRecovery(source, "dossier-accepted"));
            FailurePoint?.Invoke("after-dossier-accepted");
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

    private void MarkDossierAccepted(TaskInfo source, PromoteConceptResponse plan)
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
                    var prior = _scanner.FindJob(existing.TargetJobId ?? existing.TargetKey ?? "", source.WatchPath)
                        ?? throw new InvalidOperationException("A recorded implementation card is missing. Restore it before retrying promotion.");
                    if (spawn.Request.Dossier is { } dossier)
                    {
                        if (prior.Dossier is null
                            && !_mutations.SetJobDossier(prior.Id, dossier, prior.WatchPath))
                            throw new IOException("The implementation card's Dossier relation could not be persisted.");
                    }
                    EnsureSourceRelation(source, prior);
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
                    EnsureSourceRelation(source, created);
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

    private void EnsureSourceRelation(TaskInfo source, TaskInfo card)
    {
        var sourceKey = source.Key ?? source.Id;
        if (card.References.RelatedTo.Contains(sourceKey, StringComparer.OrdinalIgnoreCase)) return;
        var related = card.References.RelatedTo.Append(sourceKey).ToList();
        if (!_mutations.SetTaskReferences(card.Id, card.References with { RelatedTo = related }, card.WatchPath))
            throw new IOException("The implementation card was created, but its source relation could not be persisted.");
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
