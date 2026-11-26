using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>Durable collision receipt, retained when the card moves or the coordinator restarts.</summary>
internal sealed record IntegrationConflictReceipt(
    string DeliverySha, string Target, DateTimeOffset OriginalDeliveredAtUtc,
    MergeIntoIntegrationResult Result, bool RecoveryQueued = false, IReadOnlyList<string>? ReservedPaths = null);

/// <summary>
/// Collects Git and task facts for the conflict policy. This receipt has no TTL:
/// a review id, explicit retry or unrelated target commit cannot erase a collision.
/// </summary>
public sealed class IntegrationConflictRecovery
{
    private const string FileName = "integration-conflict-recovery.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<RemoteDeliveryIntegrationRequest, string?> _tip;
    private readonly Func<RemoteDeliveryIntegrationRequest, string, IReadOnlyList<string>, bool> _touches;
    private readonly Func<RemoteDeliveryIntegrationRequest, IReadOnlyList<string>?> _paths;
    private readonly Func<RemoteDeliveryIntegrationRequest, IReadOnlyList<TaskInfo>> _cards;

    public IntegrationConflictRecovery(GitService git, TaskScannerService scanner)
        : this(request => git.GetBranchTip(Repository(git, request), git.IntegrationBaseRef(Repository(git, request), request.IntegrationBranch)),
            (request, oldTip, paths) => git.TargetTouchedPaths(Repository(git, request), oldTip,
                git.GetBranchTip(Repository(git, request), git.IntegrationBaseRef(Repository(git, request), request.IntegrationBranch)), paths),
            request => git.DeliveryChangedPaths(Repository(git, request), Sha(request), request.IntegrationBranch),
            request => string.IsNullOrWhiteSpace(request.WatchPath) ? []
                : scanner.ScanAutomationJobsWithArchive(request.WatchPath))
    { }

    private static string Repository(GitService git, RemoteDeliveryIntegrationRequest request)
        => git.ResolveRepoRootForWatchPath(request.WatchPath) ?? request.WatchPath ?? string.Empty;

    internal IntegrationConflictRecovery(
        Func<RemoteDeliveryIntegrationRequest, string?> tip,
        Func<RemoteDeliveryIntegrationRequest, string, IReadOnlyList<string>, bool> touches,
        Func<RemoteDeliveryIntegrationRequest, IReadOnlyList<string>?> paths,
        Func<RemoteDeliveryIntegrationRequest, IReadOnlyList<TaskInfo>> cards)
        => (_tip, _touches, _paths, _cards) = (tip, touches, paths, cards);

    private static string Sha(RemoteDeliveryIntegrationRequest request)
        => request.ReviewedResultSha ?? ReviewSubjectStore.Read(request.JobFolderPath)?.ResultSha ?? string.Empty;

    internal MergeIntoIntegrationResult? Replay(RemoteDeliveryIntegrationRequest request)
    {
        if (Read(request.JobFolderPath) is not { } receipt || receipt.Target != request.IntegrationBranch)
            return null;
        var oldTip = receipt.Result.ConflictReport?.IntegrationTipSha ?? receipt.Result.PreviousIntegrationSha;
        var tip = _tip(request);
        var changed = !string.Equals(receipt.DeliverySha, Sha(request), StringComparison.OrdinalIgnoreCase);
        var targetChanged = !string.IsNullOrWhiteSpace(tip) && !string.Equals(oldTip, tip, StringComparison.OrdinalIgnoreCase);
        var files = receipt.Result.ConflictedFiles;
        var mayRetry = MonolithTransitionAdapter.IntegrationConflictRetry(changed, targetChanged,
            !changed && targetChanged && oldTip is not null && files.Count > 0 && _touches(request, oldTip, files));
        return mayRetry ? null : receipt.Result;
    }

    internal void Record(RemoteDeliveryIntegrationRequest request, MergeIntoIntegrationResult result)
    {
        if (result.Outcome is not (MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict))
        {
            // A hold or infrastructure error retains priority. A landed result releases it.
            if (result.Outcome.IsSuccessfulIntegration())
                File.Delete(Path.Combine(request.JobFolderPath, FileName));
            return;
        }
        RecordConflict(request, Sha(request), result);
    }

    private static void RecordConflict(RemoteDeliveryIntegrationRequest request, string sha, MergeIntoIntegrationResult result)
    {
        var previous = Read(request.JobFolderPath);
        var at = previous is { RecoveryQueued: true } && previous.Target == request.IntegrationBranch
            ? previous.OriginalDeliveredAtUtc : request.DeliveredAtUtc;
        var paths = previous is { RecoveryQueued: true } && previous.Target == request.IntegrationBranch
            ? (previous.ReservedPaths ?? previous.Result.ConflictedFiles).Concat(result.ConflictedFiles)
                .Distinct(StringComparer.Ordinal).ToArray() : result.ConflictedFiles;
        Write(request.JobFolderPath, new(sha, request.IntegrationBranch, at, result, ReservedPaths: paths));
    }

    internal static void PrepareQueued(RemoteDeliveryIntegrationRequest request, string sha, MergeIntoIntegrationResult result)
    {
        var previous = Read(request.JobFolderPath);
        if (previous is null || previous.DeliverySha != sha || previous.Target != request.IntegrationBranch)
            RecordConflict(request, sha, result);
        MarkQueued(request.JobFolderPath);
    }

    internal static void MarkQueued(string folder)
    {
        if (Read(folder) is { } receipt) Write(folder, receipt with { RecoveryQueued = true });
    }

    internal IReadOnlyList<RemoteDeliveryIntegrationRequest> Eligible(IReadOnlyList<RemoteDeliveryIntegrationRequest> queue)
    {
        if (queue.Count == 0) return [];
        var reservations = Reservations(queue[0]);
        if (reservations.Count == 0) return queue;
        return queue.Where(request => MonolithTransitionAdapter.IntegrationConflictAdmission(request.JobId,
                request.IntegrationBranch, MonolithTransitionAdapter.IntegrationConflictOrder(request.JobId,
                    request.IntegrationBranch, request.DeliveredAtUtc, reservations), _paths(request), reservations))
            .OrderBy(request => MonolithTransitionAdapter.IntegrationConflictOrder(request.JobId,
                request.IntegrationBranch, request.DeliveredAtUtc, reservations)).ToArray();
    }

    internal IReadOnlyList<RemoteDeliveryIntegrationRequest> Order(IReadOnlyList<RemoteDeliveryIntegrationRequest> queue)
    {
        if (queue.Count == 0) return [];
        var reservations = Reservations(queue[0]);
        return queue.OrderBy(request => MonolithTransitionAdapter.IntegrationConflictOrder(request.JobId,
            request.IntegrationBranch, request.DeliveredAtUtc, reservations)).ToArray();
    }

    private IReadOnlyList<IntegrationRecoveryReservation> Reservations(RemoteDeliveryIntegrationRequest request)
    {
        return _cards(request)
            .Where(card => card.State is TaskStates.Ready or TaskStates.Progress or TaskStates.AutoReview)
            .Select(card => (Card: card, Receipt: Read(card.FolderPath)))
            .Where(item => item.Receipt is { RecoveryQueued: true })
            .Select(item => new IntegrationRecoveryReservation(item.Card.Id, item.Receipt!.Target,
                item.Receipt.OriginalDeliveredAtUtc, item.Receipt.ReservedPaths ?? item.Receipt.Result.ConflictedFiles)).ToArray();
    }

    private static IntegrationConflictReceipt? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<IntegrationConflictReceipt>(File.ReadAllText(path), Json) : null;
    }

    private static void Write(string folder, IntegrationConflictReceipt receipt)
    {
        if (!Directory.Exists(folder)) return;
        var path = Path.Combine(folder, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
