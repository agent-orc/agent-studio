using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TestEnvelopeCanaryProcessTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Daemon_configuration_is_removed_and_declared_configuration_fails_the_real_canary()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var project = Path.Combine(repository, "backend.Tests/OrchestratorApi.Tests.csproj");
        Assert.True(File.Exists(project), project);
        var oldValue = Environment.GetEnvironmentVariable("RUNNER_SERVER_URL");
        try
        {
            Environment.SetEnvironmentVariable("RUNNER_SERVER_URL", "https://daemon.invalid");
            using var clean = new TestCommandEnvelope(TestEnvelope.EmptyEnvironment);
            var green = await RunCanary(clean, repository, project);
            Assert.True(green.Success, green.StdOut + green.StdErr);
            using var declared = new TestCommandEnvelope(new Dictionary<string, string>
                { ["RUNNER_SERVER_URL"] = "https://declared.invalid" });
            var failed = await RunCanary(declared, repository, project);
            Assert.False(failed.Success);
            Assert.Contains("Forbidden variable: RUNNER_SERVER_URL", failed.StdOut + failed.StdErr);
        }
        finally { Environment.SetEnvironmentVariable("RUNNER_SERVER_URL", oldValue); }
    }

    private static async Task<ProcessResult> RunCanary(TestCommandEnvelope envelope, string repository, string project)
    {
        var launch = envelope.Launch("dotnet", ["test", project, "--no-build", "--filter", "FullyQualifiedName~TestEnvelopeCanaryTests"]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, repository,
            environment: envelope.Environment, clearEnvironment: true, ct: deadline.Token);
    }
}
