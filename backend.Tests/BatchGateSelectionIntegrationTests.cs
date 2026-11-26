using AgentStudio.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class BatchGateSelectionIntegrationTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task MixedBatchFreezesAndSendsTheMemberUnionToTheGate()
    {
        Git(_repo, "checkout", "-q", "--detach", "refs/remotes/origin/develop");
        File.WriteAllText(Path.Combine(_repo, ".agent-studio/project.yml"), """
            schemaVersion: 1
            stack: [custom]
            commands:
              prepare: .agent-studio/prepare
              build: [echo backend-build, echo frontend-build]
              test: [echo backend-test, echo frontend-test, echo docs-test]
              lint: [echo frontend-lint]
            testSuites:
              - id: backend
                category: routine
                expectedDurationSeconds: 1
                command: echo backend-test
              - id: frontend
                category: routine
                expectedDurationSeconds: 1
                command: echo frontend-test
              - id: docs
                category: routine
                expectedDurationSeconds: 1
                command: echo docs-test
            testSelection:
              - paths: [backend/]
                suites: [backend]
                build: [0]
              - paths: [frontend/]
                suites: [frontend]
                build: [1]
                lint: [0]
              - paths: [docs/]
                suites: [docs]
            """);
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", "test selection inventory");
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        Git(_repo, "fetch", "-q", "origin");
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        SeedMember(factory, "SELECT-FRONTEND", "frontend/a.ts");
        SeedMember(factory, "SELECT-DOCS", "docs/a.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = true;

        await pilot.TickAsync(CancellationToken.None);

        var input = Assert.Single(_gate.SelectionInputs, input => input.Request.GateId == "batch-gate");
        Assert.Equal(TestExecutionLevels.WorkPackage, input.Request.RequiredTestLevel);
        Assert.Equal(["docs/a.md", "frontend/a.ts"], input.Paths);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        Assert.Contains("echo frontend-test", run.Commands);
        Assert.Contains("echo docs-test", run.Commands);
        Assert.DoesNotContain("echo backend-test", run.Commands);
        Assert.DoesNotContain("echo backend-build", run.Commands);
    }
}
