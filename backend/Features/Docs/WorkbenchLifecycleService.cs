using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

public sealed record DocumentWorkbenchRequest
{
    public string Actor { get; init; } = "";
    public string? ExpectedRevision { get; init; }
    public string? ExpectedFingerprint { get; init; }
}

public sealed record DocumentWorkbenchResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string WorkbenchId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Revision { get; init; }
    public string? Fingerprint { get; init; }
    public bool Idempotent { get; init; }
}

/// <summary>
/// Coordinates the terminal lifecycle transition. The eligibility decision is
/// projected by <see cref="WorkbenchDocumentationPolicy"/> and checked again
/// inside the descriptor's write gate before one atomic descriptor swap.
/// </summary>
public sealed class WorkbenchLifecycleService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.Ordinal);

    private readonly WorkbenchCatalogueService _catalogue;
    private readonly ManagedRepositoryMutationService _repositoryMutations;
    private readonly IAtomicJsonFileWriter _writer;

    public WorkbenchLifecycleService(
        WorkbenchCatalogueService catalogue,
        GitService git,
        IAtomicJsonFileWriter? writer = null,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _catalogue = catalogue;
        _writer = writer ?? new AtomicJsonFileWriter();
        _repositoryMutations = repositoryMutations
            ?? new ManagedRepositoryMutationService(git);
    }

    public DocumentWorkbenchResult Document(
        string projectName,
        string id,
        DocumentWorkbenchRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Actor) || body.Actor.Trim().Length > 120)
            return Failure(id, "validation", "actor is required.");

        var initial = _catalogue.ResolveCanonicalForMutation(projectName, id);
        if (initial == null)
            return Failure(id, "not-canonical", "No single canonical descriptor owns this item.");

        var gate = WriteGates.GetOrAdd(GateKey(initial.DescriptorPath), _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return DocumentSerialized(projectName, id, body);
        }
        finally
        {
            gate.Release();
        }
    }

    private DocumentWorkbenchResult DocumentSerialized(
        string projectName,
        string id,
        DocumentWorkbenchRequest body)
    {
        var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, id);
        if (snapshot == null)
            return Failure(id, "not-canonical", "No single canonical descriptor owns this item.");
        if (snapshot.Item.Status == "documented")
            return Success(snapshot, id, idempotent: true);
        if (snapshot.Item.Status != "decided")
            return Failure(id, "invalid-transition", "Only a decided item can be marked as documented.");
        if (snapshot.Item.Documentation is not { Eligible: true })
            return Failure(id, "references-not-terminal",
                "Every referenced card must exist and be completed or archived.");
        if (snapshot.Dirty)
            return Failure(id, "dirty-descriptor",
                "Commit the descriptor and artifact before changing the lifecycle.");
        if (body.ExpectedRevision == null && body.ExpectedFingerprint == null)
            return Failure(id, "stale-revision",
                "The transition must name the revision or fingerprint it was taken on.");
        if (body.ExpectedRevision != null && body.ExpectedRevision != snapshot.Revision)
            return Failure(id, "stale-revision", "The item moved since the transition was requested.");
        if (body.ExpectedFingerprint != null && body.ExpectedFingerprint != snapshot.Fingerprint)
            return Failure(id, "stale-revision", "The content changed since the transition was requested.");

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var actor = body.Actor.Trim();
        var descriptor = snapshot.Descriptor;
        if (snapshot.SchemaVersion >= 2)
        {
            descriptor["lifecycleState"] = "documented";
            descriptor["editedBy"] = actor;
            descriptor["editedAt"] = now;
            if (descriptor["lifecycleHistory"] is not JsonArray history)
            {
                history = [];
                descriptor["lifecycleHistory"] = history;
            }
            history.Add(new JsonObject
            {
                ["state"] = "documented",
                ["editedBy"] = actor,
                ["editedAt"] = now,
                ["note"] = "Referenced cards reached terminal states; the item is now documented.",
            });
        }
        else
        {
            // Schema v1 owns visibility through this field. This is the same
            // descriptor path used by the archive correction from AGT-2375;
            // no Wiki classification sidecar participates in the transition.
            descriptor["status"] = "documented";
            descriptor["updatedAt"] = now;
            descriptor["editedBy"] = actor;
        }

        var currentText = TryRead(snapshot.DescriptorPath);
        if (currentText == null
            || WorkbenchCatalogueService.ComputeDescriptorFingerprint(currentText)
                != snapshot.DescriptorFingerprint)
            return Failure(id, "stale-revision",
                "The descriptor changed while the lifecycle transition was being applied.");
        if (body.ExpectedFingerprint != null
            && WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath) != body.ExpectedFingerprint)
            return Failure(id, "stale-revision",
                "The content changed while the lifecycle transition was being applied.");

        var mutation = _repositoryMutations.Execute(
            projectName,
            snapshot.Root,
            $"workbench-lifecycle-{id}",
            $"chore(workbench): document {id}",
            [snapshot.DescriptorRelPath],
            () => _writer.Write(snapshot.DescriptorPath, descriptor.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));
        if (!mutation.Success)
            return Failure(id, "write-failed", $"The descriptor could not be persisted: {mutation.Error}");

        return new DocumentWorkbenchResult
        {
            Success = true,
            WorkbenchId = id,
            Status = "documented",
            Revision = mutation.CommitSha ?? snapshot.Revision,
            Fingerprint = WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath),
        };
    }

    private static DocumentWorkbenchResult Success(
        WorkbenchMutationSnapshot snapshot,
        string id,
        bool idempotent) => new()
    {
        Success = true,
        WorkbenchId = id,
        Status = "documented",
        Revision = snapshot.Revision,
        Fingerprint = snapshot.Fingerprint,
        Idempotent = idempotent,
    };

    private static DocumentWorkbenchResult Failure(string id, string code, string error) => new()
    {
        Success = false,
        ErrorCode = code,
        Error = error,
        WorkbenchId = id,
    };

    private static string GateKey(string descriptorPath)
    {
        string full;
        try { full = Path.GetFullPath(descriptorPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            SilentCatch.Note(ex, "Descriptor path could not be normalized for the lifecycle write gate.");
            full = descriptorPath;
        }
        full = full.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    private static string? TryRead(string descriptorPath)
    {
        try { return File.ReadAllText(descriptorPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Descriptor could not be re-read before the lifecycle write.");
            return null;
        }
    }
}
