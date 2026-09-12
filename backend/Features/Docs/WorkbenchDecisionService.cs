using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Persistence;

namespace AgentStudio.Docs;

public sealed record PrepareWorkbenchDecisionRequest
{
    public string OperationId { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? ExpectedRevision { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public string Actor { get; init; } = "";
    public string? ArchiveReason { get; init; }
    public WorkbenchTaskDraft? Task { get; init; }
    public List<WorkbenchDecisionResponse> Responses { get; init; } = [];
}

/// <summary>
/// Confirm repeats the prepared payload: prepare is a pure validation/preview
/// step that writes nothing, so confirm is the single durable write.
/// </summary>
public sealed record ConfirmWorkbenchDecisionRequest
{
    public string OperationId { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? ExpectedRevision { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public string Actor { get; init; } = "";
    public string? ArchiveReason { get; init; }
    public WorkbenchTaskDraft? Task { get; init; }
    public List<WorkbenchDecisionResponse> Responses { get; init; } = [];
    /// <summary>Cards the caller already created for this decision, if any.</summary>
    public string[]? SpawnedTaskKeys { get; init; }
    public string? CliType { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public bool Confirmed { get; init; }
}

public sealed record WorkbenchDecisionResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string WorkbenchId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string? Outcome { get; init; }
    public string? DecisionStage { get; init; }
    public string? Revision { get; init; }
    public string? Fingerprint { get; init; }
    public string[] SpawnedTaskKeys { get; init; } = [];
    public List<WorkbenchDecisionResponse> Responses { get; init; } = [];
    public bool Idempotent { get; init; }
    /// <summary>
    /// The server-validated draft of a feature decision. This service never
    /// creates the card: task creation stays on the existing task API, and the
    /// caller may report the resulting keys back through
    /// <see cref="ConfirmWorkbenchDecisionRequest.SpawnedTaskKeys"/>.
    /// </summary>
    public WorkbenchTaskDraft? TaskDraft { get; init; }
}

/// <summary>
/// The write half of the Dossier Decision gate (AGT-2375). Deliberately
/// small: <see cref="Prepare"/> validates and fingerprints without touching the
/// disk, <see cref="Confirm"/> writes the decision straight into the Dossier's
/// own <c>workbench.json</c>. Visibility hangs on that descriptor - never on a
/// <c>.meta.json</c> sidecar, which was the archive bug this card fixes.
/// </summary>
public sealed class WorkbenchDecisionService
{
    private static readonly JsonSerializerOptions DraftJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// One write gate per Dossier descriptor. <see cref="Confirm"/> is a
    /// check-then-act over the descriptor's fingerprint, so two concurrent
    /// confirmations of the same Dossier must not interleave between the
    /// check and the write. The key is the descriptor's own normalized path,
    /// not the (project, id) pair, so two spellings of the same project cannot
    /// acquire two different gates. The map is bounded by the number of
    /// Dossier descriptors that were ever confirmed in this process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConfirmGates =
        new(StringComparer.Ordinal);

    private readonly WorkbenchCatalogueService _catalogue;
    private readonly ManagedRepositoryMutationService _repositoryMutations;
    private readonly IAtomicJsonFileWriter _fileWriter;
    private readonly WorkbenchChangeNotifier? _notifier;

    public WorkbenchDecisionService(
        WorkbenchCatalogueService catalogue,
        GitService git,
        IAtomicJsonFileWriter? fileWriter = null,
        WorkbenchChangeNotifier? notifier = null,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _catalogue = catalogue;
        _fileWriter = fileWriter ?? new AtomicJsonFileWriter();
        _notifier = notifier;
        _repositoryMutations = repositoryMutations
            ?? new ManagedRepositoryMutationService(git);
    }

    public WorkbenchDecisionResult Prepare(
        string projectName, string id, PrepareWorkbenchDecisionRequest body)
    {
        var gate = Gate(projectName, id, body.OperationId, body.Outcome, body.Actor,
            body.ArchiveReason, body.Task, body.Responses,
            body.ExpectedRevision, body.ExpectedFingerprint);
        if (gate.Failure != null) return gate.Failure;
        var snapshot = gate.Snapshot!;
        return new WorkbenchDecisionResult
        {
            Success = true,
            WorkbenchId = id,
            OperationId = body.OperationId,
            Outcome = body.Outcome,
            DecisionStage = "prepared",
            Revision = snapshot.Revision,
            Fingerprint = snapshot.Fingerprint,
            TaskDraft = gate.Draft,
            Responses = body.Responses,
        };
    }

    /// <summary>
    /// Serializes on the target descriptor and then performs the single durable
    /// write. Without the gate the fingerprint check and the write are an
    /// unguarded check-then-act: two confirmations taken on the same revision
    /// would both pass the gate and the second would silently overwrite the
    /// first decision.
    /// </summary>
    public WorkbenchDecisionResult Confirm(
        string projectName, string id, ConfirmWorkbenchDecisionRequest body)
    {
        if (!body.Confirmed)
            return Failure(id, body.OperationId, "validation",
                "The decision must be explicitly confirmed.");
        if (body.Outcome is "feature-spawn" or "rework")
        {
            if (body.CliType != null && !CliTypes.IsValid(body.CliType))
                return Failure(id, body.OperationId, "validation", "cliType is invalid.");
            if (body.Model is { Length: > 200 })
                return Failure(id, body.OperationId, "validation", "model must be at most 200 characters.");
            if (body.ThinkingLevel is { Length: > 80 })
                return Failure(id, body.OperationId, "validation", "thinkingLevel must be at most 80 characters.");
        }

        var gateKey = ConfirmGateKey(projectName, id);
        var writeGate = ConfirmGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        writeGate.Wait();
        try
        {
            return ConfirmSerialized(projectName, id, body);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    /// The descriptor path the confirmation will write, normalized into a
    /// stable lock key. Falls back to the requested identity when nothing
    /// canonical resolves - <see cref="Gate"/> refuses that case anyway, the
    /// key only needs to be consistent.
    /// </summary>
    private string ConfirmGateKey(string projectName, string id)
    {
        var descriptorPath = _catalogue.ResolveCanonicalForMutation(projectName, id)?.DescriptorPath;
        if (descriptorPath == null) return $"unresolved:{projectName}/{id}";
        string full;
        try { full = Path.GetFullPath(descriptorPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            SilentCatch.Note(ex, "Dossier descriptor path could not be normalized for the write gate.");
            full = descriptorPath;
        }
        full = full.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    private WorkbenchDecisionResult ConfirmSerialized(
        string projectName, string id, ConfirmWorkbenchDecisionRequest body)
    {
        var gate = Gate(projectName, id, body.OperationId, body.Outcome, body.Actor,
            body.ArchiveReason, body.Task, body.Responses,
            body.ExpectedRevision, body.ExpectedFingerprint,
            body.SpawnedTaskKeys);
        if (gate.Failure != null) return gate.Failure;
        var snapshot = gate.Snapshot!;
        if (snapshot.Revision == null && snapshot.Fingerprint == null)
            return Failure(id, body.OperationId, "validation",
                "The Dossier has no readable revision or fingerprint provenance.");

        var archive = body.Outcome == "archive";
        var rework = body.Outcome == "rework";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var actor = body.Actor.Trim();
        var note = rework
            ? "Revision requested"
            : archive
                ? $"Archived: {body.ArchiveReason!.Trim()}"
                : $"Decided to build a feature: {gate.Draft!.Title.Trim()}";
        var spawned = (body.SpawnedTaskKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .ToArray();

        var descriptor = snapshot.Descriptor;
        var receipt = new JsonObject
        {
            ["outcome"] = body.Outcome,
            ["action"] = rework ? "rework-requested" : archive ? "archived" : "created-card",
            ["state"] = rework ? "pending" : "succeeded",
            ["operationId"] = body.OperationId,
            ["sourceRevision"] = snapshot.Revision,
            ["sourceFingerprint"] = snapshot.Fingerprint,
            ["sourceEntryFingerprint"] = WorkbenchCatalogueService.ComputeEntryFingerprint(snapshot.EntryPath),
            ["preparedAt"] = now,
            ["preparedBy"] = actor,
            ["confirmedAt"] = now,
            ["confirmedBy"] = actor,
            ["decidedAt"] = rework ? null : now,
            ["spawnedTaskKeys"] = new JsonArray(spawned.Select(key => (JsonNode)key!).ToArray()),
            ["responses"] = JsonSerializer.SerializeToNode(body.Responses, DraftJson),
            ["cliType"] = body.CliType?.Trim(),
            ["model"] = body.Model?.Trim(),
            ["thinkingLevel"] = body.ThinkingLevel?.Trim(),
        };
        if (archive) receipt["reason"] = body.ArchiveReason!.Trim();
        else if (!rework) receipt["taskDraft"] = JsonSerializer.SerializeToNode(gate.Draft, DraftJson);
        descriptor["decision"] = receipt;
        if (spawned.Length > 0)
        {
            var related = descriptor["relatedTaskKeys"] is JsonArray existing
                ? existing
                : [];
            var known = related
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var key) ? key : null)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in spawned)
                if (known.Add(key)) related.Add(key);
            descriptor["relatedTaskKeys"] = related;
        }

        if (snapshot.SchemaVersion >= 2)
        {
            // schema v2: the lifecycle projection is the visibility source.
            // Archive settles into "done" (projected as "archived"), a feature
            // decision into "decided" - the two settled states the catalogue
            // accepts for a succeeded receipt.
            var lifecycleState = rework
                ? snapshot.Item.LifecycleState ?? "review-requested"
                : archive ? "done" : "decided";
            descriptor["lifecycleState"] = lifecycleState;
            descriptor["editedBy"] = actor;
            descriptor["editedAt"] = now;
            if (descriptor["lifecycleHistory"] is not JsonArray history)
            {
                history = [];
                descriptor["lifecycleHistory"] = history;
            }
            history.Add(new JsonObject
            {
                ["state"] = lifecycleState,
                ["editedBy"] = actor,
                ["editedAt"] = now,
                ["note"] = note,
            });
        }
        else
        {
            // schema v1 still carries most Dossiers. Its visibility hangs on
            // the flat status field; the receipt above rides along so a later
            // migration to v2 keeps the provenance.
            descriptor["status"] = rework ? "decision-pending" : archive ? "archived" : "decided";
            descriptor["updatedAt"] = now;
            descriptor["editedBy"] = actor;
            if (rework)
            {
                if (descriptor["lifecycleHistory"] is not JsonArray history)
                {
                    history = [];
                    descriptor["lifecycleHistory"] = history;
                }
                history.Add(new JsonObject
                {
                    ["state"] = "review-requested",
                    ["editedBy"] = actor,
                    ["editedAt"] = now,
                    ["note"] = note,
                });
            }
        }

        // Last check before the write, inside the gate: re-read the descriptor
        // and hold it against the caller's expectation. The gate keeps two
        // confirmations apart, this keeps any writer outside the gate (a git
        // checkout, an editor, a hand edit) from being overwritten by a
        // decision that was taken on bytes which no longer exist.
        var currentText = TryReadDescriptorText(snapshot.DescriptorPath);
        if (currentText == null
            || WorkbenchCatalogueService.ComputeDescriptorFingerprint(currentText)
                != snapshot.DescriptorFingerprint)
            return Failure(id, body.OperationId, "stale-revision",
                "The Dossier descriptor changed while the decision was being confirmed.");
        if (body.ExpectedFingerprint != null
            && WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath) != body.ExpectedFingerprint)
            return Failure(id, body.OperationId, "stale-revision",
                "The Dossier content changed while the decision was being confirmed.");

        var mutation = _repositoryMutations.Execute(
            projectName,
            snapshot.Root,
            $"workbench-decision-{id}",
            $"chore(workbench): {(rework ? "request revision for" : archive ? "archive" : "record decision for")} {id}",
            [snapshot.DescriptorRelPath],
            () => _fileWriter.Write(snapshot.DescriptorPath, descriptor.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));
        if (!mutation.Success)
        {
            return Failure(id, body.OperationId, "write-failed",
                $"The Dossier decision could not be persisted: {mutation.Error}");
        }

        var revision = mutation.CommitSha ?? snapshot.Revision;
        var currentStatus = rework ? "decision-pending" : archive ? "archived" : "decided";
        _notifier?.PublishDecisionRecorded(projectName, id, snapshot.Item.Status, currentStatus);

        return new WorkbenchDecisionResult
        {
            Success = true,
            WorkbenchId = id,
            OperationId = body.OperationId,
            Outcome = body.Outcome,
            DecisionStage = rework ? "pending" : archive ? "archived" : "succeeded",
            Revision = revision,
            Fingerprint = WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath),
            SpawnedTaskKeys = spawned,
            Responses = body.Responses,
            TaskDraft = gate.Draft,
        };
    }

    private sealed record GateResult(
        WorkbenchDecisionResult? Failure,
        WorkbenchMutationSnapshot? Snapshot = null,
        WorkbenchTaskDraft? Draft = null);

    /// <summary>
    /// The shared admission check of both phases: payload shape, canonical
    /// ownership, idempotency, a clean working tree, and the caller's expected
    /// revision/fingerprint against the descriptor's current bytes.
    /// <paramref name="spawnedTaskKeys"/> only reaches this from
    /// <see cref="Confirm"/>; <see cref="Prepare"/> has no such field.
    /// </summary>
    private GateResult Gate(
        string projectName, string id, string operationId, string outcome, string actor,
        string? archiveReason, WorkbenchTaskDraft? task,
        IReadOnlyList<WorkbenchDecisionResponse> responses,
        string? expectedRevision, string? expectedFingerprint,
        string[]? spawnedTaskKeys = null)
    {
        if (!WorkbenchDecisionContracts.SafeOperationId(operationId))
            return new(Failure(id, operationId, "validation", "operationId is malformed."));
        if (outcome is not ("feature-spawn" or "archive" or "rework"))
            return new(Failure(id, operationId, "validation", $"Unsupported outcome '{outcome}'."));
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 120)
            return new(Failure(id, operationId, "validation", "actor is required."));
        var responsesError = WorkbenchDecisionContracts.ValidateResponses(
            responses,
            allowPartial: outcome == "rework");
        if (responsesError != null)
            return new(Failure(id, operationId, "validation", responsesError));

        WorkbenchTaskDraft? draft = null;
        if (outcome == "archive")
        {
            if (string.IsNullOrWhiteSpace(archiveReason) || archiveReason.Trim().Length > 20_000)
                return new(Failure(id, operationId, "validation",
                    "archiveReason is required for an archive decision."));
            if (task != null)
                return new(Failure(id, operationId, "validation",
                    "An archive decision cannot carry a task draft."));
            // The read side refuses a settled archive receipt that carries
            // spawned keys, so accepting them here would write a descriptor
            // that can never be read back: the Dossier would be permanently
            // invalid. Refuse on the way in instead.
            if (spawnedTaskKeys != null
                && spawnedTaskKeys.Any(key => !string.IsNullOrWhiteSpace(key)))
                return new(Failure(id, operationId, "validation",
                    "An archive decision cannot carry spawned task keys."));
        }
        else if (outcome == "feature-spawn")
        {
            if (!string.IsNullOrWhiteSpace(archiveReason))
                return new(Failure(id, operationId, "validation",
                    "A feature decision cannot carry an archive reason."));
            var draftError = WorkbenchDecisionContracts.ValidateTaskDraft(task);
            if (draftError != null)
                return new(Failure(id, operationId, "validation", $"Feature decision {draftError}"));
            draft = task;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(archiveReason) || task != null)
                return new(Failure(id, operationId, "validation",
                    "A rework request cannot carry an archive reason or task draft."));
            if (!responses.Any(response => !string.IsNullOrWhiteSpace(response.Comment)))
                return new(Failure(id, operationId, "validation",
                    "A rework request needs a comment."));
            if (spawnedTaskKeys != null
                && spawnedTaskKeys.Any(key => !string.IsNullOrWhiteSpace(key)))
                return new(Failure(id, operationId, "validation",
                    "A rework request cannot carry spawned task keys."));
        }

        var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, id);
        if (snapshot == null)
            return new(Failure(id, operationId, "not-canonical",
                "No single canonical Dossier descriptor owns this id."));

        var stored = snapshot.Item.Decision;
        if (stored is { State: "succeeded" })
            return stored.OperationId == operationId
                ? new(new WorkbenchDecisionResult
                {
                    Success = true,
                    WorkbenchId = id,
                    OperationId = operationId,
                    Outcome = stored.Outcome,
                    DecisionStage = stored.Outcome == "archive" ? "archived" : "succeeded",
                    Revision = snapshot.Revision,
                    Fingerprint = snapshot.Fingerprint,
                    SpawnedTaskKeys = stored.SpawnedTaskKeys,
                    Responses = stored.Responses,
                    Idempotent = true,
                })
                : new(Failure(id, operationId, "already-settled",
                    "This Dossier already carries a settled decision."));
        if (snapshot.Item.Status is "decided" or "documented" or "archived")
            return new(Failure(id, operationId, "already-settled",
                "This Dossier already carries a settled decision."));
        if (_catalogue.OperationIdOwnedByAnotherWorkbench(projectName, operationId, id))
            return new(Failure(id, operationId, "operation-id-conflict",
                "This operationId belongs to a different Dossier."));
        if (snapshot.Dirty)
            return new(Failure(id, operationId, "dirty-descriptor",
                "Commit the Dossier descriptor and artifact before deciding."));
        var staleness = WorkbenchDecisionContracts.StalenessError(
            expectedRevision, expectedFingerprint, snapshot.Revision, snapshot.Fingerprint);
        if (staleness != null)
            return new(Failure(id, operationId, "stale-revision", staleness));

        return new(null, snapshot, draft);
    }

    private static string? TryReadDescriptorText(string descriptorPath)
    {
        try
        {
            return File.ReadAllText(descriptorPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Dossier descriptor could not be re-read before the decision write.");
            return null;
        }
    }

    private static WorkbenchDecisionResult Failure(
        string id, string operationId, string code, string error) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Error = error,
            WorkbenchId = id,
            OperationId = operationId,
        };
}
