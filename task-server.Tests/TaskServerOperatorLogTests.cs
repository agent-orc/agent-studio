using AgentStudio.TaskServer;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class TaskServerOperatorLogTests
{
    [Fact]
    public async Task Append_is_durable_ordered_and_redacts_before_storage()
    {
        var root = Path.Combine(Path.GetTempPath(), "operator-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
            var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = root }), clock);
            var secretPart = new string('c', 32);
            var secret = "sat." + new string('b', 32) + "." + secretPart;
            var first = await store.AppendOperatorLogAsync(
                new TaskServerOperatorLogWrite("*", "observed", "Saw " + secret, [], []),
                "operator-agent:fixture", CancellationToken.None);
            var second = await store.AppendOperatorLogAsync(
                new TaskServerOperatorLogWrite("*", "decided", "Wait for review.", [], []),
                "operator-agent:fixture", CancellationToken.None);
            var restarted = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = root }), clock);
            Assert.Equal([first.Cursor, second.Cursor], restarted.ReadOperatorLog("fixture-project", 0)
                .Select(entry => entry.Cursor));
            Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "operator", "operator-log.jsonl")));
            Assert.DoesNotContain(secretPart, File.ReadAllText(Path.Combine(root, "operator", "operator-log.jsonl")));
            Assert.Contains("[redacted]", restarted.ReadOperatorLog("fixture-project", 0)[0].Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
