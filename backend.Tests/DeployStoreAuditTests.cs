using AgentStudio.Deploy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeployStoreAuditTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 20, 16, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deploy-audit-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_runbook_and_deployment_audit_appends_keep_every_event()
    {
        var store = new DeployStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build());
        const int rounds = 200;
        using var start = new Barrier(2);
        // The runbook and deployment services lock separately, so both writers reach the shared store at once.
        Task Writer(string action) => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var index = 0; index < rounds; index++)
                store.AppendAudit("demo", [new DeployAuditEntry(At, "owner", action, null, $"{action}-{index}", "stage", "")]);
        });

        await Task.WhenAll(Writer("runbook-approved"), Writer("deploy-approved"));

        var audit = store.ReadAudit("demo", rounds * 4);
        Assert.Equal(rounds * 2, audit.Count);
        Assert.Equal(rounds, audit.Count(entry => entry.Action == "runbook-approved"));
        Assert.Equal(rounds, audit.Count(entry => entry.Action == "deploy-approved"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
