using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3077: the standalone Task Server applies the same private-repository
/// claim filter as the backend. A review host without a read credential for
/// the subject's repository is refused before the claim with the missing
/// <c>review:repository-read</c> key named; a host that holds it claims.
/// </summary>
public sealed partial class RemoteReviewAuthorityTests
{
    private const string PrivateReadKey = "review:repository-read:example.invalid/product";

    [Fact]
    public async Task Private_repository_is_refused_before_claim_without_a_scoped_read_credential()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, plan: PrivatePlan());
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");

        var empty = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        Assert.Equal("empty", empty.Status);
        Assert.Equal(ReviewClaimEmptyReasons.UnclaimablePlanRequirements, empty.Reason);
        Assert.Equal([PrivateReadKey], empty.MissingCapabilities);
        Assert.Contains("RUNNER_REVIEW_GIT_CREDENTIALS_FILE", empty.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_repository_is_claimed_by_the_host_holding_its_read_credential()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store, plan: PrivatePlan());
        await store.RegisterRunnerAsync(
            "review-private",
            new RegisterRunnerRequest(
                "review-private", "host-private", "instance-private", "1.0.0", TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                    ReviewCapabilities.BaselineComparison,
                    ReviewCapabilities.DependencyPreparation,
                    ReviewCapabilities.LibraryStepV1,
                    PrivateReadKey,
                ]),
            "review-private",
            default);

        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-private", "instance-private"), "review-private", default);

        Assert.Equal("claimed", claim.Status);
        Assert.Equal(subject.SubjectId, claim.Subject!.SubjectId);
        Assert.True(claim.Subject.Plan.PrivateRepository);
    }

    private static ReviewPlanDto PrivatePlan()
        => new(
            [new ReviewCommandDto("verify-subject", "completion", "git", ["rev-parse", "HEAD"])],
            ["completion"],
            LibraryVersion: ReviewLibraryStepPolicy.Version,
            PrivateRepository: true);
}
