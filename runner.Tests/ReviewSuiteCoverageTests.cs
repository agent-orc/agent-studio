using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: command timestamps are fixed data and never compared with the current date.

public sealed class ReviewSuiteCoverageTests
{
    [Fact]
    public void CoverageUsesFrozenStepsDespiteCommandNormalizationAndExcludesBaselineAndUnstartedSuites()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var selection = new ReviewSuiteSelectionDto(["backend", "frontend"], ["docs"], ["Changed code."])
        {
            SuiteSteps = new Dictionary<string, string> { ["backend"] = "verify-2", ["frontend"] = "verify-3" },
        };
        ReviewCommandEvidenceDto Evidence(string step, string role, int? exit) => new(step, "build-tests", "sh",
            ["-lc", "dotnet test Changed.Tests.csproj -maxcpucount:2 --logger console"],
            new string('a', 40), new string('a', 40), new string('b', 40), now, now,
            exit, null, "", "", WorkspaceRole: role);
        var result = RemoteReviewExecutor.SuiteCoverage(selection,
            [Evidence("verify-2", "baseline-exact", 0), Evidence("verify-2", "candidate", 1),
             Evidence("verify-3", "candidate", null)]);
        Assert.Equal(["backend"], result!.AttemptedSuites);
        Assert.Equal(["docs"], result.OmittedSuites);
        Assert.Null(RemoteReviewExecutor.SuiteCoverage(null, []));
    }
}
