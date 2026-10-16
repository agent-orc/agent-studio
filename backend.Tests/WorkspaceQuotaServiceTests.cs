using AgentStudio.Quota;
using AgentStudio.Registry;
using AgentStudio.Bus;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
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

    [Fact]
    public void Production_gate_store_has_quota_and_task_scanner()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var store = services.GetRequiredService<MonolithGateStore>();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Assert.Same(services.GetRequiredService<WorkspaceQuotaService>(),
            typeof(MonolithGateStore).GetField("_workspaceQuota", flags)!.GetValue(store));
        Assert.Same(services.GetRequiredService<TaskScannerService>(),
            typeof(MonolithGateStore).GetField("_scanner", flags)!.GetValue(store));
    }

    [Fact]
    public async Task Archived_project_usage_still_blocks_workspace_admission()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var projects = services.GetRequiredService<ProjectRegistry>();
        var project = projects.EnsureProjectForStorage(Path.Combine(_root, "alpha"), "Alpha", workspace.Id, _clock);
        Directory.CreateDirectory(project.StorageLocation);
        await services.GetRequiredService<AgentMessageBusStore>().AppendAsync(_root, new AgentMessage
        {
            Id = "01HX0000000000000000000031",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            ParticipantId = "agent:codex",
            Role = "evidence",
            Kind = "token-usage",
            Project = project.DisplayName,
            Summary = "tokens",
            Tokens = new AgentMessageTokens(Input: 90, Output: 20, Model: "gpt-5.6-sol"),
        });
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(100, null, null));
        Assert.Equal(110, quota.Snapshot(workspace.Id).Usage.ModelTokens);
        projects.SetArchived(project.Id, true);
        Assert.Equal(110, quota.Snapshot(workspace.Id).Usage.ModelTokens);
        Assert.False(quota.Admit(project.Id).Allowed);
    }

    [Fact]
    public async Task Partial_ledger_refuses_admission_even_when_other_tokens_are_readable()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "partial"), "Partial", workspace.Id, _clock);
        var job = Path.Combine(project.StorageLocation, TaskStates.Ready, "partial-card");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), "{invalid-json");
        await services.GetRequiredService<AgentMessageBusStore>().AppendAsync(_root, new AgentMessage
        {
            Id = "01HX0000000000000000000032",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            ParticipantId = "agent:codex",
            Role = "evidence",
            Kind = "token-usage",
            Project = project.DisplayName,
            Summary = "tokens",
            Tokens = new AgentMessageTokens(Input: 10, Output: 5, Model: "gpt-5.6-sol"),
        });
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(100, null, null));
        var snapshot = quota.Snapshot(workspace.Id);
        Assert.Equal(15, snapshot.Usage.ModelTokens);
        Assert.False(snapshot.MeterComplete);
        Assert.False(quota.Admit(project.Id).Allowed);
    }

    [Fact]
    public void Hosting_reservation_is_atomic_and_releases_when_environment_stops()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var projects = services.GetRequiredService<ProjectRegistry>();
        var alpha = projects.EnsureProjectForStorage(Path.Combine(_root, "alpha"), "Alpha", workspace.Id, _clock);
        var beta = projects.EnsureProjectForStorage(Path.Combine(_root, "beta"), "Beta", workspace.Id, _clock);
        Directory.CreateDirectory(alpha.StorageLocation);
        Directory.CreateDirectory(beta.StorageLocation);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(null, null, 1));
        var admitted = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(new[] { alpha, beta }, project =>
        {
            try { if (quota.ReserveProjectEnvironment(project.Id, "website")) admitted.Add(project.Id); }
            catch (InvalidOperationException ex) { Assert.Contains("quota", ex.Message, StringComparison.OrdinalIgnoreCase); }
        });
        Assert.Single(admitted);
        Assert.Equal(1, quota.Snapshot(workspace.Id).Usage.HostingSlots);
        quota.SetEnvironment(admitted.Single(), "website", false);
        Assert.Equal(0, quota.Snapshot(workspace.Id).Usage.HostingSlots);
        Assert.True(quota.ReserveProjectEnvironment(
            admitted.Single() == alpha.Id ? beta.Id : alpha.Id, "website"));
    }

    [Fact]
    public void Pipeline_core_hours_are_metered_and_block_new_claims_after_limit()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var projects = services.GetRequiredService<ProjectRegistry>();
        var project = projects.EnsureProjectForStorage(
            Path.Combine(_root, "compute"), "Compute", workspace.Id, _clock);
        var job = Path.Combine(project.StorageLocation, TaskStates.Ready, "compute-card");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id = "compute-card", title = "Compute", state = TaskStates.Ready,
            createdAt = _clock.GetUtcNow().UtcDateTime,
        }));
        var pipeline = services.GetRequiredService<PipelineExecutionLog>();
        pipeline.EnsureRun(job, PipelineCatalogue.Standard, project.DisplayName, "compute-card",
            _clock.GetUtcNow().UtcDateTime.AddHours(-2));
        pipeline.RecordStep(job, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.BuildTestGateStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            StartedAt = _clock.GetUtcNow().UtcDateTime.AddHours(-2),
            CompletedAt = _clock.GetUtcNow().UtcDateTime.AddHours(-1),
            GrantedCores = 2,
        });
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(null, 1, null));
        Assert.Equal(2, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        Assert.False(quota.Admit(project.Id).Allowed);
        projects.SetArchived(project.Id, true);
        Assert.Equal(2, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseEnvironment("Test")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = _root, ["PublicDemo:Enabled"] = "false" }))
            .ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            }));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
