using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>The review package plane (AGT-3400 S4) a review host claims shadow packages from.</summary>
internal interface IReviewPackagePlaneClient
{
    Task<ReviewPackageClaimResponse> ClaimReviewPackageAsync(ReviewPackageClaimRequest request, CancellationToken ct);
    Task<ReviewPackageLeaseDto> RenewReviewPackageAsync(string attemptId, ReviewPackageRenewRequest request, CancellationToken ct);
    Task<ReviewPackageReportDto> ReportReviewPackageAsync(string attemptId, ReviewPackageReportRequest request, CancellationToken ct);
}

public sealed partial class TaskServerClient : IReviewPackagePlaneClient
{
    public async Task<ReviewPackageClaimResponse> ClaimReviewPackageAsync(
        ReviewPackageClaimRequest request, CancellationToken ct)
        => await PostJsonAsync<ReviewPackageClaimRequest, ReviewPackageClaimResponse>(
               $"/api/v1/runners/{Uri.EscapeDataString(request.ExecutorId)}/review-package-claims", request, ct)
           ?? new ReviewPackageClaimResponse("empty", null, null, "Empty review package claim response.");

    public async Task<ReviewPackageLeaseDto> RenewReviewPackageAsync(
        string attemptId, ReviewPackageRenewRequest request, CancellationToken ct)
        => await PostJsonAsync<ReviewPackageRenewRequest, ReviewPackageLeaseDto>(
               $"/api/v1/review-packages/attempts/{Uri.EscapeDataString(attemptId)}/lease/renew", request, ct)
           ?? throw new TaskServerException(500, "Empty review package lease renewal response.");

    public async Task<ReviewPackageReportDto> ReportReviewPackageAsync(
        string attemptId, ReviewPackageReportRequest request, CancellationToken ct)
        => await PostJsonAsync<ReviewPackageReportRequest, ReviewPackageReportDto>(
               $"/api/v1/review-packages/attempts/{Uri.EscapeDataString(attemptId)}/report", request, ct)
           ?? throw new TaskServerException(500, "Empty review package report response.");
}
