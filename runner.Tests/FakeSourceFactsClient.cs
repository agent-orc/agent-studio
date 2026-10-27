using AgentRunner.SourceFacts;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.Tests;

/// <summary>
/// In-memory stand-in for the Task Server source facts routes (AGT-3318). It
/// keeps the published rows by their immutable key and applies the same
/// epoch rule as the store, so producer and reader tests share one fake.
/// </summary>
internal sealed class FakeSourceFactsClient : ISourceFactsClient
{
    private readonly object _gate = new();
    public Dictionary<(string Repository, string Base, string Head, string Options, int Renderer), SourceDiffObjectDto> Diffs { get; } = [];
    public List<(string Repository, SourcePathHistoryPublishRequest Request)> Histories { get; } = [];
    public List<(string Repository, SourceBlamePublishRequest Request)> Blames { get; } = [];
    public List<(string Repository, SourceReachabilityPublishRequest Request)> Reachability { get; } = [];
    public List<(string Repository, SourceFactDemandRequest Request)> Demands { get; } = [];
    public List<(string Repository, SourceReviewMaterialReadReport Report)> Reads { get; } = [];
    public List<(long DemandId, SourceFactDemandCompleteRequest Request)> Completions { get; } = [];
    public List<SourceFactDemandDto> PendingDemands { get; } = [];
    public string? LeaseHolder { get; set; }
    public long Epoch { get; set; } = 1;
    // clock-independent: the lease expiry is an opaque value the fake echoes back.
    public DateTime LeaseExpiresAt { get; set; } = new(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);
    public int LeaseClaims { get; private set; }

    public Task<SourceProducerLeaseDto> ClaimSourceProducerLeaseAsync(
        string repositoryId, SourceProducerLeaseRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            LeaseClaims++;
            if (LeaseHolder is not null && LeaseHolder != request.ProducerId)
                throw new TaskServerException(409, "held", "source-producer-lease-held");
            LeaseHolder = request.ProducerId;
            return Task.FromResult(new SourceProducerLeaseDto(repositoryId, request.ProducerId, Epoch, LeaseExpiresAt));
        }
    }

    public Task<SourceDiffObjectDto> PublishSourceDiffObjectAsync(
        string repositoryId, SourceDiffObjectPublishRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            RequireCurrent(request.ProducerId, request.ProducerEpoch);
            var stored = new SourceDiffObjectDto(
                repositoryId, request.BaseSha, request.HeadSha, request.Options, request.RendererVersion,
                request.ChangedFiles, request.DiffLines, request.TotalFiles, request.TotalDiffLines,
                request.MaximumFiles, request.MaximumLines, request.ContentSha256, 0,
                request.ProducerId, request.ProducerEpoch, LeaseExpiresAt);
            Diffs[(repositoryId, request.BaseSha, request.HeadSha, request.Options, request.RendererVersion)] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<SourceDiffObjectDto?> GetSourceDiffObjectAsync(
        string repositoryId, string baseSha, string headSha, string options, int rendererVersion, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(Diffs.GetValueOrDefault((repositoryId, baseSha, headSha, options, rendererVersion)));
    }

    public Task PublishSourcePathHistoryAsync(string repositoryId, SourcePathHistoryPublishRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            RequireCurrent(request.ProducerId, request.ProducerEpoch);
            Histories.Add((repositoryId, request));
        }
        return Task.CompletedTask;
    }

    public Task PublishSourceBlameAsync(string repositoryId, SourceBlamePublishRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            RequireCurrent(request.ProducerId, request.ProducerEpoch);
            Blames.Add((repositoryId, request));
        }
        return Task.CompletedTask;
    }

    public Task PublishSourceReachabilityAsync(string repositoryId, SourceReachabilityPublishRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            RequireCurrent(request.ProducerId, request.ProducerEpoch);
            Reachability.Add((repositoryId, request));
        }
        return Task.CompletedTask;
    }

    public Task RecordSourceFactDemandAsync(string repositoryId, SourceFactDemandRequest request, CancellationToken ct)
    {
        lock (_gate) Demands.Add((repositoryId, request));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SourceFactDemandDto>> ListSourceFactDemandsAsync(string repositoryId, int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SourceFactDemandDto>>(
                PendingDemands.Where(demand => demand.RepositoryId == repositoryId).Take(limit).ToArray());
    }

    public Task CompleteSourceFactDemandAsync(
        string repositoryId, long demandId, SourceFactDemandCompleteRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            RequireCurrent(request.ProducerId, request.ProducerEpoch);
            Completions.Add((demandId, request));
            PendingDemands.RemoveAll(demand => demand.DemandId == demandId);
        }
        return Task.CompletedTask;
    }

    public Task RecordSourceReviewMaterialReadAsync(string repositoryId, SourceReviewMaterialReadReport report, CancellationToken ct)
    {
        lock (_gate) Reads.Add((repositoryId, report));
        return Task.CompletedTask;
    }

    /// <summary>Simulates a takeover by another producer: every later publish of the old holder is fenced.</summary>
    public void TakeOver(string producerId)
    {
        lock (_gate)
        {
            LeaseHolder = producerId;
            Epoch++;
        }
    }

    private void RequireCurrent(string producerId, long epoch)
    {
        if (producerId != LeaseHolder || epoch != Epoch)
            throw new TaskServerException(409, "stale", "stale-source-producer");
    }
}
