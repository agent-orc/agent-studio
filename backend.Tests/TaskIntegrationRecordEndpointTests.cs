using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskIntegrationRecordEndpointTests : IDisposable
{
    private const string TaskKey = "AGT-9998";
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "agt-integration-record-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public TaskIntegrationRecordEndpointTests()
    {
        _watchPath = Path.Combine(_workspace, "tasks");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "TaskIntegrationRecordEndpointTests cleanup"); }
    }

    [Theory]
    [InlineData(TaskStates.HumanReview, true)]
    [InlineData(TaskStates.Escalated, true)]
    [InlineData(TaskStates.Completed, true)]
    [InlineData(TaskStates.Archive, true)]
    [InlineData(TaskStates.Ready, false)]
    [InlineData(TaskStates.Progress, false)]
    [InlineData(TaskStates.AutoReview, false)]
    public void AppendPolicy_OnlyAllowsAcceptedAndTerminalLanes(string state, bool allowed)
    {
        var validation = TaskIntegrationRecordAppendPolicy.Validate(state, Request());

        Assert.Equal(allowed, validation.Allowed);
        Assert.Equal(!allowed, validation.InFlight);
    }

    [Fact]
    public void AppendPolicy_CuratedMappingRequiresExactShaPairAndEpochInAutoReview()
    {
        var mapping = Request() with
        {
            Classification = IntegrationRecordClasses.CuratedMapping,
            SourceSha = new string('a', 40),
            IntegrationSha = new string('b', 40),
            DeliveryEpoch = "run-current",
        };
        Assert.True(TaskIntegrationRecordAppendPolicy.Validate(TaskStates.AutoReview, mapping).Allowed);
        Assert.False(TaskIntegrationRecordAppendPolicy.Validate(TaskStates.AutoReview,
            mapping with { IntegrationSha = "abc1234" }).Allowed);
        Assert.False(TaskIntegrationRecordAppendPolicy.Validate(TaskStates.AutoReview,
            mapping with { DeliveryEpoch = null }).Allowed);
        Assert.False(TaskIntegrationRecordAppendPolicy.Validate(TaskStates.AutoReview,
            mapping with { Classification = IntegrationRecordClasses.IntegratedVerified }).Allowed);
    }

    [Fact]
    public async Task AppendCuratedMapping_PersistsSourceAndIntegrationShaInAutoReview()
    {
        WriteTask(TaskStates.AutoReview);
        await using var factory = BuildFactory(guarded: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var path = $"/api/tasks/{TaskKey}/integration-records?watchPath={Uri.EscapeDataString(_watchPath)}";
        var request = Request() with
        {
            Classification = IntegrationRecordClasses.CuratedMapping,
            SourceSha = new string('a', 40), IntegrationSha = new string('b', 40),
            DeliveryEpoch = "run-current",
        };
        using var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        using var detailResponse = await client.GetAsync(
            $"/api/tasks/{TaskKey}?watchPath={Uri.EscapeDataString(_watchPath)}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var record = Assert.Single(detail.RootElement.GetProperty("info")
            .GetProperty("integrationRecords").EnumerateArray());
        Assert.Equal(request.SourceSha, record.GetProperty("sourceSha").GetString());
        Assert.Equal(request.IntegrationSha, record.GetProperty("integrationSha").GetString());
        Assert.Equal("run-current", record.GetProperty("deliveryEpoch").GetString());
    }

    [Fact]
    public async Task AppendIntegrationRecord_IsAppendOnlyIdempotent_AndDoesNotMoveTask()
    {
        WriteTask(TaskStates.HumanReview);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var path = $"/api/tasks/{TaskKey}/integration-records?watchPath={Uri.EscapeDataString(_watchPath)}";

        using var first = await client.PostAsJsonAsync(path, Request());
        first.EnsureSuccessStatusCode();
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstBody.RootElement.GetProperty("appended").GetBoolean());

        using var repeated = await client.PostAsJsonAsync(path, Request());
        repeated.EnsureSuccessStatusCode();
        using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.False(repeatedBody.RootElement.GetProperty("appended").GetBoolean());

        using var detailResponse = await client.GetAsync(
            $"/api/tasks/{TaskKey}?watchPath={Uri.EscapeDataString(_watchPath)}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var info = detail.RootElement.GetProperty("info");
        Assert.Equal(TaskStates.HumanReview, info.GetProperty("state").GetString());
        var record = Assert.Single(info.GetProperty("integrationRecords").EnumerateArray());
        Assert.Equal("operator-gpt-verification-2026-08-11", record.GetProperty("id").GetString());
        Assert.Equal(IntegrationRecordClasses.IntegratedVerified, record.GetProperty("classification").GetString());
        Assert.Equal("main", record.GetProperty("integrationBranch").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef01234567",
            Assert.Single(record.GetProperty("commitShas").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task AppendIntegrationRecord_RejectsInFlightAndUnknownClassificationWithoutMutation()
    {
        WriteTask(TaskStates.Ready);
        const string invalidTaskKey = "AGT-9997";
        WriteTask(TaskStates.HumanReview, invalidTaskKey);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var path = $"/api/tasks/{TaskKey}/integration-records?watchPath={Uri.EscapeDataString(_watchPath)}";

        using var inFlight = await client.PostAsJsonAsync(path, Request());
        Assert.Equal(HttpStatusCode.Conflict, inFlight.StatusCode);

        var invalidPath = $"/api/tasks/{invalidTaskKey}/integration-records?watchPath={Uri.EscapeDataString(_watchPath)}";
        using var invalid = await client.PostAsJsonAsync(
            invalidPath,
            Request() with { Classification = "looks-good" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var detailResponse = await client.GetAsync(
            $"/api/tasks/{TaskKey}?watchPath={Uri.EscapeDataString(_watchPath)}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Empty(detail.RootElement.GetProperty("info").GetProperty("integrationRecords").EnumerateArray());

        using var invalidDetailResponse = await client.GetAsync(
            $"/api/tasks/{invalidTaskKey}?watchPath={Uri.EscapeDataString(_watchPath)}");
        invalidDetailResponse.EnsureSuccessStatusCode();
        using var invalidDetail = JsonDocument.Parse(await invalidDetailResponse.Content.ReadAsStringAsync());
        Assert.Empty(invalidDetail.RootElement.GetProperty("info").GetProperty("integrationRecords").EnumerateArray());
    }

    [Fact]
    public async Task AppendIntegrationRecord_DuplicateIdsRequireAKeyOrFolderAddress()
    {
        const string duplicateId = "human-decision-needed-die-zeilen-sind-nicht-aussichtlichen-verteilt";
        var firstFolder = WriteFlatTask(TaskStates.Archive, "ASS-324", duplicateId);
        var secondFolder = WriteFlatTask(TaskStates.Archive, "ASS-1309", duplicateId);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var ambiguous = await client.PostAsJsonAsync(
            $"/api/tasks/{duplicateId}/integration-records?watchPath={Uri.EscapeDataString(_watchPath)}",
            Request());
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);

        using var addressed = await client.PostAsJsonAsync(
            $"/api/tasks/ASS-1309/integration-records?watchPath={Uri.EscapeDataString(_watchPath)}",
            Request());
        addressed.EnsureSuccessStatusCode();

        using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(firstFolder, "task.json")));
        Assert.False(first.RootElement.TryGetProperty("integrationRecords", out _));
        using var second = JsonDocument.Parse(File.ReadAllText(Path.Combine(secondFolder, "task.json")));
        Assert.Single(second.RootElement.GetProperty("integrationRecords").EnumerateArray());
    }

    private AppendTaskIntegrationRecordRequest Request() => new()
    {
        Id = "operator-gpt-verification-2026-08-11",
        Classification = IntegrationRecordClasses.IntegratedVerified,
        AcceptedAtUtc = new DateTime(2026, 8, 11, 10, 0, 0, DateTimeKind.Utc),
        IntegrationBranch = "main",
        CommitShas = ["0123456789abcdef0123456789abcdef01234567"],
        Evidence = "GPT-reviewed Git ancestry confirms the accepted content is on main.",
    };

    private WebApplicationFactory<Program> BuildFactory(bool guarded = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "integration-record-test",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _workspace,
                    ["ProjectSettings:integration-record-test:IntegrationBranch"] = "main",
                    ["DeliveryChain:Guarded"] = guarded.ToString(),
                }));
        });

    private void WriteTask(string state, string taskKey = TaskKey)
    {
        var folder = Path.Combine(_watchPath, state, taskKey);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = taskKey,
            key = taskKey,
            title = "Integration record endpoint fixture",
            state,
            order = 1,
            agent = "codex",
            cliType = "codex",
            taskType = "feature",
            createdAt = "2026-08-11T09:00:00Z",
            enteredLaneAt = "2026-08-11T10:00:00Z",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Verify integration bookkeeping.");
    }

    private string WriteFlatTask(string state, string key, string id)
    {
        Assert.True(TaskStorageLayout.TryParseKeyNumber(key, out var number));
        var folder = TaskStorageLayout.JobDir(_watchPath, number, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = $"Integration record endpoint fixture {key}",
            state,
            order = 1,
            agent = "codex",
            cliType = "codex",
            taskType = "feature",
            mode = TaskModes.Coding,
            fixture = true,
            createdAt = "2026-06-09T09:00:00Z",
            enteredLaneAt = "2026-06-09T10:00:00Z",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Verify integration bookkeeping.");
        return folder;
    }

}
