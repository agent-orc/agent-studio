using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewLibraryStepPolicyTests
{
    private const string SubjectSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Provider_switch_keeps_the_authored_digest_and_requires_the_selected_provider()
    {
        var original = ReviewLibraryStepPolicy.Seal(new ReviewPlanDto(
            [new ReviewCommandDto("aspect-code-quality", "code-quality", "claude", [],
                ExecutionKind: ReviewCommandKinds.AgentAspect, CliType: "claude", Model: "fixture-claude", ThinkingLevel: "high")],
            ["code-quality"], LibraryVersion: 1), SubjectSha);
        var selected = new SelectionResult { Outcome = SelectionOutcome.Fallback,
            Primary = new("claude", "fixture-claude", "high"), Route = new("codex", "fixture-codex", "medium"),
            Rule = SelectionRules.QuotaCap, Reason = "fixture quota exhausted" };
        var switched = original with { Commands = [original.Commands[0] with
        {
            FileName = "codex", CliType = "codex", Model = "fixture-codex", ThinkingLevel = "medium", ModelSelection = selected,
        }] };
        Assert.True(ReviewLibraryStepPolicy.ValidPlan(switched, SubjectSha));
        Assert.Contains(CapabilityProtocol.ProviderAuthentication("claude"), ReviewLibraryStepPolicy.RequiredCapabilities(switched));
        var execution = ReviewLibraryStepPolicy.ExecutionCapabilities(switched).ToHashSet();
        Assert.DoesNotContain(CapabilityProtocol.ProviderAuthentication("claude"), execution);
        Assert.Contains(CapabilityProtocol.ProviderAuthentication("codex"), execution);
        Assert.True(ReviewLibraryStepPolicy.Supports(switched, execution));
        execution.Remove(CapabilityProtocol.ProviderAuthentication("codex"));
        Assert.False(ReviewLibraryStepPolicy.Supports(switched, execution));
    }

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

    [Fact]
    public void Missing_capabilities_name_exactly_the_keys_a_host_lacks()
    {
        var sealedPlan = ReviewLibraryStepPolicy.Seal(Plan(), SubjectSha);
        var step = sealedPlan.Commands[0].LibraryStep!;
        var host = step.RequiredCapabilities.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(ReviewLibraryStepPolicy.MissingCapabilities(sealedPlan, host));
        Assert.Equal(
            ["review:library-step:v2"],
            ReviewLibraryStepPolicy.MissingCapabilities(sealedPlan with { LibraryVersion = 2 }, host));
        host.Remove(CapabilityProtocol.DotNet);
        Assert.Equal([CapabilityProtocol.DotNet], ReviewLibraryStepPolicy.MissingCapabilities(sealedPlan, host));
    }

    [Theory]
    [InlineData("dotnet test", "toolchain:dotnet")]
    [InlineData("npm ci", "toolchain:node")]
    [InlineData("npx playwright test", "toolchain:playwright")]
    public void Every_toolchain_key_the_policy_emits_comes_from_the_published_table(string command, string key)
    {
        var sealedPlan = ReviewLibraryStepPolicy.Seal(
            new ReviewPlanDto(
                [new ReviewCommandDto("verify-1", "build-tests", "sh", ["-lc", command])],
                ["build-tests"],
                LibraryVersion: ReviewLibraryStepPolicy.Version),
            SubjectSha);
        var emitted = ReviewLibraryStepPolicy.RequiredCapabilities(sealedPlan)
            .Where(item => item.StartsWith("toolchain:", StringComparison.Ordinal));

        Assert.Contains(key, emitted);
        Assert.All(emitted, item => Assert.Contains(
            ReviewLibraryStepPolicy.ToolchainRequirements, requirement => requirement.Key == item));
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
