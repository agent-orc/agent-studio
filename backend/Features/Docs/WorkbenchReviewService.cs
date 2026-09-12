using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentStudio.Persistence;

namespace AgentStudio.Docs;

public sealed record RecordWorkbenchReviewRequest
{
    public string Verdict { get; init; } = "";
    public string[] SupersededBy { get; init; } = [];
    public string ReviewedBy { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed record WorkbenchReviewResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    string WorkbenchId,
    WorkbenchReviewProjection? Review,
    string? Revision);

public static partial class WorkbenchReviewContracts
{
    private static readonly HashSet<string> Verdicts = new(StringComparer.Ordinal)
        { "current", "partially-superseded", "superseded", "historical" };

    [GeneratedRegex("^[A-Z][A-Z0-9-]{1,39}-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskKey();

    [GeneratedRegex("^[A-Z][A-Z0-9-]{1,39}-W[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DossierKey();

    public static bool ValidReviewer(string value) =>
        value == "Operator" || TaskKey().IsMatch(value);

    public static string? Validate(string verdict, IReadOnlyList<string>? supersededBy, string note)
    {
        if (!Verdicts.Contains(verdict)) return "verdict is invalid.";
        if (supersededBy == null || supersededBy.Count > 100
            || supersededBy.Any(key => !DossierKey().IsMatch(key))
            || supersededBy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != supersededBy.Count)
            return "supersededBy needs unique Dossier keys.";
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length > 500
            || note.Contains('\n') || note.Contains('\r'))
            return "note is required, must be one line, and must be at most 500 characters.";
        if ((verdict is "partially-superseded" or "superseded") && supersededBy.Count == 0)
            return "supersededBy is required for a superseded verdict.";
        return null;
    }
}

public sealed class WorkbenchReviewService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly ManagedRepositoryMutationService _mutations;
    private readonly IAtomicJsonFileWriter _writer;
    private readonly WorkbenchChangeNotifier? _notifier;

    public WorkbenchReviewService(
        WorkbenchCatalogueService catalogue,
        GitService git,
        IAtomicJsonFileWriter? writer = null,
        ManagedRepositoryMutationService? mutations = null,
        WorkbenchChangeNotifier? notifier = null)
    {
        _catalogue = catalogue;
        _writer = writer ?? new AtomicJsonFileWriter();
        _mutations = mutations ?? new ManagedRepositoryMutationService(git);
        _notifier = notifier;
    }

    public WorkbenchReviewResult Record(string projectName, string id, RecordWorkbenchReviewRequest body)
    {
        var validation = WorkbenchReviewContracts.Validate(body.Verdict, body.SupersededBy, body.Note);
        if (validation != null || !WorkbenchReviewContracts.ValidReviewer(body.ReviewedBy))
            return Failure(id, validation ?? "reviewedBy must be Operator or a task key.");
        var initial = _catalogue.ResolveCanonicalForMutation(projectName, id);
        if (initial == null) return Failure(id, "The Dossier is not writable or its descriptor is invalid.", "not-found");
        var gate = Gates.GetOrAdd(initial.DescriptorPath, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, id);
            if (snapshot == null) return Failure(id, "The Dossier changed before the review could be written.", "stale-revision");
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var review = new WorkbenchReviewProjection(
                body.Verdict,
                body.SupersededBy.Select(key => key.Trim()).ToArray(),
                now,
                body.ReviewedBy.Trim(),
                body.Note.Trim());
            var node = new JsonObject
            {
                ["verdict"] = review.Verdict,
                ["supersededBy"] = new JsonArray(review.SupersededBy.Select(key => (JsonNode)key).ToArray()),
                ["reviewedAt"] = review.ReviewedAt,
                ["reviewedBy"] = review.ReviewedBy,
                ["note"] = review.Note,
            };
            snapshot.Descriptor["review"] = node.DeepClone();
            if (snapshot.Descriptor["reviewHistory"] is not JsonArray history)
            {
                history = [];
                snapshot.Descriptor["reviewHistory"] = history;
            }
            history.Add(node.DeepClone());
            var mutation = _mutations.Execute(
                projectName,
                snapshot.Root,
                $"workbench-review-{id}",
                $"chore(workbench): record relevance review for {id}",
                [snapshot.DescriptorRelPath],
                () => _writer.Write(snapshot.DescriptorPath, snapshot.Descriptor.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true })));
            if (!mutation.Success)
                return Failure(id, $"The Dossier review could not be persisted: {mutation.Error}", "write-failed");
            _notifier?.PublishReviewRecorded(projectName, id);
            return new(true, null, null, id, review, mutation.CommitSha ?? snapshot.Revision);
        }
        finally { gate.Release(); }
    }

    private static WorkbenchReviewResult Failure(string id, string error, string code = "validation") =>
        new(false, code, error, id, null, null);
}
