using System.Net;
using System.Text.Json;
using AgentRunner.SourceFacts;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.SourceFacts
{
    /// <summary>The Task Server routes of the derived source data store (AGT-3318).</summary>
    public interface ISourceFactsClient
    {
        Task<SourceProducerLeaseDto> ClaimSourceProducerLeaseAsync(
            string repositoryId, SourceProducerLeaseRequest request, CancellationToken ct);

        Task<SourceDiffObjectDto> PublishSourceDiffObjectAsync(
            string repositoryId, SourceDiffObjectPublishRequest request, CancellationToken ct);

        Task<SourceDiffObjectDto?> GetSourceDiffObjectAsync(
            string repositoryId, string baseSha, string headSha, string options, int rendererVersion,
            CancellationToken ct);

        Task PublishSourcePathHistoryAsync(
            string repositoryId, SourcePathHistoryPublishRequest request, CancellationToken ct);

        Task PublishSourceBlameAsync(
            string repositoryId, SourceBlamePublishRequest request, CancellationToken ct);

        Task PublishSourceReachabilityAsync(
            string repositoryId, SourceReachabilityPublishRequest request, CancellationToken ct);

        Task RecordSourceFactDemandAsync(
            string repositoryId, SourceFactDemandRequest request, CancellationToken ct);

        Task<IReadOnlyList<SourceFactDemandDto>> ListSourceFactDemandsAsync(
            string repositoryId, int limit, CancellationToken ct);

        Task CompleteSourceFactDemandAsync(
            string repositoryId, long demandId, SourceFactDemandCompleteRequest request, CancellationToken ct);

        Task RecordSourceReviewMaterialReadAsync(
            string repositoryId, SourceReviewMaterialReadReport report, CancellationToken ct);
    }
}

namespace AgentRunner
{
    public sealed partial class TaskServerClient
    {
        private static string SourceFactsRoot(string repositoryId)
            => $"/api/v1/source-facts/{Uri.EscapeDataString(repositoryId)}";

        public async Task<SourceProducerLeaseDto> ClaimSourceProducerLeaseAsync(
            string repositoryId, SourceProducerLeaseRequest request, CancellationToken ct)
            => await PostJsonAsync<SourceProducerLeaseRequest, SourceProducerLeaseDto>(
                   SourceFactsRoot(repositoryId) + "/producer/lease", request, ct)
               ?? throw new TaskServerException(500, "Empty source producer lease response.");

        public async Task<SourceDiffObjectDto> PublishSourceDiffObjectAsync(
            string repositoryId, SourceDiffObjectPublishRequest request, CancellationToken ct)
            => await SendJsonAsync<SourceDiffObjectPublishRequest, SourceDiffObjectDto>(
                   HttpMethod.Put, SourceFactsRoot(repositoryId) + "/diffs", request, ct)
               ?? throw new TaskServerException(500, "Empty diff object publish response.");

        public async Task<SourceDiffObjectDto?> GetSourceDiffObjectAsync(
            string repositoryId, string baseSha, string headSha, string options, int rendererVersion,
            CancellationToken ct)
        {
            var url = SourceFactsRoot(repositoryId)
                      + "/diffs?base=" + Uri.EscapeDataString(baseSha)
                      + "&head=" + Uri.EscapeDataString(headSha)
                      + "&options=" + Uri.EscapeDataString(options)
                      + "&renderer=" + rendererVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var response = await _http.GetAsync(url, ct);
            var detail = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.NotFound
                && TryReadApiErrorCode(detail) == "source-object-missing") return null;
            if (!response.IsSuccessStatusCode)
                throw new TaskServerException(
                    (int)response.StatusCode,
                    $"Diff object lookup failed: {Trim(detail)}",
                    TryReadApiErrorCode(detail));
            return JsonSerializer.Deserialize<SourceDiffObjectDto>(detail, Json)
                   ?? throw new TaskServerException(500, "Empty diff object response.");
        }

        public async Task PublishSourcePathHistoryAsync(
            string repositoryId, SourcePathHistoryPublishRequest request, CancellationToken ct)
            => await SendJsonAsync<SourcePathHistoryPublishRequest, SourcePathHistoryDto>(
                HttpMethod.Put, SourceFactsRoot(repositoryId) + "/path-histories", request, ct);

        public async Task PublishSourceBlameAsync(
            string repositoryId, SourceBlamePublishRequest request, CancellationToken ct)
            => await SendJsonAsync<SourceBlamePublishRequest, SourceBlameDto>(
                HttpMethod.Put, SourceFactsRoot(repositoryId) + "/blames", request, ct);

        public async Task PublishSourceReachabilityAsync(
            string repositoryId, SourceReachabilityPublishRequest request, CancellationToken ct)
            => await SendJsonAsync<SourceReachabilityPublishRequest, SourceReachabilityView>(
                HttpMethod.Put, SourceFactsRoot(repositoryId) + "/reachability", request, ct);

        public async Task RecordSourceFactDemandAsync(
            string repositoryId, SourceFactDemandRequest request, CancellationToken ct)
            => await PostJsonAsync<SourceFactDemandRequest, SourceFactDemandDto>(
                SourceFactsRoot(repositoryId) + "/demands", request, ct);

        public async Task<IReadOnlyList<SourceFactDemandDto>> ListSourceFactDemandsAsync(
            string repositoryId, int limit, CancellationToken ct)
        {
            var url = SourceFactsRoot(repositoryId) + "/demands?limit="
                      + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var response = await _http.GetAsync(url, ct);
            var detail = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new TaskServerException(
                    (int)response.StatusCode,
                    $"Source fact demand listing failed: {Trim(detail)}",
                    TryReadApiErrorCode(detail));
            return JsonSerializer.Deserialize<SourceFactDemandDto[]>(detail, Json) ?? [];
        }

        public async Task CompleteSourceFactDemandAsync(
            string repositoryId, long demandId, SourceFactDemandCompleteRequest request, CancellationToken ct)
            => await PostJsonAsync<SourceFactDemandCompleteRequest, SourceFactDemandDto>(
                SourceFactsRoot(repositoryId) + "/demands/"
                + demandId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/complete",
                request, ct);

        public async Task RecordSourceReviewMaterialReadAsync(
            string repositoryId, SourceReviewMaterialReadReport report, CancellationToken ct)
            => await PostJsonWithoutResponseAsync(
                SourceFactsRoot(repositoryId) + "/review-material/reads", report, ct);
    }
}
