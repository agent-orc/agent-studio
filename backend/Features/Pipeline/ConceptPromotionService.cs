using System.Text.Json.Nodes;
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

        var created = CreateCards(source, spawns).ToList();
        MarkDossierAccepted(source, plan);
        return new PromoteConceptTasksResponse
        {
            Source = plan.Source,
            Created = created,
        };
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
        var descriptorPath = Path.Combine(root, descriptorRelative);
        var changed = false;
        string? workbenchId = null;
        var previousStatus = "decision-pending";
        var result = _repositoryMutations.Execute(
            source.ProjectName,
            root,
            $"concept-accepted-{source.Key ?? source.Id}",
            $"chore(dossier): accept concept {source.Key ?? source.Id}",
            [descriptorRelative],
            () =>
            {
                var descriptor = JsonNode.Parse(File.ReadAllText(descriptorPath))?.AsObject()
                    ?? throw new InvalidDataException("The concept Dossier descriptor is invalid.");
                workbenchId = descriptor["id"]?.GetValue<string>();
                var schema = descriptor["schemaVersion"]?.GetValue<int>() ?? 1;
                if (schema >= 2) previousStatus = "active";
                else previousStatus = descriptor["status"]?.GetValue<string>() ?? "decision-pending";
                var alreadyAccepted = schema >= 2
                    ? descriptor["lifecycleState"]?.GetValue<string>() == "decided"
                    : descriptor["status"]?.GetValue<string>() == "decided";
                if (alreadyAccepted) return;
                changed = true;
                var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var actor = source.Key ?? source.Id;
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
                File.WriteAllText(descriptorPath, descriptor.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            });
        if (!result.Success)
            throw new InvalidOperationException(
                result.Error ?? "The concept Dossier could not be marked in implementation.");
        if (changed && !string.IsNullOrWhiteSpace(workbenchId))
            _workbenchChanges.PublishDecisionRecorded(
                source.ProjectName, workbenchId, previousStatus, "decided");
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
