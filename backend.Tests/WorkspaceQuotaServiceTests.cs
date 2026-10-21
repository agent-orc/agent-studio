using AgentStudio.Quota;
using AgentStudio.Registry;
using AgentStudio.Bus;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
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
        using var factory = Factory();
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

    public enum LedgerRow { NormalizedCodexReceipt, LegacyOpenAiBusRow, ClaudeBusRow }

    [Theory]
    [InlineData(LedgerRow.NormalizedCodexReceipt)]
    [InlineData(LedgerRow.LegacyOpenAiBusRow)]
    [InlineData(LedgerRow.ClaudeBusRow)]
    public async Task Every_ledger_row_counts_uncached_input_cached_input_and_output_once(LedgerRow row)
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "tokens"), "Tokens", workspace.Id, _clock);
        Directory.CreateDirectory(project.StorageLocation);
        switch (row)
        {
            case LedgerRow.NormalizedCodexReceipt:
                // A Codex receipt is stored normalized: input excludes the 70 cached tokens.
                SeedTask(project.StorageLocation, "codex-card", new
                {
                    calls = 1, inputTokens = 30, outputTokens = 20, cacheReadTokens = 70, totalTokens = 120,
                    entries = new[]
                    {
                        new
                        {
                            ts = _clock.GetUtcNow().UtcDateTime, model = "gpt-5.3-codex", inputTokens = 30,
                            outputTokens = 20, cacheReadTokens = 70, inputIncludesCached = true,
                        },
                    },
                });
                break;
            case LedgerRow.LegacyOpenAiBusRow:
                // Written before the flag existed: input 100 includes the 70 cached tokens.
                await Bus(services, project.DisplayName, "01HX0000000000000000000041",
                    new AgentMessageTokens(Input: 100, Output: 20, CacheRead: 70, Model: "gpt-5.6-sol"));
                break;
            case LedgerRow.ClaudeBusRow:
                await Bus(services, project.DisplayName, "01HX0000000000000000000042",
                    new AgentMessageTokens(Input: 30, Output: 20, CacheRead: 70, Model: "claude-opus-4-1"));
                break;
        }
        services.GetRequiredService<TaskScannerService>().InvalidateCache();
        var quota = services.GetRequiredService<WorkspaceQuotaService>();

        Assert.Equal(120, quota.Snapshot(workspace.Id).Usage.ModelTokens);
    }

    [Fact]
    public async Task Token_and_compute_consumers_share_one_card_id()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "cards"), "Cards", workspace.Id, _clock);
        var job = SeedTask(project.StorageLocation, "card-folder-id", key: "CRD-7");
        RecordStep(services, job, project.DisplayName, "card-folder-id",
            _clock.GetUtcNow().UtcDateTime.AddHours(-1), _clock.GetUtcNow().UtcDateTime, cores: 1);
        await Bus(services, project.DisplayName, "01HX0000000000000000000043",
            new AgentMessageTokens(Input: 10, Output: 5, Model: "claude-opus-4-1"), jobId: "card-folder-id");
        services.GetRequiredService<TaskScannerService>().InvalidateCache();

        var consumer = Assert.Single(services.GetRequiredService<WorkspaceQuotaService>()
            .Snapshot(workspace.Id).BiggestConsumers);

        Assert.Equal("CRD-7", consumer.CardId);
        Assert.Equal(15, consumer.ModelTokens);
        Assert.Equal(1m, consumer.ComputeCoreHours);
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
        await Bus(services, project.DisplayName, "01HX0000000000000000000031",
            new AgentMessageTokens(Input: 90, Output: 20, Model: "gpt-5.6-sol"));
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(100, null, null));
        Assert.Equal(110, quota.Snapshot(workspace.Id).Usage.ModelTokens);
        projects.SetArchived(project.Id, true);
        Assert.Equal(110, quota.Snapshot(workspace.Id).Usage.ModelTokens);
        Assert.False(quota.Admit(project.Id).Allowed);
    }

    [Fact]
    public async Task Partial_ledger_refuses_admission_and_names_the_project_and_source()
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
        await Bus(services, project.DisplayName, "01HX0000000000000000000032",
            new AgentMessageTokens(Input: 10, Output: 5, Model: "gpt-5.6-sol"));
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(100, null, null));
        var snapshot = quota.Snapshot(workspace.Id);
        Assert.Equal(15, snapshot.Usage.ModelTokens);
        Assert.False(snapshot.MeterComplete);
        var admission = quota.Admit(project.Id);
        Assert.False(admission.Allowed);
        Assert.True(admission.MeterUnavailable);
        Assert.Contains("Project Partial", admission.Message, StringComparison.Ordinal);
        Assert.Contains("receipt", admission.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_blank_limit_fields_keeps_admission_a_strict_no_op_with_an_unreadable_ledger()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "blank"), "Blank", workspace.Id, _clock);
        var job = Path.Combine(project.StorageLocation, TaskStates.Ready, "blank-card");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), "{invalid-json");
        await Bus(services, project.DisplayName, "01HX0000000000000000000033",
            new AgentMessageTokens(Input: 10, Output: 5, Model: "gpt-5.6-sol"));
        var quota = services.GetRequiredService<WorkspaceQuotaService>();

        // The settings form sends every blank field as null.
        quota.SetLimits(workspace.Id, null, new QuotaLimits(null, null, null));
        quota.SetLimits(workspace.Id, project.Id, new QuotaLimits(null, null, null));

        Assert.False(quota.HasLimits());
        foreach (var admission in new[] { quota.Admit(project.Id), quota.Admit(project.Id, hosting: true) })
        {
            Assert.True(admission.Allowed);
            Assert.Null(admission.Message);
            Assert.False(admission.MeterUnavailable);
        }
        Assert.False(ActivatorUtilities.CreateInstance<WorkspaceQuotaService>(services).HasLimits());
    }

    [Fact]
    public void Unreadable_state_keeps_the_last_good_limits_and_reports_the_meter()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "state"), "State", workspace.Id, _clock);
        Directory.CreateDirectory(project.StorageLocation);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(0, null, null));
        File.WriteAllText(Path.Combine(_root, ".metadata", "workspace-quotas.json"), "{broken");

        var kept = quota.Admit(project.Id);
        var fresh = ActivatorUtilities.CreateInstance<WorkspaceQuotaService>(services).Admit(project.Id);

        Assert.False(kept.Allowed);
        Assert.True(fresh.Allowed);
        Assert.True(fresh.MeterUnavailable);
        Assert.Contains("could not be read", fresh.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unregistered_project_is_admitted_with_meter_unavailable()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(0, 0, 0));

        var admission = quota.Admit("task-server-only-name");

        Assert.True(admission.Allowed);
        Assert.True(admission.MeterUnavailable);
        Assert.Contains("task-server-only-name", admission.Message, StringComparison.Ordinal);
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
            catch (InvalidOperationException ex) { Assert.Contains("hosting slots", ex.Message, StringComparison.Ordinal); }
        });
        Assert.Single(admitted);
        Assert.Equal(1, quota.Snapshot(workspace.Id).Usage.HostingSlots);
        Assert.Equal([projects.FindByIdOrDisplayName(admitted.Single())!.DisplayName], quota.RunningWebsiteProjects());
        Assert.Equal(_clock.GetUtcNow(), quota.ProjectEnvironmentReservedAt(admitted.Single(), "website"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        quota.MarkProjectEnvironmentDispatched(admitted.Single(), "website", _clock.GetUtcNow());
        Assert.Equal(_clock.GetUtcNow(), quota.ProjectEnvironmentReservedAt(admitted.Single(), "website"));
        quota.SetEnvironment(admitted.Single(), "website", false);
        Assert.Equal(0, quota.Snapshot(workspace.Id).Usage.HostingSlots);
        Assert.Empty(quota.RunningWebsiteProjects());
        Assert.True(quota.ReserveProjectEnvironment(
            admitted.Single() == alpha.Id ? beta.Id : alpha.Id, "website"));
    }

    [Fact]
    public void Full_hosting_slots_stop_deploys_but_not_coding()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "hosting"), "Hosting", workspace.Id, _clock);
        Directory.CreateDirectory(project.StorageLocation);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(1000, 10, 1));
        Assert.True(quota.ReserveProjectEnvironment(project.Id, "website"));

        var snapshot = quota.Snapshot(workspace.Id);
        var board = quota.BoardAdmission(workspace.Id);

        Assert.Equal("hosting-full", snapshot.Status);
        Assert.Equal("All 1 hosting slots are in use; new deploys wait until an environment stops.", snapshot.Message);
        Assert.True(snapshot.CodingAllowed);
        Assert.False(snapshot.HostingAllowed);
        Assert.True(quota.Admit(project.Id).Allowed);
        Assert.False(quota.Admit(project.Id, hosting: true).Allowed);
        Assert.Equal("hosting-full", board.Workspace.Status);
        Assert.True(board.Workspace.CodingAllowed);
    }

    [Fact]
    public void Board_admission_answers_every_scope_in_one_read_and_meters_nothing_without_limits()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var projects = services.GetRequiredService<ProjectRegistry>();
        var alpha = projects.EnsureProjectForStorage(Path.Combine(_root, "alpha"), "Alpha", workspace.Id, _clock);
        var beta = projects.EnsureProjectForStorage(Path.Combine(_root, "beta"), "Beta", workspace.Id, _clock);
        // An unreadable ledger would block a limited scope; without limits nothing is metered.
        var broken = Path.Combine(alpha.StorageLocation, TaskStates.Ready, "broken-card");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "task.json"), "{invalid-json");
        Directory.CreateDirectory(beta.StorageLocation);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();

        var open = quota.BoardAdmission(workspace.Id);
        Assert.False(open.Workspace.Limited);
        Assert.All(open.Projects, scope => Assert.True(scope.CodingAllowed && scope.Message is null && !scope.Limited));
        Assert.True(quota.Snapshot(workspace.Id).CodingAllowed);
        Assert.Null(quota.Snapshot(workspace.Id).Message);

        File.Delete(Path.Combine(broken, "task.json"));
        quota.SetLimits(workspace.Id, beta.Id, new QuotaLimits(0, null, null));
        var limited = quota.BoardAdmission(workspace.Id);

        Assert.True(limited.Workspace.CodingAllowed);
        var betaScope = Assert.Single(limited.Projects, scope => scope.ProjectId == beta.Id);
        Assert.False(betaScope.CodingAllowed);
        Assert.Equal("Beta", betaScope.Project);
        Assert.Contains("Weekly quota reached", betaScope.Message, StringComparison.Ordinal);
        Assert.True(Assert.Single(limited.Projects, scope => scope.ProjectId == alpha.Id).CodingAllowed);
    }

    [Fact]
    public async Task Token_usage_resets_at_monday_midnight_utc()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 18, 23, 59, 0, TimeSpan.Zero));
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "week"), "Week", workspace.Id, _clock);
        Directory.CreateDirectory(project.StorageLocation);
        await Bus(services, project.DisplayName, "01HX0000000000000000000044",
            new AgentMessageTokens(Input: 90, Output: 20, Model: "claude-opus-4-1"));
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(100, null, null));
        Assert.Equal(110, quota.Snapshot(workspace.Id).Usage.ModelTokens);
        Assert.False(quota.Admit(project.Id).Allowed);

        _clock.Advance(TimeSpan.FromMinutes(1));

        var monday = quota.Snapshot(workspace.Id);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, monday.WeekStartUtc);
        Assert.Equal(0, monday.Usage.ModelTokens);
        Assert.True(quota.Admit(project.Id).Allowed);
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
        var job = SeedTask(project.StorageLocation, "compute-card");
        RecordStep(services, job, project.DisplayName, "compute-card",
            _clock.GetUtcNow().UtcDateTime.AddHours(-2), _clock.GetUtcNow().UtcDateTime.AddHours(-1), cores: 2);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(null, 1, null));
        Assert.Equal(2, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        Assert.False(quota.Admit(project.Id).Allowed);
        projects.SetArchived(project.Id, true);
        Assert.Equal(2, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
    }

    [Fact]
    public async Task Active_run_keeps_running_and_metering_when_the_quota_is_reached()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "active"), "Active", workspace.Id, _clock);
        var job = SeedTask(project.StorageLocation, "active-card", state: TaskStates.Progress);
        RecordStep(services, job, project.DisplayName, "active-card",
            _clock.GetUtcNow().UtcDateTime.AddHours(-2), completedAt: null, cores: 2);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(null, 3, null));

        Assert.Equal(4, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        Assert.False(quota.Admit(project.Id).Allowed);
        _clock.Advance(TimeSpan.FromHours(1));

        // Stale-while-revalidate: the read after the TTL gets the previous value and starts one recompute.
        Assert.Equal(4, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        await quota.PendingUsageRefresh(workspace.Id);
        // Refusal is admission only: the open step is neither completed nor moved, and it keeps metering.
        Assert.Equal(6, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        Assert.True(Directory.Exists(job));
        var step = Assert.Single(services.GetRequiredService<PipelineExecutionLog>().Read(job)!.Steps,
            item => item.StartedAt is not null);
        Assert.Null(step.CompletedAt);
    }

    [Fact]
    public async Task Orphaned_running_step_of_a_previous_attempt_stops_metering_when_the_next_attempt_starts()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "orphans"), "Orphans", workspace.Id, _clock);
        var job = SeedTask(project.StorageLocation, "orphan-card", state: TaskStates.Progress);
        var now = _clock.GetUtcNow().UtcDateTime;
        RecordStep(services, job, project.DisplayName, "orphan-card", now.AddHours(-3), completedAt: null, cores: 1);
        // The next pickup archives the incomplete attempt together with its Running step.
        var pipeline = services.GetRequiredService<PipelineExecutionLog>();
        pipeline.EnsureAgentRunStart(job, PipelineCatalogue.Standard, project.DisplayName, "orphan-card", now.AddHours(-2));
        services.GetRequiredService<TaskScannerService>().InvalidateCache();
        var quota = services.GetRequiredService<WorkspaceQuotaService>();

        Assert.Equal(PipelineStepStatus.Running, Assert.Single(pipeline.Read(job)!.PreviousAttempts).Steps
            .Single(step => step.StartedAt is not null).Status);
        Assert.Equal(1, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        _clock.Advance(TimeSpan.FromHours(5));
        quota.Snapshot(workspace.Id);
        await quota.PendingUsageRefresh(workspace.Id);

        Assert.Equal(1, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
    }

    [Fact]
    public async Task Open_step_on_a_human_review_card_ends_at_the_last_record_write()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "reviewed"), "Reviewed", workspace.Id, _clock);
        var job = SeedTask(project.StorageLocation, "reviewed-card", state: TaskStates.HumanReview);
        var now = _clock.GetUtcNow().UtcDateTime;
        RecordStep(services, job, project.DisplayName, "reviewed-card", now.AddHours(-2), completedAt: null, cores: 2);
        File.SetLastWriteTimeUtc(Path.Combine(job, PipelineExecutionLog.FileName), now.AddHours(-1));
        var quota = services.GetRequiredService<WorkspaceQuotaService>();

        Assert.Equal(2, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
        _clock.Advance(TimeSpan.FromHours(3));
        quota.Snapshot(workspace.Id);
        await quota.PendingUsageRefresh(workspace.Id);

        Assert.Equal(2, quota.Snapshot(workspace.Id).Usage.ComputeCoreHours);
    }

    [Fact]
    public void Without_any_limit_admission_answers_without_message_or_meter_warning()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var quota = factory.Services.GetRequiredService<WorkspaceQuotaService>();

        var admission = quota.Admit("task-server-only-name", hosting: true);

        Assert.False(quota.HasLimits());
        Assert.True(admission.Allowed);
        Assert.Null(admission.Message);
        Assert.False(admission.MeterUnavailable);
    }

    [Fact]
    public void Override_audits_older_than_the_retention_are_trimmed()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.Override(workspace.Id, null, "owner-1", "First");
        _clock.Advance(WorkspaceQuotaService.AuditRetention + TimeSpan.FromDays(1));

        quota.Override(workspace.Id, null, "owner-2", "Second");

        Assert.Equal("Second", Assert.Single(quota.Audit(workspace.Id, null)).Reason);
    }

    [Fact]
    public void Monolith_gate_claim_admits_an_unresolved_subject_with_a_meter_warning()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, new QuotaLimits(0, null, null));
        var store = new MonolithGateStore((string?)null, _clock, new GateHosts(), quota,
            services.GetRequiredService<TaskScannerService>());
        store.Create(GateSubject("NOPE-1"));

        var claimed = store.Claim(new GateClaimRequest("gate-a", "instance-a"));

        Assert.Equal("claimed", claimed.Status);
        Assert.Contains("could not be resolved", claimed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Monolith_gate_claim_is_refused_at_the_quota_and_claims_again_after_the_limit_is_lifted()
    {
        Directory.CreateDirectory(_root);
        using var factory = Factory();
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var project = services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(_root, "gates"), "Gates", workspace.Id, _clock);
        SeedTask(project.StorageLocation, "gate-card", key: "GAT-1");
        var scanner = services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        var store = new MonolithGateStore((string?)null, _clock, new GateHosts(), quota, scanner);
        store.Create(GateSubject("GAT-1"));
        quota.SetLimits(workspace.Id, project.Id, new QuotaLimits(0, null, null));

        var refused = store.Claim(new GateClaimRequest("gate-a", "instance-a"));
        quota.SetLimits(workspace.Id, project.Id, null);
        var claimed = store.Claim(new GateClaimRequest("gate-a", "instance-a"));

        Assert.Equal("empty", refused.Status);
        Assert.Contains("Weekly quota reached", refused.Message, StringComparison.Ordinal);
        Assert.Equal("claimed", claimed.Status);
        Assert.Equal("GAT-1", claimed.Subject!.TaskId);
    }

    private CreateGateSubjectRequest GateSubject(string taskId)
    {
        var commands = new[] { new GateCommand("test-1", "bash", ["-lc", "dotnet test"], 600, "") };
        var plan = new GatePlan("post-build-test-gate", 1, commands, "", 600,
            GateRules.RequiredCapabilities(commands), 32768, "always");
        return new CreateGateSubjectRequest(taskId, "run-1", "repo-1", "https://git.test/repo.git",
            new string('1', 40), "refs/agent-studio/results/run-1", null, null, GateRules.PlanHash(plan),
            new string('b', 64), "1.0", new string('c', 64), plan, _clock.GetUtcNow().UtcDateTime.AddHours(1));
    }

    private sealed class GateHosts : IGateExecutorDirectory
    {
        public bool TryGetGateExecutor(string runnerId, string instanceId,
            out string hostId, out IReadOnlySet<string> capabilities)
        {
            hostId = runnerId.Replace("gate-", "host-", StringComparison.Ordinal);
            capabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                GateCapabilities.Executor, GateCapabilities.GitMaterialization,
                CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess, CapabilityProtocol.DotNet,
            };
            return true;
        }
    }

    private string SeedTask(string projectRoot, string id, object? tokenSummary = null, string? key = null,
        string state = TaskStates.Ready)
    {
        var job = Path.Combine(projectRoot, state, id);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id, key, title = id, state, createdAt = _clock.GetUtcNow().UtcDateTime, tokenSummary,
        }));
        return job;
    }

    private static void RecordStep(IServiceProvider services, string job, string project, string id,
        DateTime startedAt, DateTime? completedAt, int cores)
    {
        var pipeline = services.GetRequiredService<PipelineExecutionLog>();
        pipeline.EnsureRun(job, PipelineCatalogue.Standard, project, id, startedAt);
        pipeline.RecordStep(job, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.BuildTestGateStepId,
            Kind = StepKind.Tool,
            Status = completedAt is null ? PipelineStepStatus.Running : PipelineStepStatus.Passed,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            GrantedCores = cores,
        });
        services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }

    private Task Bus(IServiceProvider services, string project, string id, AgentMessageTokens tokens,
        string? jobId = null)
        => services.GetRequiredService<AgentMessageBusStore>().AppendAsync(_root, new AgentMessage
        {
            Id = id,
            CreatedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-30),
            ParticipantId = "agent:codex",
            Role = "evidence",
            Kind = "token-usage",
            Project = project,
            JobId = jobId,
            Summary = "tokens",
            Tokens = tokens,
        });

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
