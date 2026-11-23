using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeliveryFailureEvidenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "failure-input-" + Guid.NewGuid().ToString("N"));
    private const string Command = "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound";

    [Fact]
    public void RedReceiptCarriesEveryTestErrorCommandAndTestedShaIntoNextRound()
    {
        var names = Enumerable.Range(1, 3).Select(index => $"Example.Tests.Failure{index}" + new string('x', 1100)).ToArray();
        var gate = new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 100, "", "red", false, false)
        {
            TestedSha = new string('a', 40),
            Processes = [new BuildTestGateProcessEvidence
            {
                Command = Command, ExitCode = 1,
                TestReport = new GateTestReport(new GateTestCounts(3, 0, 3, 0), names)
                {
                    FailureMessages = names.ToDictionary(name => name, name => "Expected true for " + name),
                },
            }],
        };
        var gateStep = new PipelineStepExecution { StepId = PipelineCatalogue.BuildTestGateStepId,
            Status = PipelineStepStatus.Failed, Reason = string.Join(", ", names) + " " + Command };
        var escalation = new PipelineStepExecution { StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Status = PipelineStepStatus.Failed, Reason = "Escalated red gate." };
        var job = new AgentStudio.Shared.TaskInfo { FolderPath = _folder };
        var before = TaskFailureContinuationService.BuildContinuationPrompt(job,
            [gateStep, escalation], escalation, null, null, null).Prompt;
        Assert.DoesNotContain(names[2], before);
        Assert.DoesNotContain(Command, before);
        IntegrationGateReceipts.Record(_folder, "pre-develop-test-gate", gate);
        var prompt = DeliveryFailureEvidence.Append(_folder, "Fix the red gate.");
        Assert.Contains(Command, prompt);
        Assert.Contains(gate.TestedSha, prompt);
        foreach (var name in names)
        {
            Assert.Contains(name, prompt);
            Assert.Contains("Expected true for " + name, prompt);
        }
        Assert.Contains(File.ReadAllText(Path.Combine(_folder, DeliveryFailureEvidence.RelativePath)), prompt);
        Assert.Equal(prompt, DeliveryFailureEvidence.Append(_folder, prompt));
        var automatic = TaskFailureContinuationService.BuildContinuationPrompt(job,
            [gateStep, escalation], escalation, null, null, null).Prompt;
        foreach (var name in names) Assert.Contains(name, automatic);
        Assert.Contains(Command, automatic);
    }

    [Fact]
    public void CompactReceiptRetainsCommandAndErrorsBeforeOutputIsCut()
    {
        var path = Path.Combine(_folder, "receipt.json");
        FallbackGateEvidence.Write(path, new(BuildTestGateVerdict.Fail, 1, 1, new string('z', 100_000), "red", false, false)
        {
            Processes = [new() { Command = Command, ExitCode = 1,
                TestReport = new(new(1, 0, 1, 0), ["Example.Failure"])
                { FailureMessages = new Dictionary<string, string> { ["Example.Failure"] = "Expected 42" } } }],
        });
        var receipt = File.ReadAllText(path);
        Assert.Contains(Command, receipt);
        Assert.Contains("Expected 42", receipt);
    }

    [Fact]
    public void TrxRetainsFirstErrorEvenWhenConsoleTailHasNoFailure()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "test.trx");
        File.WriteAllText(path, """
            <TestRun><Results><UnitTestResult testName="Example.Failure" outcome="Failed">
            <Output><ErrorInfo><Message>Expected 42
            Stack information</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>
            """);
        var timing = new GateTestTiming();
        timing.ReadTrx(path);
        Assert.Equal("Expected 42", timing.Report!.FailureMessages["Example.Failure"]);
    }

    [Fact]
    public void ConflictReceiptCarriesCommandErrorAndFullShasIntoTheRound()
    {
        var log = new PipelineExecutionLog(TokenTestServices.Pricing(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutionLog>.Instance);
        log.Begin(_folder, PipelineCatalogue.Standard, project: "demo", jobId: "task");
        log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = "merge-into-develop", Status = PipelineStepStatus.Failed,
            ConflictReport = new AgentStudio.Git.IntegrationConflictReport
            {
                DeliverySha = new string('c', 40), IntegrationTipSha = new string('d', 40),
                FailedCommand = "git merge --no-ff task/1", FirstError = "CONFLICT in shared.cs",
                ConflictedFileCount = 1, ConflictedFiles = ["shared.cs"],
            },
        });
        var prompt = DeliveryFailureEvidence.Append(_folder, "Resolve the conflict.");
        Assert.Contains("git merge --no-ff task/1", prompt);
        Assert.Contains("CONFLICT in shared.cs", prompt);
        Assert.Contains(new string('c', 40), prompt);
        Assert.Contains(new string('d', 40), prompt);
    }

    [Theory]
    [InlineData("  Failed Example.Failure [1 ms]\n  Error Message:\n   Expected 42\n  Stack Trace:\n ignored")]
    [InlineData("[xUnit.net 00:00:01] Example.Failure [FAIL]\n[xUnit.net 00:00:01] Expected 42")]
    public void FirstErrorReadsTheDiagnosticInsteadOfItsHeading(string output)
        => Assert.Equal("Expected 42", DeliveryFailureEvidence.FirstError(output, "Example.Failure"));

    [Fact]
    public void LegacyParkRecoversTheReceiptInsteadOfUsingTheDisplayReason()
    {
        var path = Path.Combine(_folder, "gate.json");
        FallbackGateEvidence.Write(path, new(BuildTestGateVerdict.Fail, 1, 1, "", "red", false, false)
        {
            TestedSha = new string('b', 40),
            Processes = [new() { Command = Command, ExitCode = 1,
                TestReport = new(new(1, 0, 1, 0), ["Example.LegacyFailure"]) }],
        });
        ParkedBlockerMarker.Write(_folder, new() { Reason = "Short display reason.\nEvidence: " + path });
        var prompt = DeliveryFailureEvidence.Append(_folder, "Fix it.");
        Assert.Contains(Command, prompt);
        Assert.Contains("Example.LegacyFailure", prompt);
        Assert.Contains("Not recorded in receipt.", prompt);
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
