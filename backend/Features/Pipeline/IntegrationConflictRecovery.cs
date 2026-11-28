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

    internal static bool SaveMechanicalFallbackBase(GitService git, string? repoRoot, string folder,
        ContinuationBaseRecord result, string? attemptBaseSha)
    {
        var pin = ContinuationBaseStore.Read(folder);
        if (pin is not { RetainUntilIntegrated: true }
            || string.Equals(result.CommitSha, pin.CommitSha, StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.CommitSha, attemptBaseSha, StringComparison.OrdinalIgnoreCase))
            return ContinuationBaseStore.SaveAttemptResult(folder, result);

        var verified = repoRoot is not null
            && git.VerifyDeliveredCommit(repoRoot, result.Ref, result.CommitSha).Status == DeliveryVerificationStatus.Verified
            && git.IsAncestor(repoRoot, pin.CommitSha, result.CommitSha);
        return verified
            ? ContinuationBaseStore.SaveAttemptResult(folder, result, pin)
            : true;
    }

    internal MergeIntoIntegrationResult? Replay(RemoteDeliveryIntegrationRequest request)
    {
        var receipt = Read(request.JobFolderPath, out var unreadable);
        if (unreadable)
            return MergeIntoIntegrationResult.Failed(AcceptedIntegrationFailureCodes.IntegrationError,
                $"integration-conflict-receipt-unreadable: {FileName} could not be read; repair the receipt before retrying integration.");
        if (receipt is null || receipt.Target != request.IntegrationBranch)
            return null;
        var oldTip = receipt.Result.ConflictReport?.IntegrationTipSha ?? receipt.Result.PreviousIntegrationSha;
        var tip = _tip(request);
        var deliverySha = Sha(request);
        var changed = !string.IsNullOrWhiteSpace(deliverySha)
            && !string.Equals(receipt.DeliverySha, deliverySha, StringComparison.OrdinalIgnoreCase);
        var targetChanged = !string.IsNullOrWhiteSpace(tip) && !string.Equals(oldTip, tip, StringComparison.OrdinalIgnoreCase);
        var files = receipt.Result.ConflictedFiles;
        var mayRetry = MonolithTransitionAdapter.IntegrationConflictRetry(changed, targetChanged,
            !changed && targetChanged && oldTip is not null && files.Count > 0 && _touches(request, oldTip, files));
        return mayRetry ? null : receipt.Result;
    }

    internal string? Record(RemoteDeliveryIntegrationRequest request, MergeIntoIntegrationResult result)
    {
        if (result.Outcome is not (MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict))
        {
            // A hold or infrastructure error retains priority. A landed result releases it.
            if (result.Outcome.IsSuccessfulIntegration())
            {
                try
                {
                    File.Delete(Path.Combine(request.JobFolderPath, FileName));
                    if (ContinuationBaseStore.Read(request.JobFolderPath) is { RetainUntilIntegrated: true } pin)
                        ContinuationBaseStore.TryClearWhen(request.JobFolderPath, pin);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Publication already happened. Receipt cleanup cannot reverse it.
                    SilentCatch.Note(ex, "IntegrationConflictRecovery: receipt cleanup");
                }
            }
            return null;
        }
        var error = Write(request.JobFolderPath,
            ConflictReceipt(request, Sha(request), result, Read(request.JobFolderPath)));
        return error is null ? null
            : $"{error} Conflict files: {string.Join(", ", result.ConflictedFiles)}. {result.Error}";
    }

    private static IntegrationConflictReceipt ConflictReceipt(RemoteDeliveryIntegrationRequest request,
        string sha, MergeIntoIntegrationResult result, IntegrationConflictReceipt? previous)
    {
        var at = previous is { RecoveryQueued: true } && previous.Target == request.IntegrationBranch
            ? previous.OriginalDeliveredAtUtc : request.DeliveredAtUtc;
        var paths = previous is { RecoveryQueued: true } && previous.Target == request.IntegrationBranch
            ? (previous.ReservedPaths ?? previous.Result.ConflictedFiles).Concat(result.ConflictedFiles)
                .Distinct(StringComparer.Ordinal).ToArray() : result.ConflictedFiles;
        return new(sha, request.IntegrationBranch, at, result, ReservedPaths: paths);
    }

    internal static string? PrepareQueued(RemoteDeliveryIntegrationRequest request, string sha, MergeIntoIntegrationResult result)
    {
        var previous = Read(request.JobFolderPath);
        var receipt = previous is not null && previous.DeliverySha == sha && previous.Target == request.IntegrationBranch
            ? previous : ConflictReceipt(request, sha, result, previous);
        return Write(request.JobFolderPath, receipt with { RecoveryQueued = true });
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
        => Read(folder, out _);

    private static IntegrationConflictReceipt? Read(string folder, out bool unreadable)
    {
        unreadable = false;
        var path = Path.Combine(folder, FileName);
        try
        {
            var receipt = JsonSerializer.Deserialize<IntegrationConflictReceipt>(File.ReadAllText(path), Json);
            if (receipt is null || string.IsNullOrWhiteSpace(receipt.DeliverySha)
                || string.IsNullOrWhiteSpace(receipt.Target) || receipt.Result?.ConflictedFiles is null)
                throw new JsonException("The integration conflict receipt is missing required fields.");
            return receipt;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "IntegrationConflictRecovery: read");
            unreadable = true;
            return null;
        }
    }

    private static string? Write(string folder, IntegrationConflictReceipt receipt)
    {
        var path = Path.Combine(folder, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, Json));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "IntegrationConflictRecovery: receipt write");
            return $"integration-conflict-receipt-write-failed: {FileName} could not be saved; repair filesystem access before retrying integration. {ex.Message}";
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "IntegrationConflictRecovery: temporary receipt cleanup");
            }
        }
    }
}
