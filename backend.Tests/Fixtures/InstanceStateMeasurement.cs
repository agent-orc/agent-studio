using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>Opt-in suite measurement, with synthetic state owned by the test process.</summary>
internal static class InstanceStateMeasurement
{
    [ModuleInitializer]
    internal static void Seed()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_STUDIO_TEST_INSTANCE_SIBLINGS");
        if (configured is null) return;
        if (!int.TryParse(configured, out var count) || count is < 0 or > 200_000)
            throw new ArgumentException("AGENT_STUDIO_TEST_INSTANCE_SIBLINGS must be between 0 and 200000.");

        TestTempRoot.Redirect();
        var state = LocalStateRoot.Resolve();
        if (!state.StartsWith(TestTempRoot.SuiteRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic instance state must belong to this test process.");
        var timer = Stopwatch.StartNew();
        var instances = Path.Combine(state, "agentstudio", "instances");
        Directory.CreateDirectory(instances);
        for (var index = 0; index < count; index++)
            Directory.CreateDirectory(Path.Combine(instances, "measurement-" + index));

        var report = Environment.GetEnvironmentVariable("AGENT_STUDIO_TEST_INSTANCE_MEASUREMENT_FILE")
            ?? throw new InvalidOperationException("The suite measurement requires a report path.");
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            suiteRoot = TestTempRoot.SuiteRoot,
            stateRoot = state,
            siblings = count,
            seedSeconds = timer.Elapsed.TotalSeconds,
        }));
    }
}
