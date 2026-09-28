using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewLibraryStepPolicyTests
{
    private const string SubjectSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Versioned_step_has_the_same_identity_and_evidence_contract_on_two_hosts()
    {
        var plan = Plan();
        var first = ReviewLibraryStepPolicy.Seal(plan, SubjectSha);
        var second = ReviewLibraryStepPolicy.Seal(plan, SubjectSha);
        var firstStep = first.Commands[0].LibraryStep!;
        var secondStep = second.Commands[0].LibraryStep!;
        Assert.Equal(firstStep.Digest, secondStep.Digest);
        Assert.Equal(firstStep.InputSubjectSha, secondStep.InputSubjectSha);
        Assert.Contains(CapabilityProtocol.DotNet, firstStep.RequiredCapabilities);
        Assert.True(ReviewLibraryStepPolicy.ValidPlan(first, SubjectSha));
        Assert.True(ReviewLibraryStepPolicy.ValidReport(first, [Evidence(firstStep)]));
        Assert.True(ReviewLibraryStepPolicy.ValidReport(second, [Evidence(secondStep)]));
        var hostA = firstStep.RequiredCapabilities.ToHashSet(StringComparer.Ordinal);
        var hostB = secondStep.RequiredCapabilities.ToHashSet(StringComparer.Ordinal);
        Assert.True(ReviewLibraryStepPolicy.Supports(first, hostA));
        Assert.True(ReviewLibraryStepPolicy.Supports(second, hostB));
        hostB.Remove(ReviewCapabilities.LibraryStepV1);
        Assert.False(ReviewLibraryStepPolicy.Supports(second, hostB));
    }

    [Fact]
    public void Changed_subject_command_or_digest_cannot_reuse_the_step()
    {
        var sealedPlan = ReviewLibraryStepPolicy.Seal(Plan(), SubjectSha);
        var step = sealedPlan.Commands[0].LibraryStep!;
        Assert.False(ReviewLibraryStepPolicy.ValidPlan(sealedPlan, new string('b', 40)));
        Assert.False(ReviewLibraryStepPolicy.ValidReport(sealedPlan,
            [Evidence(step with { Digest = new string('0', 64) })]));
        Assert.False(ReviewLibraryStepPolicy.ValidReport(sealedPlan,
            [Evidence(step) with { ExpectedResultSha = new string('b', 40) }]));
        Assert.False(ReviewLibraryStepPolicy.ValidReport(sealedPlan,
            [Evidence(step) with
            {
                Budget = new ReviewCommandBudgetEvidenceDto(
                    "review-command", step.TimeoutSeconds * 1000L + 1, 1, false),
            }]));
        Assert.Throws<ArgumentException>(() => ReviewLibraryStepPolicy.Seal(
            sealedPlan with
            {
                Commands = [sealedPlan.Commands[0] with { Arguments = ["changed"] }],
            }, SubjectSha));
    }

    private static ReviewPlanDto Plan()
        => new(
            [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-lc", "dotnet test"],
                TimeoutSeconds: 120, CompareToBaseline: true)],
            ["build-tests"],
            LibraryVersion: ReviewLibraryStepPolicy.Version);

    private static ReviewCommandEvidenceDto Evidence(ReviewLibraryStepDto step)
        => new(step.Id, "build-tests", "sh", ["-lc", "dotnet test"],
            SubjectSha, SubjectSha, new string('c', 40),
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 0, null,
            new string('d', 64), new string('e', 64), LibraryStep: step);
}
