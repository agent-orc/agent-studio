using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using AgentStudio.Operator;
using Contract = AgentStudio.TaskServer.Contracts;
using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewDecisionOrchestratorMachineBlockerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "machine-blocker-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
    private string Watch => Path.Combine(_workspace, "projects", "demo");
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _machine;
    private readonly AgentStudio.TaskAccess.ITaskAccess _access;
    private readonly IConfiguration _config;
    private readonly TimelineLog _timeline = new(NullLogger<TimelineLog>.Instance);
    private int _modelCalls;

    public ReviewDecisionOrchestratorMachineBlockerTests()
    {
        foreach (var lane in TaskStates.All) Directory.CreateDirectory(Path.Combine(Watch, lane));
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace, ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = Watch, ["WatchPaths:0:RootPath"] = Watch,
            ["ReviewDecisionOrchestrator:Enabled"] = "true", ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
        }).Build();
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config));
        _machine = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance);
        var cache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(cache);
        var mutations = new TaskMutationService(_scanner,
            new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance, _timeline);
        var transitions = new TaskTransitionService(_scanner, _machine, mutations,
            new GitService(NullLogger<GitService>.Instance, _scanner, _config),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config),
            NullLogger<TaskTransitionService>.Instance, timeline: _timeline);
        _access = new AgentStudio.TaskAccess.TaskAccessService(_scanner, mutations, _machine, transitions, cache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
    }

    [Theory]
    [InlineData("TASK_BLOCKED")]
    [InlineData("TASK_NEEDS_INPUT")]
    public async Task Integrated_dependency_requeues_without_model_or_owner_item(string sentinel)
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, sentinel,
            "implementation of card AGT-4011 is not integrated");
        var orchestrator = Build(IntegrationStatuses.Integrated);
        await orchestrator.TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Ready, child.State);
        Assert.Contains("current integration base", File.ReadAllText(Path.Combine(child.FolderPath, "orchestrator-follow-up.md")));
        Assert.Empty(OperatorDecisionFacts.FromTasks([child], _ => null));
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
        Assert.Equal(0, _modelCalls);
    }

    [Fact]
    public async Task Pending_dependency_waits_and_wakes_when_platform_facts_change()
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "agt-4011-implementation-not-integrated");
        await Build(IntegrationStatuses.Pending).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.AutoReview, child.State);
        var receipt = ReadReceipt(child);
        Assert.Equal("integration-dependency", receipt.GetProperty("kind").GetString());
        Assert.Equal(_time.GetUtcNow().AddMinutes(30), receipt.GetProperty("deadline").GetDateTimeOffset());
        Assert.Empty(OperatorDecisionFacts.FromTasks([child], _ => null));
        _time.Advance(TimeSpan.FromMinutes(1));
        await Build(IntegrationStatuses.Integrated).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.Ready, _scanner.FindJob("child", Watch)!.State);
        Assert.Equal(0, _modelCalls);
    }

    [Theory]
    [InlineData(false, IntegrationStatuses.Pending)]
    [InlineData(true, IntegrationStatuses.ConflictSkipped)]
    public async Task Missing_or_failed_dependency_creates_one_repair_and_machine_quarantine(bool exists, string status)
    {
        if (exists) Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "missing-AGT-4011-implementation");
        var orchestrator = Build(status);
        await orchestrator.TickOnceAsync(_workspace, default);
        await orchestrator.TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Escalated, child.State);
        Assert.Single(_access.ListByLane("demo", TaskStates.Preparation));
        var park = ParkedBlockerMarker.TryRead(child.FolderPath)!;
        Assert.Equal(IntegrationBlockerPolicy.QuarantineCategory, park.BlockerType);
        Assert.False(ParkedBlockerCatalog.RequiresDecisionCard(park.BlockerType));
        Assert.Empty(OperatorDecisionFacts.FromTasks([child with { ParkedBlocker = Status(park.BlockerType) }], _ => null));
        Assert.Equal(0, _modelCalls);
    }

    [Fact]
    public async Task Existing_automatic_conflict_round_is_a_pending_dependency()
    {
        Seed("parent", "AGT-4011", TaskStates.Ready);
        File.WriteAllText(Path.Combine(_scanner.FindJob("parent", Watch)!.FolderPath, "pending-intent.json"),
            """{"mode":"continue","savedReason":"merge-conflict","prompt":"Resolve the recorded conflict."}""");
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "missing-AGT-4011-implementation");
        await Build(IntegrationStatuses.ConflictSkipped).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.AutoReview, _scanner.FindJob("child", Watch)!.State);
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Fact]
    public async Task Integration_into_another_target_branch_does_not_release_the_wait()
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "agt-4011-implementation-not-integrated");
        var orchestrator = Build(IntegrationStatuses.Integrated);
        orchestrator.MachineBlockerIntegrationReader = _ => new() { Status = IntegrationStatuses.Integrated, IntegrationBranch = "main" };
        await orchestrator.TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.AutoReview, _scanner.FindJob("child", Watch)!.State);
    }

    [Fact]
    public async Task An_owner_authored_ordinary_dependency_still_resolves_from_integration_facts()
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "agt-4011-implementation-not-integrated");
        var path = Path.Combine(_scanner.FindJob("child", Watch)!.FolderPath, "task.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["references"] = System.Text.Json.Nodes.JsonNode.Parse("""{"dependsOn":[{"key":"AGT-4011","origin":"owner"}]}""");
        File.WriteAllText(path, json.ToJsonString());
        _scanner.InvalidateCache();
        await Build(IntegrationStatuses.Integrated).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Ready, child.State);
        Assert.Equal("owner", Assert.Single(child.References.DependsOn).Origin);
        Assert.Empty(OperatorDecisionFacts.FromTasks([child], _ => null));
    }

    [Fact]
    public async Task Explicit_release_hold_is_preserved_when_a_dependency_is_integrated()
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "agt-4011-implementation-not-integrated");
        var path = Path.Combine(_scanner.FindJob("child", Watch)!.FolderPath, "task.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["references"] = System.Text.Json.Nodes.JsonNode.Parse("""{"dependsOn":[{"key":"AGT-4011","releaseGate":true,"origin":"owner"}]}""");
        File.WriteAllText(path, json.ToJsonString());
        _scanner.InvalidateCache();
        await Build(IntegrationStatuses.Integrated).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Escalated, child.State);
        Assert.True(Assert.Single(child.References.DependsOn).ReleaseGate);
        Assert.Equal(HumanReviewEscalationCategories.AgentBlocked, ParkedBlockerMarker.TryRead(child.FolderPath)!.BlockerType);
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Fact]
    public async Task Dependency_deadline_survives_restart_and_reaches_quarantine()
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_NEEDS_INPUT", "agt-4011-implementation-not-integrated");
        await Build(IntegrationStatuses.Pending).TickOnceAsync(_workspace, default);
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(IntegrationStatuses.Pending).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Escalated, child.State);
        Assert.Contains("deadline expired", ReadReceipt(child).GetProperty("reason").GetString());
        Assert.Single(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Fact]
    public async Task Synthetic_hardware_finding_without_source_is_quarantined_without_waiving_evidence()
    {
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_NEEDS_INPUT",
            "Hardware verification is unavailable on this fleet.");
        await Build(IntegrationStatuses.Pending).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Escalated, child.State);
        Assert.Contains("could not be reconstructed", ReadReceipt(child).GetProperty("reason").GetString());
        Assert.Single(_access.ListByLane("demo", TaskStates.Preparation));
        Assert.Equal(0, _modelCalls);
    }

    [Fact]
    public async Task A_stated_product_fork_is_preserved_even_when_its_sentinel_only_names_integration()
    {
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED", "agt-4011-implementation-not-integrated");
        File.WriteAllText(TaskPaths.CliOutputLog(_scanner.FindJob("child", Watch)!.FolderPath),
            "[12:00:00.000] [stdout] Should the release keep the public endpoint?\n"
            + "[12:00:00.100] [stdout] - Option A: Keep the endpoint. Existing clients continue (recommended).\n"
            + "[12:00:00.200] [stdout] - Option B: Remove the endpoint. Clients must migrate.\n"
            + "[12:00:01.000] [stdout] [[TASK_BLOCKED:agt-4011-implementation-not-integrated]]\n");
        await Build(IntegrationStatuses.Integrated).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.Escalated, _scanner.FindJob("child", Watch)!.State);
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Fact]
    public async Task Real_product_question_keeps_human_intake()
    {
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_BLOCKED",
            "Should we change scope while implementation of card AGT-4011 is not integrated?");
        await Build(IntegrationStatuses.Integrated).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Escalated, child.State);
        Assert.Equal(HumanReviewEscalationCategories.AgentBlocked, ParkedBlockerMarker.TryRead(child.FolderPath)!.BlockerType);
        Assert.Single(OperatorDecisionFacts.FromTasks([child], _ => null));
    }

    [Fact]
    public async Task Mandatory_capability_deadline_revokes_pending_review_and_preserves_immutable_result()
    {
        var (authority, review, envelope) = SeedCanonicalReview();
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry()).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.AutoReview, _scanner.FindJob("child", Watch)!.State);
        _time.Advance(TimeSpan.FromMinutes(16));
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry()).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.Escalated, child.State);
        Assert.Equal(AttemptLifecycleState.Superseded, authority.GetReview(review.AttemptId)!.State);
        Assert.Equal(envelope, authority.GetRun(review.SourceRunAttemptId)!.ResultEnvelope);
        Assert.Equal(envelope.ResultSha, ReviewSubjectStore.Read(child.FolderPath)!.ResultSha);
        Assert.Equal(envelope.ImmutableRemoteRef, ReviewSubjectStore.Read(child.FolderPath)!.ImmutableResultRef);
        Assert.Single(_access.ListByLane("demo", TaskStates.Preparation));
        Assert.Equal(0, _modelCalls);
    }

    [Fact]
    public async Task A_claimed_review_supersedes_a_pending_capability_observation()
    {
        var (authority, review, _) = SeedCanonicalReview();
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry()).TickOnceAsync(_workspace, default);
        Assert.True(authority.ClaimReview(review.AttemptId, "reviewer", "host", 1800, "claim").Accepted);
        _time.Advance(TimeSpan.FromMinutes(16));
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry()).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.AutoReview, _scanner.FindJob("child", Watch)!.State);
        Assert.Equal(AttemptLifecycleState.Leased, authority.GetReview(review.AttemptId)!.State);
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Fact]
    public async Task Standalone_authority_inventory_places_verification_on_an_alternate_capable_host()
    {
        var (authority, review, _) = SeedCanonicalReview();
        _config["TaskServer:BaseUrl"] = "http://authority.invalid";
        var now = _time.GetUtcNow().UtcDateTime;
        var requirements = Contract.ReviewLibraryStepPolicy.ExecutionCapabilities(review.Subject.Plan!)
            .Append(Contract.ReviewCapabilities.ReviewExecutor);
        var capabilities = requirements.Select(key => new Contract.CapabilityHealthDto(key, "verification", "ready", "healthy",
            null, now, now.AddMinutes(1), true, null, null, null, null, 0, null, null, null, [], [])).ToArray();
        var host = new Contract.RunnerCapabilitySnapshotDto("reviewer", "reviewer", "alternate-host", "instance", "1", 1,
            "active", now, now, new("alternate-host", "open", null, null, null, null), capabilities, null);
        using var http = new InventoryClient([host]);
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry(), http).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.AutoReview, child.State);
        Assert.Equal("alternate-host", Assert.Single(ReadReceipt(child).GetProperty("capableHosts").EnumerateArray()).GetString());
        Assert.Equal(MachineBlockerAction.PlaceVerification, (MachineBlockerAction)ReadReceipt(child).GetProperty("action").GetInt32());
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
        Assert.Equal(1, http.Requests);
    }

    [Fact]
    public async Task Unreachable_authority_inventory_is_unknown_and_uses_the_fixed_capability_deadline()
    {
        var (authority, _, _) = SeedCanonicalReview();
        _config["TaskServer:BaseUrl"] = "http://authority.invalid";
        using var http = new InventoryClient([], fail: true);
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry(), http).TickOnceAsync(_workspace, default);
        var waiting = _scanner.FindJob("child", Watch)!;
        Assert.Equal(TaskStates.AutoReview, waiting.State);
        Assert.Contains("inventory=unavailable", ReadReceipt(waiting).GetProperty("evidence")[0].GetString());
        _time.Advance(TimeSpan.FromMinutes(16));
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry(), http).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.Escalated, _scanner.FindJob("child", Watch)!.State);
        Assert.Single(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Fact]
    public async Task Explicit_host_owner_hold_is_preserved_beyond_the_machine_deadline()
    {
        var (authority, review, _) = SeedCanonicalReview();
        _config["TaskServer:BaseUrl"] = "http://authority.invalid";
        var now = _time.GetUtcNow().UtcDateTime;
        var capabilities = Contract.ReviewLibraryStepPolicy.ExecutionCapabilities(review.Subject.Plan!)
            .Append(Contract.ReviewCapabilities.ReviewExecutor)
            .Select(key => new Contract.CapabilityHealthDto(key, "verification", "ready", "healthy", null,
                now, now.AddMinutes(1), true, null, null, null, null, 0, null, null, null, [], [])).ToArray();
        var host = new Contract.RunnerCapabilitySnapshotDto("held-reviewer", "held-reviewer", "held-host", "instance", "1", 1,
            "active", now, now, new("held-host", "operator-drain", null, null, "Owner maintenance", now), capabilities, null);
        var inventory = new[] { host, host with { RunnerId = "alternate", HostId = "alternate-host",
            HostAdmission = new("alternate-host", "automatic-drain", "Recovering", now, null, null) } };
        using var http = new InventoryClient(inventory);
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry(), http).TickOnceAsync(_workspace, default);
        _time.Advance(TimeSpan.FromMinutes(16));
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry(), http).TickOnceAsync(_workspace, default);
        Assert.Equal(TaskStates.AutoReview, _scanner.FindJob("child", Watch)!.State);
        Assert.Equal(AttemptLifecycleState.Pending, authority.GetReview(review.AttemptId)!.State);
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
        var restoredAt = _time.GetUtcNow().UtcDateTime;
        inventory[1] = inventory[1] with
        {
            LastSeenAt = restoredAt,
            HostAdmission = new("alternate-host", "open", null, null, null, null),
            Capabilities = capabilities.Select(capability => capability with
                { FreshUntil = restoredAt.AddMinutes(1), AdvertisedAt = restoredAt }).ToArray(),
        };
        await Build(IntegrationStatuses.Pending, authority, new V1ReviewExecutorRegistry(), http).TickOnceAsync(_workspace, default);
        var child = _scanner.FindJob("child", Watch)!;
        Assert.Equal("alternate-host", Assert.Single(ReadReceipt(child).GetProperty("capableHosts").EnumerateArray()).GetString());
        Assert.Empty(_access.ListByLane("demo", TaskStates.Preparation));
    }

    [Theory]
    [InlineData(true, TaskStates.Ready)]
    [InlineData(false, TaskStates.AutoReview)]
    public async Task Disabled_model_budget_resolves_only_machine_facts_without_a_model_call(bool codingParent, string expectedState)
    {
        _config["ReviewDecisionOrchestrator:CallsPerHour"] = "0";
        Seed("parent", "AGT-4011", TaskStates.HumanReview);
        if (!codingParent)
        {
            var path = Path.Combine(_scanner.FindJob("parent", Watch)!.FolderPath, "task.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["mode"] = TaskModes.Research;
            File.WriteAllText(path, json.ToJsonString());
        }
        Seed("child", "AGT-4098", TaskStates.AutoReview, "TASK_NEEDS_INPUT", "agt-4011-implementation-not-integrated");
        await Build(IntegrationStatuses.Integrated).TickOnceAsync(_workspace, default);
        Assert.Equal(expectedState, _scanner.FindJob("child", Watch)!.State);
        Assert.Equal(0, _modelCalls);
    }

    private sealed class InventoryClient(Contract.RunnerCapabilitySnapshotDto[] hosts, bool fail = false) : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("http://authority.invalid") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (fail) throw new HttpRequestException("Synthetic authority outage.");
            Assert.Equal("/api/v1/management/remote-hosts", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(hosts) });
        }
    }

    private (AttemptAuthorityService Authority, ReviewAttemptDto Review, Contract.ImmutableResultEnvelope Envelope) SeedCanonicalReview()
    {
        Seed("child", "AGT-4098", TaskStates.AutoReview);
        var authority = new AttemptAuthorityService(_config, NullLogger<AttemptAuthorityService>.Instance, () => _time.GetUtcNow().UtcDateTime);
        var run = authority.AcquireRun("AGT-4098", "demo", null, "coder", "coding-host", 60, "run").RunAttempt!;
        var sha = new string('a', 40);
        var envelope = new Contract.ImmutableResultEnvelope("demo", run.AttemptId, new string('0', 40), sha,
            "refs/agent-studio/results/" + run.AttemptId, null, new string('1', 64));
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle"), Outcome = "done", ResultSha = sha,
            ResultEnvelope = envelope, ResultEnvelopeDigest = Contract.ResultEnvelopeDigest.Compute(envelope),
        }).Accepted);
        var plan = new Contract.ReviewPlanDto([new("verify", "review-verification", "dotnet", ["test"])],
            ["review-verification"], LibraryVersion: 1);
        var review = authority.CreateReviewAttempt(new("AGT-4098", "demo", sha, run.AttemptId,
            "requirements", "policy", [], "review", ResultRef: envelope.ImmutableRemoteRef, Plan: plan)).ReviewAttempt!;
        ReviewSubjectStore.Write(_scanner.FindJob("child", Watch)!.FolderPath, new()
        {
            TaskKey = "AGT-4098", RunAttemptId = run.AttemptId, AttemptChainId = run.AttemptId,
            ResultSha = sha, ImmutableResultRef = envelope.ImmutableRemoteRef,
        });
        return (authority, review, envelope);
    }

    private ReviewDecisionOrchestrator Build(string integration, AttemptAuthorityService? authority = null,
        V1ReviewExecutorRegistry? executors = null, IHttpClientFactory? httpClients = null)
    {
        var prompts = new RuntimePromptService(_config, NullLogger<RuntimePromptService>.Instance);
        var result = new ReviewDecisionOrchestrator(_scanner, _machine, _access,
            new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance), prompts,
            new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance), new AutoReviewStatusSnapshot(),
            _config, NullLogger<ReviewDecisionOrchestrator>.Instance, timeline: _timeline, timeProvider: _time,
            attemptAuthority: authority, blockerExecutors: executors, blockerHttpClients: httpClients);
        result.MachineBlockerIntegrationReader = _ => new TaskIntegrationStatus { Status = integration, IntegrationBranch = "develop" };
        result.CliRunner = (_, _, _, _, _) => { _modelCalls++; return Task.FromResult("unused"); };
        return result;
    }

    private void Seed(string id, string key, string lane, string? sentinel = null, string reason = "")
    {
        Assert.True(TaskStorageLayout.TryParseKeyNumber(key, out var number));
        var folder = TaskStorageLayout.JobDir(Watch, number, key);
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key, title = id, state = lane, agent = "claude", integrationBranch = "develop", enteredLaneAt = _time.GetUtcNow(),
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "# Implement the scoped change\n\nDeliver the requested source change and focused verification.\n");
        if (sentinel is not null) File.WriteAllText(TaskPaths.CliOutputLog(folder),
            $"[12:00:00.000] [stdout] [[{sentinel}: {reason}]]\n");
        _scanner.InvalidateCache();
    }

    private static JsonElement ReadReceipt(TaskInfo task)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(TaskPaths.LogsDir(task.FolderPath), "machine-blocker.json"))).RootElement;
    private ParkedBlockerStatus Status(string category) => new(category, "manual", "quarantine", _time.GetUtcNow().UtcDateTime, 0,
        "Repair linked", "blocked", null, "mandatory evidence unsatisfied");
    public void Dispose() { try { Directory.Delete(_workspace, true); } catch (IOException) { } }
}
