using System.Collections.Concurrent;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.SourceFacts;

/// <summary>
/// The fenced producer of derived source data on a runner host (AGT-3318,
/// git-facts option C, D7 A-B). At result handoff it computes the diff object
/// against the merge base within the review budget, then path history and
/// blame for the touched files, and publishes them to the Task Server under
/// the repository's producer epoch. On demand it answers what readers found
/// missing. It is the only Git client behind these facts; the Task Server and
/// the Studio only read the published rows.
/// </summary>
public sealed class SourceFactsProducer
{
    /// <summary>Touched files that get path history and blame at handoff; the rest are produced on demand.</summary>
    public const int HandoffPathFactLimit = 40;
    public const string RepositoryMarkerFile = "source-facts-repository-id";
    internal const int LeaseTtlSeconds = 300;
    private static readonly TimeSpan LeaseRenewMargin = TimeSpan.FromSeconds(60);

    private readonly ISourceFactsClient _client;
    private readonly string _producerId;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, SourceProducerLeaseDto> _leases = new(StringComparer.Ordinal);

    public SourceFactsProducer(
        ISourceFactsClient client,
        string producerId,
        Action<string> log,
        Func<DateTime>? utcNow = null)
    {
        _client = client;
        _producerId = producerId;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public sealed record HandoffResult(bool Published, string? MergeBaseSha, int PathFacts, string? SkipReason);

    /// <summary>
    /// Publishes the facts of one delivered result. Never throws for a Git or
    /// Task Server fault: the handoff it follows has already been acknowledged,
    /// and a reader that misses the object falls back and records a demand.
    /// </summary>
    public async Task<HandoffResult> PublishHandoffAsync(
        string repositoryId,
        string repositoryPath,
        string headSha,
        string fallbackBaseSha,
        string? integrationBranch,
        string? fetchRef,
        CancellationToken ct)
    {
        try
        {
            var mergeBase = await ResolveMergeBaseAsync(repositoryPath, headSha, integrationBranch, ct)
                            ?? fallbackBaseSha;
            var lease = await EnsureLeaseAsync(repositoryId, ct);
            if (lease is null)
            {
                // Another host holds the repository. Its sweep computes the
                // object from the pushed result instead.
                await _client.RecordSourceFactDemandAsync(repositoryId, new SourceFactDemandRequest(
                    SourceFactKinds.Diff, "handoff-on-non-holder", mergeBase, headSha, FetchRef: fetchRef), ct);
                return new HandoffResult(false, mergeBase, 0, "lease-held-elsewhere");
            }
            var material = await PublishDiffAsync(repositoryId, repositoryPath, lease, mergeBase, headSha, ct);
            var produced = 0;
            foreach (var path in material.ChangedFiles.Take(HandoffPathFactLimit))
            {
                if (await PublishPathFactsAsync(repositoryId, repositoryPath, lease, headSha, path, ct))
                    produced++;
            }
            _log(
                $"source-facts handoff published repository={repositoryId} base={mergeBase} head={headSha} " +
                $"files={material.ChangedFiles.Count}/{material.TotalFiles} pathFacts={produced} epoch={lease.Epoch}");
            return new HandoffResult(true, mergeBase, produced, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log($"source-facts handoff cancelled repository={repositoryId} head={headSha}");
            return new HandoffResult(false, null, 0, "cancelled");
        }
        catch (Exception exception)
        {
            if (exception is TaskServerException { ErrorCode: "stale-source-producer" })
                _leases.TryRemove(repositoryId, out _);
            _log($"source-facts handoff deferred repository={repositoryId} head={headSha} " +
                 $"type={exception.GetType().Name}: {exception.Message}");
            return new HandoffResult(false, null, 0, exception.GetType().Name);
        }
    }

    /// <summary>Answers the pending demands of one repository whose checkout lives on this host.</summary>
    public async Task<int> DrainDemandsAsync(string repositoryId, string repositoryPath, CancellationToken ct)
    {
        var lease = await EnsureLeaseAsync(repositoryId, ct);
        if (lease is null) return 0;
        var demands = await _client.ListSourceFactDemandsAsync(repositoryId, 20, ct);
        var answered = 0;
        foreach (var demand in demands)
        {
            string? error = null;
            try
            {
                lease = await EnsureLeaseAsync(repositoryId, ct)
                        ?? throw new InvalidOperationException("The source facts lease moved to another producer.");
                error = await AnswerAsync(repositoryId, repositoryPath, lease, demand, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskServerException exception) when (exception.ErrorCode == "stale-source-producer")
            {
                _leases.TryRemove(repositoryId, out _);
                _log($"source-facts producer fenced repository={repositoryId}: {exception.Message}");
                return answered;
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }
            await _client.CompleteSourceFactDemandAsync(
                repositoryId,
                demand.DemandId,
                new SourceFactDemandCompleteRequest(lease.ProducerId, lease.Epoch, error is null, error),
                ct);
            if (error is null) answered++;
            else _log($"source-facts demand failed repository={repositoryId} id={demand.DemandId} kind={demand.Kind}: {error}");
        }
        return answered;
    }

    private async Task<string?> AnswerAsync(
        string repositoryId, string repositoryPath, SourceProducerLeaseDto lease, SourceFactDemandDto demand,
        CancellationToken ct)
    {
        switch (demand.Kind)
        {
            case SourceFactKinds.Diff:
                if (!await EnsureCommitsAsync(repositoryPath, [demand.BaseSha!, demand.HeadSha!], demand.FetchRef, ct))
                    return "commits-unavailable";
                await PublishDiffAsync(repositoryId, repositoryPath, lease, demand.BaseSha!, demand.HeadSha!, ct);
                return null;
            case SourceFactKinds.PathHistory:
            case SourceFactKinds.Blame:
                if (!await EnsureCommitsAsync(repositoryPath, [demand.HeadSha!], demand.FetchRef, ct))
                    return "commits-unavailable";
                return await PublishPathFactsAsync(repositoryId, repositoryPath, lease, demand.HeadSha!, demand.Path!, ct)
                    ? null
                    : "path-unavailable";
            case SourceFactKinds.Reachability:
                return await PublishReachabilityAsync(repositoryId, repositoryPath, lease, demand.Shas, demand.TargetRefs, ct);
            default:
                return $"unknown-kind:{demand.Kind}";
        }
    }

    private async Task<MergeBaseDiffMaterial> PublishDiffAsync(
        string repositoryId, string repositoryPath, SourceProducerLeaseDto lease, string baseSha, string headSha,
        CancellationToken ct)
    {
        var material = await MergeBaseDiffMaterial.ComputeAsync(
            repositoryPath, baseSha, headSha,
            SourceDiffObjectOptions.ReviewMaximumFiles, SourceDiffObjectOptions.ReviewMaximumLines,
            environment: null, clearEnvironment: false, ct);
        await _client.PublishSourceDiffObjectAsync(
            repositoryId, material.ToPublishRequest(lease.ProducerId, lease.Epoch), ct);
        return material;
    }

    private async Task<bool> PublishPathFactsAsync(
        string repositoryId, string repositoryPath, SourceProducerLeaseDto lease, string headSha, string path,
        CancellationToken ct)
    {
        var history = await SourcePathFacts.ReadHistoryAsync(repositoryPath, headSha, path, ct);
        if (history is null) return false;
        await _client.PublishSourcePathHistoryAsync(repositoryId, new SourcePathHistoryPublishRequest(
            lease.ProducerId, lease.Epoch, headSha, path, history.Value.Entries, history.Value.Truncated), ct);
        // A deleted or binary-unblamable path keeps its history without blame.
        var blame = await SourcePathFacts.ReadBlameAsync(repositoryPath, headSha, path, ct);
        if (blame is not null)
            await _client.PublishSourceBlameAsync(repositoryId, new SourceBlamePublishRequest(
                lease.ProducerId, lease.Epoch, headSha, path, blame.Value.Ranges, blame.Value.Commits), ct);
        return true;
    }

    private async Task<string?> PublishReachabilityAsync(
        string repositoryId, string repositoryPath, SourceProducerLeaseDto lease,
        IReadOnlyList<string> shas, IReadOnlyList<string> targetRefs, CancellationToken ct)
    {
        var heads = new List<SourceRefHead>();
        var facts = new List<SourceReachabilityFact>();
        foreach (var target in targetRefs)
        {
            // Refresh the remote-tracking ref so a fact never describes a head
            // older than the one integration has already pushed.
            await ProcessRunner.RunAsync(
                "git",
                ["fetch", "--no-tags", "--quiet", "origin", $"+refs/heads/{target}:refs/remotes/origin/{target}"],
                repositoryPath, ct: ct);
            var head = await RevParseAsync(repositoryPath, $"refs/remotes/origin/{target}", ct)
                       ?? await RevParseAsync(repositoryPath, $"refs/heads/{target}", ct);
            if (head is null) continue;
            heads.Add(new SourceRefHead(target, head));
            foreach (var sha in shas)
            {
                var ancestor = await ProcessRunner.RunAsync(
                    "git", ["merge-base", "--is-ancestor", sha, head], repositoryPath, ct: ct);
                // Exit 1 means "not an ancestor"; any other failure, such as an
                // unknown commit, leaves the pair missing instead of false.
                if (ancestor.ExitCode is 0 or 1)
                    facts.Add(new SourceReachabilityFact(sha, target, ancestor.ExitCode == 0, head));
            }
        }
        if (heads.Count == 0) return "target-refs-unavailable";
        await _client.PublishSourceReachabilityAsync(repositoryId, new SourceReachabilityPublishRequest(
            lease.ProducerId, lease.Epoch, heads, facts), ct);
        return null;
    }

    private async Task<SourceProducerLeaseDto?> EnsureLeaseAsync(string repositoryId, CancellationToken ct)
    {
        if (_leases.TryGetValue(repositoryId, out var cached) && cached.ExpiresAt - _utcNow() > LeaseRenewMargin)
            return cached;
        try
        {
            var lease = await _client.ClaimSourceProducerLeaseAsync(
                repositoryId, new SourceProducerLeaseRequest(_producerId, LeaseTtlSeconds), ct);
            if (cached is not null && cached.Epoch != lease.Epoch)
                _log($"source-facts producer epoch changed repository={repositoryId} {cached.Epoch}->{lease.Epoch}");
            _leases[repositoryId] = lease;
            return lease;
        }
        catch (TaskServerException exception) when (exception.ErrorCode == "source-producer-lease-held")
        {
            _leases.TryRemove(repositoryId, out _);
            return null;
        }
    }

    internal static async Task<string?> ResolveMergeBaseAsync(
        string repositoryPath, string headSha, string? integrationBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(integrationBranch)) return null;
        var branch = integrationBranch.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? integrationBranch["refs/heads/".Length..]
            : integrationBranch;
        foreach (var target in new[] { $"refs/remotes/origin/{branch}", $"refs/heads/{branch}" })
        {
            var result = await ProcessRunner.RunAsync("git", ["merge-base", headSha, target], repositoryPath, ct: ct);
            var sha = result.StdOut.Trim();
            if (result.Success && sha.Length is 40 or 64) return sha;
        }
        return null;
    }

    private static async Task<bool> EnsureCommitsAsync(
        string repositoryPath, IReadOnlyList<string> shas, string? fetchRef, CancellationToken ct)
    {
        if (await HasCommitsAsync(repositoryPath, shas, ct)) return true;
        if (string.IsNullOrWhiteSpace(fetchRef)) return false;
        await ProcessRunner.RunAsync("git", ["fetch", "--no-tags", "--quiet", "origin", fetchRef], repositoryPath, ct: ct);
        return await HasCommitsAsync(repositoryPath, shas, ct);
    }

    private static async Task<bool> HasCommitsAsync(string repositoryPath, IReadOnlyList<string> shas, CancellationToken ct)
    {
        foreach (var sha in shas)
        {
            var probe = await ProcessRunner.RunAsync("git", ["cat-file", "-e", sha + "^{commit}"], repositoryPath, ct: ct);
            if (!probe.Success) return false;
        }
        return true;
    }

    private static async Task<string?> RevParseAsync(string repositoryPath, string reference, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], repositoryPath, ct: ct);
        var sha = result.StdOut.Trim();
        return result.Success && sha.Length is 40 or 64 ? sha : null;
    }
}
