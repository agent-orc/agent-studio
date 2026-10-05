using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3016: command steps are judged by exit status; verdict markers come
/// only from aspect replies.
/// </summary>
public sealed class ReviewCommandVerdictPolicyTests
{
    private const string Sha = "1d3cb3c92619917ba483ac19e1332a80516d629f";

    // The build-tests row of the AGT-2954 grade (review_ebfb76a94645421399da4d61b20a36d3):
    // a released agent-host read the AspectRunnerTests sentinel out of the
    // verify-3 `dotnet test` log.
    private static readonly ReviewVerdictDto Agt2954QuotedMarkerRow = new(
        "build-tests",
        "concerns",
        "RemoteAspectVerdict; malformed: duplicate-key",
        "needs wor\"···, expectedStatus: Concerns, expectedSummary: \"needs work\") [< 1 ms]   Passed AgentStudio.Tests.AspectRunnerTests.PassAspect_JsonTwin_HasNullTag ...");

    [Fact]
    public void NormalizeReport_QuotedConcernsMarkerOnGreenCommand_PassesAndOpensTheGate()
    {
        var request = Report(
            [Step("verify-1"), Step("verify-2")],
            [
                ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-1", 0),
                new ReviewVerdictDto(
                    "build-tests",
                    "concerns",
                    "RemoteAspectVerdict; malformed: duplicate-key",
                    "[[ASPECT_VERDICT: status=concerns; summary=needs work]] quoted by a test log"),
            ]);

        var normalized = ReviewCommandVerdictPolicy.NormalizeReport(request, Plan(ToolCommands(2)));

        var repaired = normalized.Verdicts[1];
        Assert.Equal("pass", repaired.Status);
        Assert.Equal(ReviewCommandVerdictPolicy.CommandPassed, repaired.Classification);
        Assert.Equal("command:verify-2", repaired.EvidenceChecked);
        Assert.Equal(request.Verdicts[0], normalized.Verdicts[0]);
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            true, "Pass", Plan(ToolCommands(2)), normalized.Verdicts);
        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Passed, decision.BuildTestGate);
    }

    [Theory]
    [InlineData("pass", "RemoteAspectVerdict")]
    [InlineData("block", "RemoteAspectVerdict; malformed: duplicate-key")]
    [InlineData("concerns", "review:unparseable")]
    public void NormalizeReport_MarkerVerdictOnCommand_IsJudgedByExitStatusOnly(
        string quotedStatus,
        string classification)
    {
        var request = Report(
            [Step("verify-1", exitCode: 1)],
            [new ReviewVerdictDto("build-tests", quotedStatus, classification, "quoted")]);

        var verdict = Assert.Single(
            ReviewCommandVerdictPolicy.NormalizeReport(request, Plan(ToolCommands(1))).Verdicts);

        Assert.Equal("block", verdict.Status);
        Assert.Equal(ReviewCommandVerdictPolicy.CommandFailed, verdict.Classification);
        Assert.Equal("Review command 'verify-1' exited 1.", verdict.Summary);
    }

    [Fact]
    public void NormalizeReport_MalformedAspectReply_StaysUnchanged()
    {
        var aspect = new ReviewCommandDto(
            "aspect-code-quality",
            "code-quality",
            "codex",
            [],
            ExecutionKind: ReviewCommandKinds.AgentAspect);
        var malformed = new ReviewVerdictDto(
            "code-quality",
            "concerns",
            "RemoteAspectVerdict; malformed: duplicate-key",
            "needs work Detail: summary=again");
        var unparseable = malformed with
        {
            Aspect = "requirement-fit",
            Classification = ReviewCommandVerdictPolicy.UnparseableClassification,
        };
        var request = Report(
            [Step("verify-1"), Step("aspect-code-quality", "code-quality", ReviewCommandKinds.AgentAspect)],
            [ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-1", 0), malformed, unparseable]);
        var plan = Plan([
            .. ToolCommands(1),
            aspect,
            aspect with { StepId = "aspect-requirement-fit", Aspect = "requirement-fit" },
        ]);

        var normalized = ReviewCommandVerdictPolicy.NormalizeReport(request, plan);

        Assert.Same(request, normalized);
        Assert.Equal(
            ReviewFollowUpAction.RetryAspect,
            ReviewFollowUpPolicy.Decide(
                normalized.Verdicts.Select(verdict => new ReviewFollowUpFinding(
                    verdict.Aspect,
                    verdict.Status,
                    verdict.Summary,
                    verdict.EvidenceChecked,
                    verdict.Missing,
                    verdict.Classification)),
                concernRoundsUsed: 0,
                maxConcernRounds: 1).Action);
    }

    [Fact]
    public void Agt2954Grade_EvaluatesToShouldIntegrate()
    {
        var plan = Agt2954Plan();
        var request = Report(
            [
                Step("prepare-1", "preparation", phase: "preparation"),
                Step("prepare-2", "preparation", phase: "preparation"),
                .. plan.Commands.Select(command => Step(command.StepId, command.Aspect, command.ExecutionKind)),
            ],
            [
                ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-1", 0),
                ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-2", 0),
                Agt2954QuotedMarkerRow,
                ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-4", 0),
                ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-5", 0),
                ReviewCommandVerdictPolicy.FromExitCode("build-tests", "verify-6", 0),
                ReviewCommandVerdictPolicy.FromExitCode("lint", "verify-7", 0),
                AspectPass("requirement-fit"),
                AspectPass("code-quality"),
                AspectPass("documentation-impact"),
                AspectPass("tests-and-evidence"),
            ]);

        // The refusal the operator saw on 2026-10-05.
        var unrepaired = RemoteDeliveryIntegrationPolicy.Decide(true, request.Outcome, plan, request.Verdicts);
        Assert.False(unrepaired.ShouldIntegrate);
        Assert.Equal("At least one applicable Remote Review build/test gate did not pass.", unrepaired.Reason);

        // Same normalization order as POST /reviews/attempts/{attemptId}/report.
        var normalized = ReviewCommandVerdictPolicy.NormalizeReport(request, plan);
        normalized = ReviewVerdictCitationPolicy.NormalizeReport(
            normalized,
            plan.Commands
                .Where(command => ReviewCommandKinds.IsAgent(command.ExecutionKind))
                .Select(command => command.Aspect));
        normalized = ReviewReportDiagnosisPolicy.Normalize(normalized, plan);

        Assert.Equal("Pass", normalized.Outcome);
        var verify3 = normalized.Verdicts[2];
        Assert.Equal("pass", verify3.Status);
        Assert.Equal("command:verify-3", verify3.EvidenceChecked);
        Assert.DoesNotContain(normalized.Verdicts, ReviewCommandVerdictPolicy.IsMarkerDerived);
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, normalized.Outcome, plan, normalized.Verdicts);
        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Passed, decision.BuildTestGate);
    }

    private static ReviewPlanDto Agt2954Plan()
    {
        string[] verify =
        [
            "dotnet build -maxcpucount:2 -nodeReuse:false agent-taskboard.sln --no-restore",
            "npm --prefix frontend run build",
            "dotnet test --logger \"console;verbosity=normal\" backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound",
            "dotnet test --logger \"console;verbosity=normal\" runner.Tests/AgentRunner.Tests.csproj --no-build --filter Category!=MachineBound",
            "dotnet test --logger \"console;verbosity=normal\" task-server.Tests/TaskServer.Tests.csproj --no-build --filter Category!=MachineBound",
            "npm --prefix frontend run test:ci",
            "npm --prefix frontend run lint",
        ];
        var commands = verify
            .Select((line, index) => new ReviewCommandDto(
                $"verify-{index + 1}",
                index == verify.Length - 1 ? "lint" : "build-tests",
                "sh",
                ["-lc", line],
                CompareToBaseline: true))
            .Concat(new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" }
                .Select(aspect => new ReviewCommandDto(
                    $"aspect-{aspect}",
                    aspect,
                    "codex",
                    [],
                    ExecutionKind: ReviewCommandKinds.AgentAspect)))
            .ToArray();
        return Plan(commands);
    }

    private static ReviewVerdictDto AspectPass(string aspect)
        => new(aspect, "pass", "RemoteAspectVerdict", $"{aspect} passed.", "docs/system/domains/pipeline.md", "none");

    private static ReviewCommandDto[] ToolCommands(int count)
        => Enumerable.Range(1, count)
            .Select(index => new ReviewCommandDto($"verify-{index}", "build-tests", "sh", ["-lc", "dotnet test"]))
            .ToArray();

    private static ReviewPlanDto Plan(IReadOnlyList<ReviewCommandDto> commands)
        => new(commands, commands.Select(command => command.Aspect).Distinct().ToArray());

    private static ReviewCommandEvidenceDto Step(
        string stepId,
        string aspect = "build-tests",
        string executionKind = ReviewCommandKinds.Tool,
        int exitCode = 0,
        string phase = "verification")
        => new(
            stepId,
            aspect,
            "sh",
            [],
            Sha,
            Sha,
            "tree",
            DateTime.UnixEpoch,
            DateTime.UnixEpoch,
            exitCode,
            null,
            "stdout",
            "stderr",
            Phase: phase,
            ExecutionKind: executionKind);

    private static ReviewReportRequest Report(
        IReadOnlyList<ReviewCommandEvidenceDto> commands,
        IReadOnlyList<ReviewVerdictDto> verdicts)
        => new(
            "review-executor",
            "instance",
            "lease",
            1,
            "key",
            "Pass",
            null,
            null,
            new ReviewWorkspaceProofDto("repo", Sha, Sha, "tree", false, false, "workspace", "namespace"),
            new ReviewEnvironmentDto(
                "host",
                "review-executor",
                "instance",
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            commands,
            [],
            verdicts);
}
