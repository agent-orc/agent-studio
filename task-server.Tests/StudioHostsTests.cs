using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioHostsTests
{
    [Fact]
    public async Task Client_list_groups_runners_by_host_and_reflects_runtime_capacity_and_admission()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-hosts-a",
            new RegisterRunnerRequest("runner-a", "host-hosts-1", "instance-a", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var empty = await client.GetFromJsonAsync<List<StudioClientSummaryDto>>("/api/v1/studio/clients");
        var summary = Assert.Single(empty!, item => item.ClientId == "host-hosts-1");
        Assert.Equal(1, summary.RunnerCount);
        Assert.Equal("open", summary.AdmissionState);
        Assert.Null(summary.RetiredAt);

        var capacityPut = await client.PutAsJsonAsync(
            "/api/v1/studio/clients/host-hosts-1/runner-capacity",
            new UpdateRuntimeCapacitySettingsRequest(4, 80, "balanced", 0));
        capacityPut.EnsureSuccessStatusCode();
        var capacity = await store.GetRuntimeCapacitySettingsAsync("host-hosts-1", default);
        Assert.Equal(4, capacity!.MaxParallelism);

        var afterCapacity = await client.GetFromJsonAsync<List<StudioClientSummaryDto>>("/api/v1/studio/clients");
        Assert.Equal(4, Assert.Single(afterCapacity!).RuntimeCapacity!.MaxParallelism);
    }

    [Fact]
    public async Task Defaults_round_trip_and_reject_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-defaults", new RegisterRunnerRequest(
                "runner", "host-defaults-1", "instance-defaults", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var missing = await client.GetAsync("/api/v1/studio/clients/host-defaults-1/defaults");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var settings = new StudioHostDefaultsSettings("claude-sonnet", "claude", "high", 3);
        var create = await client.PutAsJsonAsync(
            "/api/v1/studio/clients/host-defaults-1/defaults",
            new UpdateStudioHostDefaultsRequest(settings, 0));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<StudioHostDefaultsDto>())!;
        Assert.Equal(1, created.Version);
        Assert.Equal("claude-sonnet", created.Defaults.Model);

        var read = await client.GetFromJsonAsync<StudioHostDefaultsDto>("/api/v1/studio/clients/host-defaults-1/defaults");
        Assert.Equal("high", read!.Defaults.ThinkingLevel);

        var stale = await client.PutAsJsonAsync(
            "/api/v1/studio/clients/host-defaults-1/defaults",
            new UpdateStudioHostDefaultsRequest(settings with { MaxParallelism = 5 }, 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var update = await client.PutAsJsonAsync(
            "/api/v1/studio/clients/host-defaults-1/defaults",
            new UpdateStudioHostDefaultsRequest(settings with { MaxParallelism = 5 }, 1));
        update.EnsureSuccessStatusCode();
        Assert.Equal(2, (await update.Content.ReadFromJsonAsync<StudioHostDefaultsDto>())!.Version);
    }

    [Fact]
    public async Task Drain_and_revive_round_trip_through_admission_state_and_are_idempotent()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-drain", new RegisterRunnerRequest(
                "runner", "host-drain-1", "instance-drain", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var drain = await client.PostAsJsonAsync(
            "/api/v1/studio/clients/host-drain-1/drain", new StudioHostDrainRequest("maintenance window"));
        drain.EnsureSuccessStatusCode();
        var afterDrain = (await drain.Content.ReadFromJsonAsync<RemoteHostAdmissionDto>())!;
        Assert.Equal("operator-draining", afterDrain.AdmissionState);

        // Idempotent: draining an already-draining host is a no-op, not an error.
        var drainAgain = await client.PostAsJsonAsync(
            "/api/v1/studio/clients/host-drain-1/drain", new StudioHostDrainRequest("maintenance window"));
        drainAgain.EnsureSuccessStatusCode();

        var clientsWhileDraining = await client.GetFromJsonAsync<List<StudioClientSummaryDto>>("/api/v1/studio/clients");
        Assert.Equal("operator-draining", Assert.Single(clientsWhileDraining!).AdmissionState);

        var revive = await client.PostAsJsonAsync("/api/v1/studio/clients/host-drain-1/revive", new StudioHostReviveRequest());
        revive.EnsureSuccessStatusCode();
        Assert.Equal("open", (await revive.Content.ReadFromJsonAsync<RemoteHostAdmissionDto>())!.AdmissionState);

        // Idempotent: reviving an already-open host is a no-op, not an error.
        var reviveAgain = await client.PostAsJsonAsync("/api/v1/studio/clients/host-drain-1/revive", new StudioHostReviveRequest());
        reviveAgain.EnsureSuccessStatusCode();
        Assert.Equal("open", (await reviveAgain.Content.ReadFromJsonAsync<RemoteHostAdmissionDto>())!.AdmissionState);

        var clientsAfterRevive = await client.GetFromJsonAsync<List<StudioClientSummaryDto>>("/api/v1/studio/clients");
        Assert.Equal("open", Assert.Single(clientsAfterRevive!).AdmissionState);
    }

    [Fact]
    public async Task Retire_and_permanent_delete_are_idempotent_and_observable_via_the_client_list()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-retire", new RegisterRunnerRequest(
                "runner", "host-retire-1", "instance-retire", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var retire = await client.PostAsJsonAsync(
            "/api/v1/studio/clients/host-retire-1/retire", new StudioHostRetireRequest("decommissioned"));
        retire.EnsureSuccessStatusCode();
        var retired = (await retire.Content.ReadFromJsonAsync<StudioHostLifecycleDto>())!;
        Assert.NotNull(retired.RetiredAt);
        Assert.Equal("decommissioned", retired.RetiredReason);

        var retireAgain = await client.PostAsJsonAsync(
            "/api/v1/studio/clients/host-retire-1/retire", new StudioHostRetireRequest("different reason"));
        retireAgain.EnsureSuccessStatusCode();
        var retiredAgain = (await retireAgain.Content.ReadFromJsonAsync<StudioHostLifecycleDto>())!;
        Assert.Equal(retired.RetiredAt, retiredAgain.RetiredAt);
        Assert.Equal("decommissioned", retiredAgain.RetiredReason);

        var clientsAfterRetire = await client.GetFromJsonAsync<List<StudioClientSummaryDto>>("/api/v1/studio/clients");
        Assert.NotNull(Assert.Single(clientsAfterRetire!).RetiredAt);

        var permanent = await client.DeleteAsync("/api/v1/studio/clients/host-retire-1/permanent");
        permanent.EnsureSuccessStatusCode();
        var deleted = (await permanent.Content.ReadFromJsonAsync<StudioHostLifecycleDto>())!;
        Assert.NotNull(deleted.PermanentlyDeletedAt);

        var permanentAgain = await client.DeleteAsync("/api/v1/studio/clients/host-retire-1/permanent");
        permanentAgain.EnsureSuccessStatusCode();
        var deletedAgain = (await permanentAgain.Content.ReadFromJsonAsync<StudioHostLifecycleDto>())!;
        Assert.Equal(deleted.PermanentlyDeletedAt, deletedAgain.PermanentlyDeletedAt);

        var clientsAfterDelete = await client.GetFromJsonAsync<List<StudioClientSummaryDto>>("/api/v1/studio/clients");
        Assert.NotNull(Assert.Single(clientsAfterDelete!).PermanentlyDeletedAt);

        // Soft tombstone only: the underlying runner row is preserved.
        var runners = await store.ListRunnerCapabilitySnapshotsAsync(default);
        Assert.Contains(runners, item => item.HostId == "host-retire-1");
    }

    [Fact]
    public async Task Preflight_invalidate_bumps_a_per_host_generation_counter()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-preflight", new RegisterRunnerRequest(
                "runner", "host-preflight-1", "instance-preflight", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var first = await client.PostAsync(
            "/api/v1/studio/clients/host-preflight-1/runner-project-preflights/invalidate", null);
        first.EnsureSuccessStatusCode();
        var firstResponse = (await first.Content.ReadFromJsonAsync<StudioPreflightInvalidateResponse>())!;
        Assert.Equal(1, firstResponse.Generation);

        var second = await client.PostAsync(
            "/api/v1/studio/clients/host-preflight-1/runner-project-preflights/invalidate", null);
        second.EnsureSuccessStatusCode();
        var secondResponse = (await second.Content.ReadFromJsonAsync<StudioPreflightInvalidateResponse>())!;
        Assert.Equal(2, secondResponse.Generation);
    }

    [Fact]
    public async Task Telemetry_round_trips_whatever_was_last_reported()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-telemetry", new RegisterRunnerRequest(
                "runner", "host-telemetry-1", "instance-telemetry", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var missing = await client.GetAsync("/api/v1/studio/clients/host-telemetry-1/telemetry");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var telemetry = new HostTelemetrySnapshotDto(
            DateTime.UtcNow, 42.5, 0.1, 0.2, 0.3, 1_000_000L, 2_000_000L, null, null, null, null, 4, 1);
        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest(
                "runner-telemetry", "instance-telemetry", CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow, 300, 1,
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "execution")],
                telemetry),
            "test", default);

        var read = await client.GetFromJsonAsync<HostTelemetrySnapshotDto>("/api/v1/studio/clients/host-telemetry-1/telemetry");
        Assert.Equal(42.5, read!.CpuPercent);
        Assert.Equal(4, read.CpuCores);
    }

    [Fact]
    public async Task Management_commands_dispatch_known_commands_and_reject_an_unknown_one()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await store.RegisterRunnerAsync(
            "runner-commands", new RegisterRunnerRequest(
                "runner", "host-commands-1", "instance-commands", "1.0.0", TaskServerProtocol.Current,
                [CapabilityProtocol.CodingExecutor]),
            "test", default);

        var unknown = await client.PostAsJsonAsync(
            "/api/v1/management/commands", new StudioManagementCommandRequest("hosts.teleport"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("unknown-management-command", (await unknown.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var argumentsJson = System.Text.Json.JsonSerializer.Serialize(
            new StudioManagementCommandArguments("host-commands-1", "via-command"));
        var drain = await client.PostAsJsonAsync(
            "/api/v1/management/commands", new StudioManagementCommandRequest(StudioManagementCommands.HostsDrain, argumentsJson));
        drain.EnsureSuccessStatusCode();
        Assert.Equal("ok", (await drain.Content.ReadFromJsonAsync<StudioManagementCommandResponse>())!.Status);

        var admission = await store.RequestOperatorHostDrainAsync(
            "host-commands-1", new OperatorHostDrainRequest("probe"), "test", default);
        Assert.Equal("operator-draining", admission.AdmissionState);

        var retire = await client.PostAsJsonAsync(
            "/api/v1/management/commands", new StudioManagementCommandRequest(StudioManagementCommands.HostsRetire, argumentsJson));
        retire.EnsureSuccessStatusCode();

        var lifecycle = await store.GetStudioHostLifecycleAsync("host-commands-1", default);
        Assert.NotNull(lifecycle!.RetiredAt);
    }

    [Fact]
    public async Task Provider_auth_event_is_recorded_durably()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            new StudioProviderAuthEventRequest("host-provider-1", "claude-code", "authenticated", "{\"note\":\"ok\"}"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var recorded = (await response.Content.ReadFromJsonAsync<StudioProviderAuthEventDto>())!;
        Assert.Equal("host-provider-1", recorded.HostId);
        Assert.Equal("claude-code", recorded.Provider);
        Assert.Equal("authenticated", recorded.Status);
        Assert.NotEmpty(recorded.Id);
    }
}
