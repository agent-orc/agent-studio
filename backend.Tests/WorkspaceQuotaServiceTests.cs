using AgentStudio.Quota;
using AgentStudio.Registry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkspaceQuotaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workspace-quota-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Owner_override_is_audited_persisted_and_expires_at_week_rollover()
    {
        Directory.CreateDirectory(_root);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["TaskRepository"] = _root, ["PublicDemo:Enabled"] = "false" }))
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(_clock);
                }));
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(0, 0, 0));
        Assert.Equal("blocked", quota.Snapshot(workspace.Id).Status);

        quota.Override(workspace.Id, null, "owner-1", "Incident response");
        Assert.True(quota.Snapshot(workspace.Id).OverrideActive);
        var entry = Assert.Single(quota.Audit(workspace.Id, null));
        Assert.Equal(_clock.GetUtcNow(), entry.At);
        Assert.Equal("owner-1", entry.Actor);
        Assert.Equal("Incident response", entry.Reason);

        var reloaded = ActivatorUtilities.CreateInstance<WorkspaceQuotaService>(services);
        Assert.Single(reloaded.Audit(workspace.Id, null));
        _clock.Advance(TimeSpan.FromDays(4));
        Assert.False(reloaded.Snapshot(workspace.Id).OverrideActive);
        Assert.Equal("blocked", reloaded.Snapshot(workspace.Id).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
