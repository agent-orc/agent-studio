using AgentStudio.Setup;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class DockerPreflightTests
{
    [Theory]
    [InlineData((int)DockerEngineState.NotInstalled, "Docker not installed", "Install Docker Engine")]
    [InlineData((int)DockerEngineState.NotRunning, "Docker engine not running", "systemctl enable --now docker")]
    [InlineData((int)DockerEngineState.PermissionDenied, "Docker access denied", "docker group")]
    public void Linux_failure_reports_matching_recovery(
        int stateValue, string observed, string recovery)
    {
        var state = (DockerEngineState)stateValue;
        var facts = new HostFacts(false, true, null, state, true, null,
            30L << 30, null, null, null, true, null, null);

        var finding = Assert.Single(PreflightPolicy.Evaluate(InstallationJourney.OneBox, "docker", facts),
            item => item.Check == "Docker");

        Assert.Equal(PreflightStatus.Fail, finding.Status);
        Assert.Equal(observed, finding.Observed);
        Assert.Contains(recovery, finding.Recovery);
        if (state == DockerEngineState.PermissionDenied)
            Assert.Contains("log out and log in again", finding.Recovery);
    }

    [Theory]
    [InlineData(0, "", (int)DockerEngineState.Running)]
    [InlineData(1, "permission denied while trying to connect to the Docker daemon socket", (int)DockerEngineState.PermissionDenied)]
    [InlineData(1, "Cannot connect to the Docker daemon", (int)DockerEngineState.NotRunning)]
    public void Docker_info_result_classifies_engine_state(int exitCode, string error, int expected)
        => Assert.Equal((DockerEngineState)expected, PreflightProbe.ClassifyDocker(new ProcessResult(exitCode, "", error)));

    [Fact]
    public void Missing_docker_executable_classifies_as_not_installed()
        => Assert.Equal(DockerEngineState.NotInstalled, PreflightProbe.ClassifyDocker(null));
}
