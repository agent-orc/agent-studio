using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Areas;
using AgentStudio.Persistence;

namespace AgentStudio.Docs;

/// <summary>Body of <c>PUT /api/projects/{project}/workbenches/{id}/tags</c>: replace-all.</summary>
public sealed record SetWorkbenchTagsRequest
{
    public List<string> Tags { get; init; } = [];
    public string? TaggingStatus { get; init; }
}

public sealed record WorkbenchTagsResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    string WorkbenchId,
    string[] Tags,
    string? Revision);

/// <summary>
/// Writes the <c>tags[]</c> field of a Dossier descriptor (AGT-2803). Unknown
/// tag ids are refused at this boundary against the project's effective tag
/// vocabulary, so the descriptor can only ever gain ids the registry knows;
/// reads stay lenient so retiring a tag never invalidates a Dossier.
///
/// The write follows the same shape as the review write: one per-descriptor
/// gate, one atomic JSON write, one path-scoped managed commit.
/// </summary>
public sealed class WorkbenchTagService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly AreaRegistryService _areas;
    private readonly ManagedRepositoryMutationService _mutations;
    private readonly IAtomicJsonFileWriter _writer;

    public WorkbenchTagService(
        WorkbenchCatalogueService catalogue,
        AreaRegistryService areas,
        GitService git,
        IAtomicJsonFileWriter? writer = null,
        ManagedRepositoryMutationService? mutations = null)
    {
        _catalogue = catalogue;
        _areas = areas;
        _writer = writer ?? new AtomicJsonFileWriter();
        _mutations = mutations ?? new ManagedRepositoryMutationService(git);
    }

    public WorkbenchTagsResult Set(string projectName, string id, SetWorkbenchTagsRequest body)
    {
        if (body?.TaggingStatus is not (null or "tagged" or "tags-proposed"))
            return Failure(id, "Unknown tagging status.");
        var validation = _areas.ValidateTags(projectName, body?.Tags);
        if (!validation.Ok) return Failure(id, validation.Error);
        if (validation.TagIds.Count > 50)
            return Failure(id, "A Dossier carries at most 50 tags.");

        var initial = _catalogue.ResolveCanonicalForMutation(projectName, id);
        if (initial == null)
            return Failure(id, "The Dossier is not writable or its descriptor is invalid.", "not-found");

        var gate = Gates.GetOrAdd(initial.DescriptorPath, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, id);
            if (snapshot == null)
                return Failure(id, "The Dossier changed before its tags could be written.", "stale-revision");

            snapshot.Descriptor["tags"] = new JsonArray(
                validation.TagIds.Select(tag => (JsonNode)tag).ToArray());
            if (body?.TaggingStatus != null)
                snapshot.Descriptor["taggingStatus"] = body.TaggingStatus;
            var mutation = _mutations.Execute(
                projectName,
                snapshot.Root,
                $"workbench-tags-{id}",
                $"chore(workbench): set tags for {id}",
                [snapshot.DescriptorRelPath],
                () => _writer.Write(snapshot.DescriptorPath, snapshot.Descriptor.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true })));
            if (!mutation.Success)
                return Failure(id, $"The Dossier tags could not be persisted: {mutation.Error}", "write-failed");
            return new(true, null, null, id, [.. validation.TagIds], mutation.CommitSha ?? snapshot.Revision);
        }
        finally { gate.Release(); }
    }

    private static WorkbenchTagsResult Failure(string id, string error, string code = "validation") =>
        new(false, code, error, id, [], null);
}
