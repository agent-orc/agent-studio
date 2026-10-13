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
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store, plan: PrivatePlan());
        await RegisterPrivateReviewerAsync(store, clock, readProofStatus: "ready");

        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-private", "instance-private"), "review-private", default);

        Assert.Equal("claimed", claim.Status);
        Assert.Equal(subject.SubjectId, claim.Subject!.SubjectId);
        Assert.True(claim.Subject.Plan.PrivateRepository);
    }

    [Theory]
    [InlineData("unavailable", 0)]
    [InlineData("ready", 600)]
    [InlineData(null, 0)]
    public async Task Registered_read_key_without_a_fresh_ready_proof_is_refused_before_claim(
        string? readProofStatus, int advanceSeconds)
    {
        // AGT-3077: a line in the credentials file is not a working token. An
        // expired or revoked token advertises an unavailable proof, and a proof
        // that stopped being refreshed goes stale; neither may claim.
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, plan: PrivatePlan());
        await RegisterPrivateReviewerAsync(store, clock, readProofStatus);
        clock.Advance(TimeSpan.FromSeconds(advanceSeconds));

        var empty = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-private", "instance-private"), "review-private", default);

        Assert.Equal("empty", empty.Status);
        Assert.Equal(ReviewClaimEmptyReasons.UnclaimablePlanRequirements, empty.Reason);
        Assert.Equal([PrivateReadKey], empty.MissingCapabilities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coding_host_with_the_read_key_does_not_yield_a_private_review_to_a_host_without_it(
        bool otherHostRegistersAnUnprovenKey)
    {
        // AGT-3077 x AGT-3069: website cards are coded and reviewed on the only
        // host holding the read key. An idle review host without a proven key
        // skips the private review, so yielding to it would strand the review.
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store, plan: PrivatePlan(), codingHost: "host-private");
        await RegisterPrivateReviewerAsync(store, clock, readProofStatus: "ready");
        if (otherHostRegistersAnUnprovenKey)
            await RegisterPrivateReviewerAsync(
                store, clock, readProofStatus: "unavailable", id: "review-b", instance: "instance-b", host: "host-b");
        else
            await RegisterReviewerAsync(store, "review-b", "instance-b", "host-b");

        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-private", "instance-private"), "review-private", default);

        Assert.Equal("claimed", claim.Status);
        Assert.Equal(subject.SubjectId, claim.Subject!.SubjectId);
        Assert.Equal("host-private", claim.Attempt!.HostId);
    }

    private static async Task RegisterPrivateReviewerAsync(
        TaskServerStore store,
        ManualTimeProvider clock,
        string? readProofStatus,
        string id = "review-private",
        string instance = "instance-private",
        string host = "host-private")
    {
        string[] keys =
        [
            ReviewCapabilities.ReviewExecutor,
            ReviewCapabilities.GitMaterialization,
            ReviewCapabilities.SemanticReview,
            ReviewCapabilities.BaselineComparison,
            ReviewCapabilities.DependencyPreparation,
            ReviewCapabilities.LibraryStepV1,
            PrivateReadKey,
        ];
        await store.RegisterRunnerAsync(
            id,
            new RegisterRunnerRequest(id, host, instance, "1.0.0", TaskServerProtocol.Current, keys),
            id,
            default);
        if (readProofStatus is null) return;
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            id, instance, CapabilityProtocol.CurrentSchemaVersion,
            clock.GetUtcNow().UtcDateTime, 300, 1,
            [new AdvertisedCapabilityDto(PrivateReadKey, "source", readProofStatus)]), id, default);
    }

    private static ReviewPlanDto PrivatePlan()
        => new(
            [new ReviewCommandDto("verify-subject", "completion", "git", ["rev-parse", "HEAD"])],
            ["completion"],
            LibraryVersion: ReviewLibraryStepPolicy.Version,
            PrivateRepository: true);
}
