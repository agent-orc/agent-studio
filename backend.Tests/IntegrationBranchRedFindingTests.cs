using AgentStudio.Pipeline;
using AgentStudio.Review;
using AgentStudio.Tasks;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3812: a Remote Review that classified a build-tests step as already red
/// on the integration branch parks the card in Human Review. The park reason,
/// which the Needs you row and the park panel show, must name the red branch
/// and the decision instead of "Remote Review finished with a finding".
/// </summary>
public sealed class IntegrationBranchRedFindingTests
{
    private const string Baseline = "08eb16b77409d04ab92e5da76ec88c0a5a4e586a";

    [Fact]
    public void ReviewPark_NamesTheRedIntegrationBranchAndTheDecision()
    {
        var reason = RemoteDeliveryParkReason.ForReview(passed: false, RedDevelopReport());

        Assert.Equal(
            "Integration branch develop is red: npm test (verify-2) is red on develop at 08eb16b7 without this change; "
            + "failing tests: test. Decide whether to accept this delivery while develop is red or to hold it until develop is fixed.",
            reason);
    }

    [Fact]
    public void ReviewPark_KeepsTheGenericTextWhenTheIntegrationBranchIsGreen()
    {
        var report = RedDevelopReport() with
        {
            Verdicts = [new Contract.ReviewVerdictDto("build-tests", "pass", "CommandPassed", "Review command 'verify-2' passed.")],
        };

        Assert.Equal(RemoteDeliveryParkReason.ReviewDecisionPark, RemoteDeliveryParkReason.ForReview(false, report));
        Assert.Equal(RemoteDeliveryParkReason.CompletedReviewPark, RemoteDeliveryParkReason.ForReview(true, report));
        Assert.Equal(RemoteDeliveryParkReason.CompletedReviewPark, RemoteDeliveryParkReason.ForReview(true, null));
    }

    [Fact]
    public void NeedsYouRow_StatesTheRedIntegrationBranchAsTheOwnerDecision()
    {
        var reason = RemoteDeliveryParkReason.ForReview(passed: false, RedDevelopReport());
        var park = ParkedBlockerCatalog.Build(TaskStates.HumanReview, reason, DateTime.UnixEpoch,
            "review-verdict", "IntegrationBranchDefect")!;
        var status = new ParkedBlockerStatus(park.BlockerType, ParkedBlockerConditionKinds.Manual,
            "A person clears this park.", DateTime.UnixEpoch, 60, park.Reason,
            ParkedBlockerStatuses.Undeterminable, null, "");

        var verdict = AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(
            CompletionAdmissionPreview.Admitted("Content review passed for the current brief.", null, null),
            TaskModes.Coding, status));

        Assert.Equal(AcceptanceReadinessClasses.OwnerDecision, verdict.Readiness);
        Assert.StartsWith("Parked for an owner decision: Integration branch develop is red: npm test (verify-2)", verdict.Reason);
        Assert.Contains("failing tests: test", verdict.Reason);
    }

    [Theory]
    [InlineData("sh -lc npm test", "npm test")]
    [InlineData("bash -c \"dotnet test x.csproj\"", "dotnet test x.csproj")]
    [InlineData("dotnet test", "dotnet test")]
    public void DisplayCommand_DropsTheShellWrapper(string command, string expected)
        => Assert.Equal(expected, IntegrationBranchRedFinding.DisplayCommand(command));

    [Fact]
    public void Finding_OfARedBuildWithoutTests_OmitsTheTestList()
    {
        var finding = IntegrationBranchRedFinding.FromVerdict(
            null,
            $"build verify-1 was already red on the integration branch; failed-tests=; confidence=1.00; baseline={Baseline} (baseline executed in this attempt)",
            $"command:verify-1; baseline:{Baseline}",
            _ => "sh -lc npm run build",
            null);

        Assert.Equal("npm run build (verify-1) is red on the integration branch at 08eb16b7 without this change.", finding.Sentence);
    }

    private static Contract.ReviewReportRequest RedDevelopReport()
    {
        var sha = "5301026033f474afafff7330965e1c09b4d05562";
        // clock-independent: command timestamps are report data and never compared with now.
        var at = new DateTime(2026, 11, 15, 22, 0, 0, DateTimeKind.Utc);
        return new Contract.ReviewReportRequest(
            "reviewer", "instance", "lease", 4, "settle", "IntegrationBranchDefect", "IntegrationBranchDefect",
            "At least one remote review aspect found a product concern.",
            new Contract.ReviewWorkspaceProofDto("repo", sha, sha, "tree", false, false, "workspace", "namespace",
                IntegrationRef: "refs/heads/develop", MergeBaseSha: Baseline, IntegrationTipSha: Baseline),
            new Contract.ReviewEnvironmentDto("host", "reviewer", "instance", "os", "arch", "runtime",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [
                new Contract.ReviewCommandEvidenceDto("verify-1", "build-tests", "sh", ["-lc", "npm run build"],
                    sha, sha, "tree", at, at, 0, null, "a", "b"),
                new Contract.ReviewCommandEvidenceDto("verify-2", "build-tests", "sh", ["-lc", "npm test"],
                    sha, sha, "tree", at, at, 1, null, "c", "d", BaselineSha: Baseline),
            ],
            [],
            [
                new Contract.ReviewVerdictDto("build-tests", "pass", "CommandPassed", "Review command 'verify-1' passed.",
                    "command:verify-1", "none"),
                new Contract.ReviewVerdictDto("build-tests", "pass", "IntegrationBranchDefect",
                    $"pre-existing failures: test; clean repeat found no new failures; failed-tests=test; confidence=1.00; baseline={Baseline} (baseline executed in this attempt)",
                    $"command:verify-2; baseline:{Baseline}", "none"),
            ]);
    }
}
