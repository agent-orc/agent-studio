using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2818 wire shape. The acceptance the card asks for is "open AGT-2373 and
/// it says why it cannot be picked, without reading a file", so the contract
/// that matters is the one on the HTTP surface:
///
/// <list type="number">
///   <item>a board card and a task detail in a pickup lane carry a
///   <c>pickupHold</c> object with mechanism, reason, age and ways out;</item>
///   <item>a refused dispatch is rendered from the same projection, so the
///   <c>remoteDispatchRejection</c> that was written and never shown finally
///   reaches the card;</item>
///   <item><c>GET /api/pickup-holds</c> lists the whole backlog across
///   projects.</item>
/// </list>
/// </summary>
public sealed class PickupHoldEndpointsTests : IClassFixture<PickupHoldEndpointsTests.Host>
{
    internal const string App = "hold-app";
    internal const string Lib = "hold-lib";

    private readonly Host _host;
    private readonly string _appWatch;
    private readonly string _libWatch;

    public PickupHoldEndpointsTests(Host host)
    {
        _host = host;
        _host.Reset();
        _appWatch = host.WatchPath(App);
        _libWatch = host.WatchPath(Lib);
    }

    /// <summary>The two projects every test of this class reads, with local coding allowed.</summary>
    public sealed class Host() : BoardTestHost("atp-pickup-hold-http-", App, Lib)
    {
        protected override void ConfigureServices(IServiceCollection services)
            => services.AddSingleton<ILocalCodingAdmissionPolicy>(AllowLocalCodingForTests.Instance);
    }

    [Fact]
    public async Task Detail_ArchivedReleaseGate_CardSaysTheGateCanNeverOpenAndNamesAllDecisions()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");

        using var client = _host.CreateClient();
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var doc = JsonDocument.Parse(
            await client.GetStringAsync($"/api/tasks/duplicate-cli-paths?watchPath={watchPath}"));
        var info = doc.RootElement.GetProperty("info");

        var hold = info.GetProperty("pickupHold");
        Assert.Equal("dependency-gate", hold.GetProperty("mechanism").GetString());
        Assert.Equal("unsatisfiable", hold.GetProperty("classification").GetString());
        Assert.True(hold.GetProperty("unsatisfiable").GetBoolean());
        Assert.Contains("AGT-2372", hold.GetProperty("reason").GetString()!, StringComparison.Ordinal);
        Assert.Contains("archived", hold.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.True(hold.GetProperty("heldForSeconds").GetInt64() > 0);

        var kinds = hold.GetProperty("resolutions").EnumerateArray()
            .Select(resolution => resolution.GetProperty("kind").GetString())
            .ToArray();
        Assert.Equal(new[] { "drop-dependency", "repoint-dependency", "archive-waiting-card" }, kinds);

        // The distinction also rides on the dependency edge itself, so the
        // dependency chip can say "can never open" instead of "waiting".
        var item = Assert.Single(info.GetProperty("waitsOn").GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("unsatisfiable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("unsatisfiableReason").GetString()));
    }

    [Fact]
    public async Task Grouped_RefusedDispatch_CardCarriesTheRejectionAsAHold()
    {
        WriteJob(_appWatch, TaskStates.Ready, "installer-one-executable", "AGT-2738",
            enteredLaneAt: "2026-09-06T10:00:00Z",
            rejection: """
            {"code":"capability-mismatch","runnerId":"agent-runner-01","runnerName":"agent-runner-01",
             "reason":"Required capability 'task-server:connectivity' is advertised as unavailable.",
             "rejectedAtUtc":"2026-09-06T19:47:45Z"}
            """);

        using var client = _host.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped"));

        var card = FindCard(doc.RootElement.GetProperty("ready"), "installer-one-executable");
        var hold = card.GetProperty("pickupHold");

        Assert.Equal("dispatch-rejection", hold.GetProperty("mechanism").GetString());
        var reason = hold.GetProperty("reason").GetString()!;
        Assert.Contains("agent-runner-01", reason, StringComparison.Ordinal);
        Assert.Contains("capability-mismatch", reason, StringComparison.Ordinal);
        Assert.Contains("task-server:connectivity", reason, StringComparison.Ordinal);
        Assert.Equal(
            "2026-09-06T19:47:45Z",
            hold.GetProperty("sinceUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));

        // The durable record also reaches the card through the execution
        // projection the detail header already renders.
        var rejection = card.GetProperty("executionLocation").GetProperty("lastRejection");
        Assert.Equal("capability-mismatch", rejection.GetProperty("code").GetString());
    }

    /// <summary>
    /// AGT-3580: "Re-offer the card" is a typed API action, not a sentence. The
    /// route it names clears the recorded refusal, so the hold disappears and the
    /// next dispatch tick offers the card again.
    /// </summary>
    [Fact]
    public async Task RetryDispatch_ClearsTheRecordedRefusal()
    {
        WriteJob(_appWatch, TaskStates.Ready, "installer-one-executable", "AGT-2738",
            rejection: """
            {"code":"capability-mismatch","runnerId":"agent-runner-01","runnerName":"agent-runner-01",
             "reason":"missing","rejectedAtUtc":"2026-09-06T19:47:45Z"}
            """);
        using var client = _host.CreateClient();
        using (var before = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped")))
        {
            var resolution = FindCard(before.RootElement.GetProperty("ready"), "installer-one-executable")
                .GetProperty("pickupHold").GetProperty("resolutions").EnumerateArray()
                .Single(item => item.GetProperty("kind").GetString() == PickupHoldResolutionKinds.RetryDispatch);
            Assert.Equal(RemedyApiActions.ClearDispatchRejection,
                resolution.GetProperty("action").GetProperty("target").GetString());
        }

        async Task<HttpResponseMessage> Clear(string card)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/tasks/{card}/dispatch-rejection?watchPath={Uri.EscapeDataString(_appWatch)}");
            request.Headers.Add("X-Client-Id", "local-default");
            return await client.SendAsync(request);
        }
        using var response = await Clear("AGT-2738");
        response.EnsureSuccessStatusCode();
        using var missing = await Clear("no-such-card");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("remoteDispatchRejection", File.ReadAllText(
            Path.Combine(_appWatch, TaskStates.Ready, "installer-one-executable", "task.json")));
        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped"));
        var card = FindCard(after.RootElement.GetProperty("ready"), "installer-one-executable");
        Assert.True(!card.TryGetProperty("pickupHold", out var hold) || hold.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task PickupEligibleCard_CarriesNoHold()
    {
        WriteJob(_appWatch, TaskStates.Ready, "workable", "APP-9");

        using var client = _host.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped"));

        var card = FindCard(doc.RootElement.GetProperty("ready"), "workable");
        Assert.True(!card.TryGetProperty("pickupHold", out var hold) || hold.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task PickupHolds_ListsTheBacklogAcrossProjects()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");
        WriteJob(_appWatch, TaskStates.Ready, "workable", "APP-9");

        using var client = _host.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/pickup-holds"));

        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("unsatisfiable").GetInt32());
        var keys = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("key").GetString())
            .ToArray();
        Assert.Equal(new[] { "AGT-2373" }, keys);

        using var filtered = JsonDocument.Parse(
            await client.GetStringAsync("/api/pickup-holds?unsatisfiableOnly=true"));
        var only = Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("AGT-2373", only.GetProperty("key").GetString());
    }

    [Fact]
    public async Task PickupHolds_DeduplicatesStalledAttentionByPrerequisite()
    {
        WriteJob(_libWatch, TaskStates.Escalated, "operator-decision", "AGT-2736",
            enteredLaneAt: "2026-09-11T09:00:00Z");
        WriteJob(_appWatch, TaskStates.Ready, "waiter-one", "AGT-2738", dependsOn: "AGT-2736");
        WriteJob(_appWatch, TaskStates.Ready, "waiter-two", "AGT-2739", dependsOn: "AGT-2736");

        using var client = _host.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/pickup-holds"));

        var attention = Assert.Single(doc.RootElement.GetProperty("attention").EnumerateArray());
        Assert.Equal("AGT-2736", attention.GetProperty("prerequisiteKey").GetString());
        Assert.Equal(2, attention.GetProperty("waitingCards").GetInt32());
        Assert.Contains("operator decision", attention.GetProperty("reason").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "2026-09-11T09:00:00Z",
            attention.GetProperty("sinceUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    internal static JsonElement FindCard(JsonElement lane, string id)
    {
        foreach (var card in lane.EnumerateArray())
            if (card.TryGetProperty("id", out var cid) && cid.GetString() == id)
                return card;
        Assert.Fail($"card '{id}' not found in the response");
        return default; // unreachable
    }

    internal static void WriteJob(
        string watchPath,
        string state,
        string slug,
        string key,
        string? dependsOn = null,
        bool releaseGate = false,
        string kind = "task",
        string? enteredLaneAt = null,
        string? rejection = null)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var edge = dependsOn is null
            ? ""
            : $",\"references\":{{\"dependsOn\":[{{\"key\":\"{dependsOn}\",\"releaseGate\":{releaseGate.ToString().ToLowerInvariant()}}}]}}";
        var json =
            $"{{\"id\":\"{slug}\",\"key\":\"{key}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"kind\":\"{kind}\",\"order\":1,\"agent\":\"claude\",\"cliType\":\"claude\"," +
            $"\"ownerClientId\":\"local-default\",\"noBranchExpected\":true" +
            (enteredLaneAt is null ? "" : $",\"enteredLaneAt\":\"{enteredLaneAt}\"") +
            (rejection is null ? "" : $",\"remoteDispatchRejection\":{rejection}") +
            $"{edge}}}";
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }
}

/// <summary>
/// The <see cref="PickupHoldEndpointsTests"/> wire shape on a host that does
/// not allow local coding, so a card in a pickup lane is held by its execution
/// placement. Its own class because that is a different host configuration.
/// </summary>
public sealed class PickupHoldLocalCodingDenialTests : IClassFixture<PickupHoldLocalCodingDenialTests.Host>
{
    private const string App = PickupHoldEndpointsTests.App;

    private readonly Host _host;
    private readonly string _appWatch;

    public PickupHoldLocalCodingDenialTests(Host host)
    {
        _host = host;
        _host.Reset();
        _appWatch = host.WatchPath(App);
    }

    /// <summary>The projects of <see cref="PickupHoldEndpointsTests"/>, with the host's own admission policy.</summary>
    public sealed class Host() : BoardTestHost("atp-pickup-hold-denial-", App, PickupHoldEndpointsTests.Lib);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCodingDenial_ReachesDetailAndBoardWithRoutingHint(bool deliveryUnderReview)
    {
        PickupHoldEndpointsTests.WriteJob(_appWatch, TaskStates.Ready, "local-coding", "APP-1");
        if (deliveryUnderReview)
        {
            var path = Path.Combine(_appWatch, TaskStates.Ready, "local-coding", "task.json");
            var task = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            task["phase"] = LifecyclePhases.AwaitingReview;
            File.WriteAllText(path, task.ToJsonString());
        }

        using var client = _host.CreateClient();
        var watchPath = Uri.EscapeDataString(_appWatch);
        using var detail = JsonDocument.Parse(
            await client.GetStringAsync($"/api/tasks/local-coding?watchPath={watchPath}"));
        var hold = detail.RootElement.GetProperty("info").GetProperty("pickupHold");
        Assert.Equal(PickupHoldMechanisms.ExecutionPlacement, hold.GetProperty("mechanism").GetString());
        Assert.Equal(PickupHoldClassifications.Unsatisfiable, hold.GetProperty("classification").GetString());
        Assert.Contains("local agent execution is disabled", hold.GetProperty("reason").GetString());
        var resolution = Assert.Single(hold.GetProperty("resolutions").EnumerateArray());
        Assert.Equal(PickupHoldResolutionKinds.RouteToRemoteRunner, resolution.GetProperty("kind").GetString());
        Assert.DoesNotContain("/api/", resolution.GetProperty("detail").GetString());
        var action = resolution.GetProperty("action");
        Assert.Equal(RemedyActionKinds.Setting, action.GetProperty("kind").GetString());
        Assert.Equal(ProjectSettingKeys.ExecutionRunner, action.GetProperty("target").GetString());
        Assert.Equal("hold-app", action.GetProperty("project").GetString());

        using var grouped = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped"));
        var card = PickupHoldEndpointsTests.FindCard(grouped.RootElement.GetProperty("ready"), "local-coding");
        Assert.Equal(PickupHoldMechanisms.ExecutionPlacement,
            card.GetProperty("pickupHold").GetProperty("mechanism").GetString());

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/tasks/local-coding/start?watchPath={watchPath}")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Client-Id", "local-default");
        using var start = await client.SendAsync(request);
        Assert.False(start.IsSuccessStatusCode);
        using var startBody = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        Assert.True(startBody.RootElement.TryGetProperty("code", out var code), startBody.RootElement.ToString());
        Assert.Equal(RemoteExecutionRequirement.Code, code.GetString());
        Assert.Contains("local agent execution is disabled", startBody.RootElement.GetProperty("error").GetString());
    }
}
