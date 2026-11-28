using Microsoft.Extensions.Time.Testing;
using AgentStudio.Operator;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteDispatchRejectionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "remote-dispatch-rejection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Record_PersistsDiagnosticAndKeepsRepeatedPollIdempotent()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), "{}");
        var store = new RemoteDispatchRejectionStore(
            NullLogger<RemoteDispatchRejectionStore>.Instance);
        var task = new TaskInfo
        {
            Id = "task-1",
            Key = "AGT-1",
            ProjectName = "demo",
            FolderPath = _folder,
            EnteredLaneAt = new DateTime(2026, 8, 8, 9, 0, 0, DateTimeKind.Utc),
        };

        var first = store.Record(
            task,
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc));
        var repeated = store.Record(
            task,
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            new DateTime(2026, 8, 8, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(first.RejectedAtUtc, repeated.RejectedAtUtc);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        var persisted = json.RootElement.GetProperty(RemoteDispatchRejectionStore.FieldName);
        Assert.Equal("runner-01", persisted.GetProperty("runnerId").GetString());
        Assert.Equal("project has no repositoryUrl", persisted.GetProperty("reason").GetString());
    }

    [Fact]
    public void Record_RefreshesTheSameReasonForANewReadyLaneGeneration()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), "{}");
        var store = new RemoteDispatchRejectionStore(
            NullLogger<RemoteDispatchRejectionStore>.Instance);
        var firstReadyAt = new DateTime(2026, 8, 8, 9, 0, 0, DateTimeKind.Utc);
        var firstTask = new TaskInfo
        {
            Id = "task-1",
            Key = "AGT-1",
            ProjectName = "demo",
            FolderPath = _folder,
            EnteredLaneAt = firstReadyAt,
        };
        store.Record(
            firstTask,
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            firstReadyAt.AddMinutes(1));

        var secondReadyAt = firstReadyAt.AddHours(2);
        TaskJsonFile.UpdateFieldOrThrow(_folder, "enteredLaneAt", secondReadyAt);
        var refreshed = store.Record(
            firstTask with { EnteredLaneAt = secondReadyAt },
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            secondReadyAt.AddMinutes(1));

        Assert.Equal(secondReadyAt.AddMinutes(1), refreshed.RejectedAtUtc);
    }

    [Fact]
    public void Advertisement_recovers_card_wait_and_single_host_feed_without_claim()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), "{}");
        var store = new RemoteDispatchRejectionStore(NullLogger<RemoteDispatchRejectionStore>.Instance);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var now = clock.GetUtcNow().UtcDateTime;
        var registry = new V1ReviewExecutorRegistry(clock);
        const string runner = "runner-a", instance = "instance-a", model = "gpt-6.1-sol";
        var key = CapabilityProtocol.ModelAcceptance("codex", model);
        registry.Register(runner, new RegisterRunnerRequest(runner, "host-a", instance, "test",
            TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]));
        var task = new TaskInfo { Id = "A-1", Key = "A-1", FolderPath = _folder, State = TaskStates.Ready,
            EnteredLaneAt = now.AddMinutes(-5), CliType = "codex", Model = model };
        var rejection = store.Record(task, runner, "host-a", "host-model-unavailable",
            ModelAcceptanceAdmissionPolicy.Refusal("host-a", key, ModelAcceptanceAdmission.Unavailable), now, key);
        task = task with { RemoteDispatchRejection = rejection,
            PickupHold = new() { Mechanism = PickupHoldMechanisms.HostCapabilityMissing } };
        Assert.Contains(now.ToString("yyyy-MM-dd HH:mm:ss"), CardWaitingReason.Select(task));
        var routes = new[] { new ConfiguredModelRouteDto("codex", model) };
        var down = Advertise("unavailable", 1);
        var item = Assert.Single(OperatorHostModelFactSource.Project([down], routes, [task], now));
        Assert.Equal(["A-1"], item.Cards);
        Assert.Contains("Ready cards held: 1", item.Reason);
        store.RefreshModelWaits([task], runner, instance, registry);
        Assert.Equal(rejection, RemoteDispatchRejectionStore.Read(_folder));

        var up = Advertise("ready", 2);
        store.RefreshModelWaits([task], runner, instance, registry);
        Assert.Null(RemoteDispatchRejectionStore.Read(_folder));
        Assert.Empty(OperatorHostModelFactSource.Project([up], routes, [task], now));

        RunnerCapabilitySnapshotDto Advertise(string status, int generation)
            => registry.AdvertiseCapabilities(runner, new CapabilityAdvertisementRequest(runner, instance,
                CapabilityProtocol.CurrentSchemaVersion, now, 180, generation,
                [new(CapabilityProtocol.CodingExecutor, "executor", "ready"),
                 new(CapabilityProtocol.CliExecution("codex"), "cli", "ready"),
                 new(CapabilityProtocol.ProviderAuthentication("codex"), "provider-auth", "ready"),
                 new(key, "model-acceptance", status)]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
