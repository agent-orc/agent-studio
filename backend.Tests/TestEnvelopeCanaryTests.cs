using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Base-suite boundary check. This test must run inside the test command envelope.</summary>
public sealed class TestEnvelopeCanaryTests
{
    [Fact]
    public void Test_process_has_the_declared_envelope()
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        var cgroup = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/cgroup") : null;
        Assert.Empty(TestEnvelope.Violations(environment, cgroup));
        Assert.Contains(environment.GetValueOrDefault("TEST_ENVELOPE_CGROUP"), new[] { "worker", "unavailable", "none" });
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var root = environment["TEST_ENVELOPE_TEMP"]!;
        Assert.True(temp == root || temp.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "The test host temp directory escaped the run-owned root.");
    }
}
