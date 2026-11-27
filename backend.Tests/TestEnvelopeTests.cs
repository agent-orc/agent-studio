using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestEnvelopeTests
{
    [Fact]
    public void Canary_requires_a_complete_envelope_once_any_marker_is_declared()
    {
        var legacy = new Dictionary<string, string?> { ["HOME"] = "/host/home" };
        Assert.False(TestEnvelope.HasEnvelopeDeclaration(legacy));
        var partial = new Dictionary<string, string?>(legacy) { ["TEST_ENVELOPE_SHAPE"] = "standard" };
        Assert.True(TestEnvelope.HasEnvelopeDeclaration(partial));
        Assert.Contains("HOME is not owned by this run.", TestEnvelope.Violations(partial, null));
        Assert.Contains("Processor count is missing.", TestEnvelope.Violations(partial, null));
    }

    [Fact]
    public void Host_variables_are_allowlisted_and_owned_paths_override_declarations()
    {
        var declared = new Dictionary<string, string> { ["HOME"] = "/foreign", ["PROJECT_VALUE"] = "yes",
            ["OPENAI_API_KEY"] = "fixture-" + Guid.NewGuid().ToString("N"), ["DOTNET_PROCESSOR_COUNT"] = "99" };
        var environment = TestEnvelope.BuildEnvironment(declared, "/owned/home", "/owned/temp", _ => "host");
        Assert.DoesNotContain("RUNNER_SERVER_URL", environment.Keys);
        Assert.DoesNotContain("AGENT_HOST_SERVER_URL", environment.Keys);
        Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
        Assert.DoesNotContain("GIT_CONFIG_COUNT", environment.Keys);
        Assert.Equal("/owned/home", environment["HOME"]);
        Assert.Equal("/owned/temp", environment["TMPDIR"]);
        Assert.Equal(TestEnvelope.ProcessorCount.ToString(), environment["DOTNET_PROCESSOR_COUNT"]);
        Assert.Equal("yes", environment["PROJECT_VALUE"]);
        Assert.Empty(TestEnvelope.Violations(environment, null));
    }

    [Fact]
    public void Declared_daemon_configuration_fails_the_canary_without_exposing_values()
    {
        var environment = TestEnvelope.BuildEnvironment(new Dictionary<string, string>
            { ["RUNNER_SERVER_URL"] = "https://fixture.invalid" }, "/owned/home", "/owned/temp", _ => null);
        Assert.Equal(["Forbidden variable: RUNNER_SERVER_URL"], TestEnvelope.Violations(environment, null));
    }

    [Fact]
    public void Failed_canary_is_gate_environment_evidence_even_with_failed_tests()
    {
        var process = new BuildTestGateProcessEvidence { ExitCode = 1, FailedTestsObserved = true,
            StandardOutput = "  Failed AgentStudio.Tests.TestEnvelopeCanaryTests.Test_process_has_the_declared_envelope [1 ms]" };
        Assert.Equal(BuildTestGateFailureKind.Environment, BuildTestGateRunner.ClassifyFailure(process));
    }

    [Fact]
    public void Undelegated_host_records_unavailable_and_still_builds_a_launch()
    {
        var logs = new List<string>();
        using var envelope = new TestCommandEnvelope(TestEnvelope.EmptyEnvironment, logs.Add, null, null, null,
            Path.Combine(Path.GetTempPath(), "missing-cgroup-" + Guid.NewGuid().ToString("N")), "/session.scope/daemon");
        var fingerprint = envelope.Fingerprint(TestEnvelope.EmptyEnvironment, "suite=1");
        Assert.Equal(OperatingSystem.IsLinux() ? "unavailable" : "none", fingerprint.CgroupKind);
        Assert.Null(fingerprint.CpuMax);
        Assert.Equal("dotnet", envelope.Launch("dotnet", ["test"]).FileName);
        if (OperatingSystem.IsLinux()) Assert.Contains(logs, line => line.Contains("unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_definition_uses_one_process_and_the_shared_token()
    {
        Assert.Equal(1, TestEnvelope.Reference.Processes);
        using var envelope = FakeEnvelope(TestEnvelope.Reference);
        Assert.Equal("reference", envelope.Environment["TEST_ENVELOPE_SHAPE"]);
        Assert.Equal(TestEnvelope.ProcessorCount.ToString(), envelope.Environment["DOTNET_PROCESSOR_COUNT"]);
    }

    [Fact]
    public void Fingerprints_ignore_owned_path_values_and_compare_the_resource_shape()
    {
        using var first = FakeEnvelope();
        using var second = FakeEnvelope();
        var review = first.Fingerprint(TestEnvelope.EmptyEnvironment, "suite=4");
        var reference = second.Fingerprint(TestEnvelope.EmptyEnvironment, TestEnvelope.ReferenceShardPlan);
        Assert.True(TestEnvelope.MatchesReference(review, reference));
        Assert.False(TestEnvelope.MatchesReference(review, reference with { CpuMax = "100000 100000" }));
        Assert.NotEqual(first.Environment["HOME"], second.Environment["HOME"]);
    }

    [Fact]
    public void Canary_rejects_a_daemon_cgroup_when_a_worker_was_attached()
    {
        if (!OperatingSystem.IsLinux()) return;
        var environment = TestEnvelope.BuildEnvironment(TestEnvelope.EmptyEnvironment, "/owned/home", "/owned/temp", _ => null);
        environment["TEST_ENVELOPE_CGROUP"] = "worker";
        Assert.Contains("The process is outside its worker cgroup.", TestEnvelope.Violations(environment, "0::/unit/daemon\n"));
        Assert.Empty(TestEnvelope.Violations(environment, "0::/unit/worker-test-123\n"));
    }
    private static TestCommandEnvelope FakeEnvelope(TestEnvelopeDefinition? definition = null)
        => new(TestEnvelope.EmptyEnvironment, null, null, null, definition,
            Path.Combine(Path.GetTempPath(), "fake-cgroup-" + Guid.NewGuid().ToString("N")), "/session.scope/daemon");

}
