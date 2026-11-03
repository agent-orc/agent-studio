using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// SM-11 (security-model Dossier): a review verdict is accepted only from a
/// review principal different from the delivery's coding principal.
/// </summary>
public sealed partial class RemoteReviewAuthorityTests
{
    private static TaskServerPrincipal RunnerPrincipal(string principalId, string runnerId)
        => new(principalId, TaskServerPrincipalKinds.Runner,
            new HashSet<string>([TaskServerScopes.ReviewsClaim, TaskServerScopes.ReviewsWrite], StringComparer.Ordinal),
            runnerId);

    [Fact]
    public async Task Review_verdict_from_the_delivery_coding_principal_is_refused()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, codingPrincipal: "principal-shared");
        await RegisterReviewerAsync(store, "reviewer", "instance-r", "review-host");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("reviewer", "instance-r"), "reviewer", default);
        Assert.Equal("claimed", claim.Status);

        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ReportReviewAsync(
            claim.Attempt!.AttemptId, PassingReport(claim), "reviewer", default,
            RunnerPrincipal("principal-shared", "reviewer")));
        Assert.Equal(ReviewPrincipalSeparationPolicy.CoderCode, refused.Code);
        Assert.Null((await store.GetReviewAttemptAsync(claim.Attempt.AttemptId, default))!.ReportedAt);

        var accepted = await store.ReportReviewAsync(
            claim.Attempt.AttemptId, PassingReport(claim), "reviewer", default,
            RunnerPrincipal("principal-reviewer", "reviewer"));
        Assert.Equal("Pass", accepted.Outcome);
    }

    [Fact]
    public async Task Review_verdict_from_a_principal_that_is_not_a_runner_is_refused()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, codingPrincipal: "principal-coder");
        await RegisterReviewerAsync(store, "reviewer", "instance-r", "review-host");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("reviewer", "instance-r"), "reviewer", default);

        var studio = new TaskServerPrincipal("studio-principal", TaskServerPrincipalKinds.Studio,
            new HashSet<string>([TaskServerScopes.ReviewsWrite], StringComparer.Ordinal), null);
        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ReportReviewAsync(
            claim.Attempt!.AttemptId, PassingReport(claim), "studio", default, studio));
        Assert.Equal(ReviewPrincipalSeparationPolicy.NotReviewerCode, refused.Code);
    }

    [Fact]
    public async Task Review_claim_skips_a_delivery_the_claiming_principal_coded()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, codingPrincipal: "principal-shared");
        await RegisterReviewerAsync(store, "reviewer", "instance-r", "review-host");

        var skipped = await store.ClaimReviewAsync(
            new ReviewClaimRequest("reviewer", "instance-r"), "reviewer", default, "principal-shared");
        Assert.NotEqual("claimed", skipped.Status);
        Assert.Equal(ReviewClaimEmptyReasons.CodingPrincipalExcluded, skipped.Reason);

        var claimed = await store.ClaimReviewAsync(
            new ReviewClaimRequest("reviewer", "instance-r"), "reviewer", default, "principal-reviewer");
        Assert.Equal("claimed", claimed.Status);
    }

    [Theory]
    [InlineData("coder", null, "coder", null, null, false, ReviewPrincipalSeparationPolicy.CoderCode)]
    [InlineData("coder", "p-1", "reviewer", "p-1", TaskServerPrincipalKinds.Runner, false, ReviewPrincipalSeparationPolicy.CoderCode)]
    [InlineData("coder", "p-1", "reviewer", "p-2", TaskServerPrincipalKinds.Runner, true, null)]
    [InlineData("coder", null, "reviewer", "p-2", TaskServerPrincipalKinds.Runner, true, null)]
    [InlineData("coder", "p-1", "reviewer", null, null, true, null)]
    [InlineData("coder", "p-1", "reviewer", "p-2", TaskServerPrincipalKinds.Studio, false, ReviewPrincipalSeparationPolicy.NotReviewerCode)]
    [InlineData(null, null, "reviewer", null, null, true, null)]
    [InlineData("coder", "", "reviewer", "", TaskServerPrincipalKinds.Runner, true, null)]
    public void Review_principal_separation_matrix(
        string? codingRunner, string? codingPrincipal, string reviewer, string? reviewerPrincipal,
        string? reviewerKind, bool accepted, string? code)
    {
        var decision = ReviewPrincipalSeparationPolicy.Decide(
            new DeliveryCodingPrincipal(codingRunner, codingPrincipal),
            new ReviewVerdictReporter(reviewer, reviewerPrincipal, reviewerKind));
        Assert.Equal(accepted, decision.Accepted);
        Assert.Equal(code, decision.Code);
    }
}
