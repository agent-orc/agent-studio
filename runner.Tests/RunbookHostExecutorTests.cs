using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunbookHostExecutorTests
{
    [Fact]
    public async Task Executes_ordered_steps_with_environment_variables_and_stops_after_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-host-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        var secrets = Path.Combine(root, "secrets");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(secrets);
        var marker = Path.Combine(root, "marker");
        var secret = "fixture-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(secrets, "db-secret"), secret);
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "printf '%s:%s:%s:%s' \"$LOG_LEVEL\" \"$DATABASE_URL\" \"$DB_SECRET\" \"$AGENT_STUDIO_ENVIRONMENT\" > \"$MARKER\"\n");
        File.WriteAllText(Path.Combine(scripts, "rotate.sh"), "exit 7\n");
        File.WriteAllText(Path.Combine(scripts, "restart.sh"), "printf 'unexpected' >> \"$MARKER\"\n");
        try
        {
            var result = await RunbookHostExecutor.ExecuteAsync("demo", "production", "8082",
                ["backup", "rotate", "restart"],
                new Dictionary<string, string> { ["LOG_LEVEL"] = "info", ["MARKER"] = marker },
                new Dictionary<string, string> { ["DATABASE_URL"] = "db-secret" },
                ["db-secret"],
                Path.Combine(root, "scripts"), secrets, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal([new RunbookHostStepResult(0, true), new RunbookHostStepResult(1, false)], result.Steps);
            Assert.Equal($"info:{secret}:{secret}:production", File.ReadAllText(marker));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Refuses_path_traversal_before_starting_a_script()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
            "demo", "stage", "8081", ["../escape"], new Dictionary<string, string>(),
            new Dictionary<string, string>(), [], "/tmp", "/tmp", CancellationToken.None));
    }
}
