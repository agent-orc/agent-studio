// clock-independent: pure subject rules; no rule compares a date with "now".
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class GateSubjectKindRulesTests
{
    private static readonly DateTime Fixed = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string BundleSha = new('a', 64);
    private static readonly GateBundleLocator Bundle = new(BundleSha, 10, GateBundleLimits.Locator(BundleSha));

    [Theory]
    [InlineData("merge-into-develop-build-gate", GateSubjectKinds.Merge)]
    [InlineData("merge-into-main", GateSubjectKinds.Promotion)]
    [InlineData("batch-gate", GateSubjectKinds.Batch)]
    [InlineData("batch-base", GateSubjectKinds.Batch)]
    [InlineData("post-build-test-gate", null)]
    [InlineData("pipeline-step-probe", null)]
    public void Gate_ids_map_to_their_verification_kind(string gateId, string? kind)
        => Assert.Equal(kind, GateSubjectKinds.ForGateId(gateId));

    [Fact]
    public void Merge_and_promotion_subjects_with_a_bundle_are_admissible()
    {
        GateRules.ValidateSubject(Request(GateSubjectKinds.Merge, Bundle));
        GateRules.ValidateSubject(Request(GateSubjectKinds.Promotion, Bundle));
    }

    public static TheoryData<string, CreateGateSubjectRequest> Invalid => new()
    {
        { "unknown kind", Request("deploy", Bundle) },
        { "bundle on a run subject", Request(null, Bundle) },
        { "verification without bundle or ref", Request(GateSubjectKinds.Merge, null) },
        { "oversized bundle", Request(GateSubjectKinds.Merge, Bundle with { SizeBytes = GateBundleLimits.MaxBytes + 1 }) },
        { "empty bundle", Request(GateSubjectKinds.Merge, Bundle with { SizeBytes = 0 }) },
        { "foreign locator", Request(GateSubjectKinds.Merge, Bundle with { Locator = "https://elsewhere/bundle" }) },
        { "short digest", Request(GateSubjectKinds.Merge, Bundle with { Sha256 = "abc", Locator = GateBundleLimits.Locator("abc") }) },
        { "bundle beside a run artifact", Request(GateSubjectKinds.Merge, Bundle) with
            { SourceBundleArtifactId = "artifact", SourceBundleSha256 = BundleSha } },
        { "merge with a batch id", Request(GateSubjectKinds.Merge, Bundle) with { BatchId = Guid.NewGuid().ToString("N") } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Inadmissible_verification_subjects_are_refused(string _, CreateGateSubjectRequest request)
        => Assert.Throws<ArgumentException>(() => GateRules.ValidateSubject(request));

    [Fact]
    public void Host_plan_digest_check_applies_to_verification_subjects_only()
    {
        var request = Request(GateSubjectKinds.Merge, Bundle);
        var subject = Subject(request);
        Assert.Null(GateRules.PlanDigestViolation(subject));
        Assert.NotNull(GateRules.PlanDigestViolation(subject with { PlanHash = new string('f', 64) }));
        Assert.Null(GateRules.PlanDigestViolation(subject with { Kind = null, PlanHash = new string('f', 64) }));
    }

    [Fact]
    public void Replays_must_keep_kind_and_bundle()
    {
        var request = Request(GateSubjectKinds.Merge, Bundle);
        var subject = Subject(request);
        Assert.True(GateRules.SameSubject(subject, request));
        Assert.False(GateRules.SameSubject(subject, request with { Kind = GateSubjectKinds.Promotion }));
        Assert.False(GateRules.SameSubject(subject, request with { Bundle = Bundle with { SizeBytes = 11 } }));
    }

    private static GateSubject Subject(CreateGateSubjectRequest request)
        => new("gsub_1", request.TaskId, request.SourceRunId, request.RepositoryId, request.RepositoryUrl,
            request.ExpectedSha, request.ResultRef, request.SourceBundleArtifactId, request.SourceBundleSha256,
            request.PlanHash, request.PolicyHash, request.PipelineDefinitionVersion,
            request.TestSelectionAuditDigest, request.Plan, Fixed, request.DispatchDeadline,
            request.MaxAttempts, request.BatchId, request.MembershipDigest, request.BaseSha,
            request.MemberRunIds, request.Kind, request.Bundle);

    private static CreateGateSubjectRequest Request(string? kind, GateBundleLocator? bundle)
    {
        var plan = new GatePlan("post-build-test-gate", 1,
            [new GateCommand("build-1", "bash", ["-lc", "true"], 30)], "", 300, [], 4096, "always");
        return new CreateGateSubjectRequest("job", "merge:1", "product", null, Sha, null, null, null,
            GateRules.PlanHash(plan), "policy", "3", new string('3', 64), plan,
            Fixed.AddMinutes(5), Kind: kind, Bundle: bundle);
    }
}
