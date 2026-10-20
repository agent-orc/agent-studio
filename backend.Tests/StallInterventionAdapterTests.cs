using System.Text.Json;
using AgentStudio.Clients;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;
using AgentStudio.Watcher;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3207: the real adapters behind the stall interventions, with keyed
/// cards whose task.json key differs from their folder. The attempt authority
/// files those cards under the key, so every read goes through
/// <see cref="TaskIdentity.AuthorityKey(TaskInfo)"/>.
/// </summary>
public sealed class StallInterventionAdapterTests : IDisposable
{
    private const string Project = "KORI";
    private const string RepositoryId = "repo";
    private static readonly string BaseSha = new('1', 40);
    private static readonly string ResultSha = new('2', 40);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-16T19:51:00Z"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stall-adapters-" + Guid.NewGuid().ToString("N"));
    private readonly IConfigurationRoot _configuration;

    public StallInterventionAdapterTests()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(ProjectPath, state));
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Environment:DefaultIdentityName"] = "Test Default",
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = ProjectPath,
            ["WatchPaths:0:RootPath"] = ProjectPath,
        }).Build();
    }

    private string ProjectPath => Path.Combine(_root, "projects", Project);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void Admission_is_read_and_set_by_client_id_and_never_by_display_name()
    {
        var clients = new ClientIdentityStore(_configuration, NullLogger<ClientIdentityStore>.Instance);
        var runner = clients.Register(new RegisterClientRequest { DisplayName = "KORI Runner 01", Kind = ClientIdentityKinds.Service });
        clients.SetRunnerCapacity(runner.Id, 8, 95, null);
        // Twenty minutes of samples: five overloaded ones, then fifteen calm ones.
        WriteTelemetry(runner.Id, Enumerable.Range(0, 21).Reverse().Select(minutesAgo => new HostTelemetrySample(
            Now.AddMinutes(-minutesAgo), null, minutesAgo > 14 ? 46.7 : 3.0, null, null, null, null, null, null, null, null,
            CpuCores: 12, ActiveSlots: minutesAgo > 14 ? 8 : 2)));
        var control = new ClientIdentityAdmissionControl(clients,
            new HostTelemetryStore(_configuration, NullLogger<HostTelemetryStore>.Instance));

        var reading = control.Read(runner.Id, 0.8, Now);

        Assert.NotEqual("KORI Runner 01", runner.Id);
        Assert.Equal((runner.Id, "KORI Runner 01", 8, 12, 2), (reading!.ClientId, reading.Host, reading.Admission, reading.Cores, reading.ActiveSlots));
        Assert.Equal(0.25, reading.LoadPerCore!.Value, 3);
        Assert.Equal(Now.AddMinutes(-14), reading.CalmSinceUtc);
        Assert.Null(control.Read("KORI Runner 01", 0.8, Now));
        Assert.Null(control.SetAdmission("KORI Runner 01", 4));
        Assert.Equal(6, control.SetAdmission(runner.Id, 6));
        Assert.Equal(6, clients.Find(runner.Id)!.RunnerDesiredMaxParallelism);
        Assert.False(control.TrySetAdmission(runner.Id, 8, 4));
        Assert.Equal(6, clients.Find(runner.Id)!.RunnerDesiredMaxParallelism);
        Assert.True(control.TrySetAdmission(runner.Id, 6, 8));
        Assert.Equal(8, clients.Find(runner.Id)!.RunnerDesiredMaxParallelism);
    }

    [Fact]
    public void Reroute_applies_only_to_review_hosts_and_reads_back_from_the_admitted_capabilities()
    {
        var exclusions = new ProviderRouteExclusions(() => _root);
        var registry = new V1ReviewExecutorRegistry(_clock, exclusions);
        Register(registry, "kori-review-01");
        Register(registry, "kori-review-02");
        var control = new ReviewProviderRouteControl(registry, exclusions, _clock);

        Assert.True(control.IsReviewHost("kori-review-01"));
        Assert.False(control.IsReviewHost("agent-runner-01"));
        Assert.Equal(["kori-review-02"], control.HealthyHosts("codex", "kori-review-01"));
        Assert.False(control.IsRerouted("kori-review-01", "codex"));

        control.Reroute("kori-review-01", "codex", "si-1");
        control.Reroute("agent-runner-01", "codex", "si-2");

        Assert.True(control.IsRerouted("kori-review-01", "codex"));
        Assert.True(control.IsExcluded("kori-review-01", "codex"));
        Assert.DoesNotContain("provider-auth:codex", registry.AdmittedReviewCapabilities("kori-review-01-runner", "kori-review-01:1"));
        Assert.Contains("provider-auth:codex", registry.AdmittedReviewCapabilities("kori-review-02-runner", "kori-review-02:1"));
        // A host without a review registration is never reported as rerouted.
        Assert.False(control.IsRerouted("agent-runner-01", "codex"));
        var withoutSnapshot = new ReviewProviderRouteControl(new V1ReviewExecutorRegistry(_clock, exclusions), exclusions, _clock);
        Assert.False(withoutSnapshot.IsRerouted("kori-review-01", "codex"));
        Assert.True(withoutSnapshot.IsExcluded("kori-review-01", "codex"));

        control.Restore("kori-review-01", "codex");
        Assert.False(control.IsExcluded("kori-review-01", "codex"));
        Assert.False(control.IsRerouted("kori-review-01", "codex"));
        Assert.Contains("provider-auth:codex", registry.AdmittedReviewCapabilities("kori-review-01-runner", "kori-review-01:1"));
    }

    [Fact]
    public void Review_restart_reader_sees_a_lease_started_after_the_last_zero_slot_sample()
    {
        var registry = new V1ReviewExecutorRegistry(_clock);
        Register(registry, "kori-review-01");
        registry.AdvertiseCapabilities("kori-review-01-runner", new Contract.CapabilityAdvertisementRequest(
            "kori-review-01-runner", "kori-review-01:1", Contract.CapabilityProtocol.CurrentSchemaVersion,
            Now, 900, 1,
            [new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.ReviewExecutor, "executor")],
            new Contract.HostTelemetrySnapshotDto(Now, 0, 0, 0, 0, null, null, null, null,
                null, null, 12, 0)));
        var authority = Authority();
        var run = Deliver(authority, "AGT-9201", reviewed: false);
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest("AGT-9201", RepositoryId,
            ResultSha, run.AttemptId, "requirements", "policy", [], "create-AGT-9201")).ReviewAttempt!;
        authority.ClaimReview(review.AttemptId, "kori-review-01-runner", "kori-review-01", 3600,
            "claim-AGT-9201");
        var reader = new ServiceWorkloadReader(registry, authority, new BatchGateStore(Path.Combine(_root, "gates")),
            new GateSlotUsageTracker(_configuration, _clock),
            new PullGateStore(Path.Combine(_root, "pull-gates"), _clock), _configuration);

        var reading = reader.Read("kori-review-01", "review", Now);

        Assert.Equal(0, reading!.BusySlots);
        Assert.True(reading.RunningWork);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(reader.Read("kori-review-01", "review", Now)!.BusySlots);
    }

    [Fact]
    public void Review_restart_fence_rejects_a_claim_while_the_restart_is_queued()
    {
        var authority = Authority();
        var run = Deliver(authority, "AGT-9202", reviewed: false);
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest("AGT-9202", RepositoryId,
            ResultSha, run.AttemptId, "requirements", "policy", [], "create-AGT-9202")).ReviewAttempt!;

        using (var fence = authority.TryFenceReviewHost("kori-review-01"))
        {
            Assert.NotNull(fence);
            var blocked = authority.ClaimReview(review.AttemptId, "kori-review-01-runner", "kori-review-01",
                3600, "claim-while-queued");
            Assert.Equal(AttemptWriteStatus.InvalidState, blocked.Status);
            Assert.Empty(authority.ListActiveReviewAttempts());
        }

        var claimed = authority.ClaimReview(review.AttemptId, "kori-review-01-runner", "kori-review-01",
            3600, "claim-after-restart");
        Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        Assert.Null(authority.TryFenceReviewHost("kori-review-01"));
    }

    [Fact]
    public void Review_restart_fence_survives_status_read_failure_and_studio_restart()
    {
        var authority = Authority();
        var run = Deliver(authority, "AGT-9203", reviewed: false);
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest("AGT-9203", RepositoryId,
            ResultSha, run.AttemptId, "requirements", "policy", [], "create-AGT-9203")).ReviewAttempt!;
        var fence = Assert.IsAssignableFrom<IRestartClaimFence>(authority.TryFenceReviewHost("kori-review-01"));
        fence.Retain(); // The command is queued; reading its status failed.
        Assert.Equal(AttemptWriteStatus.InvalidState, authority.ClaimReview(review.AttemptId,
            "kori-review-01-runner", "kori-review-01", 3600, "claim-during-queue").Status);

        var restartedStudio = Authority();
        Assert.True(restartedStudio.HasReviewRestartFence("kori-review-01"));
        Assert.Equal(AttemptWriteStatus.InvalidState, restartedStudio.ClaimReview(review.AttemptId,
            "kori-review-01-runner", "kori-review-01", 3600, "claim-after-studio-restart").Status);
        fence.Dispose();
        Assert.True(restartedStudio.HasReviewRestartFence("kori-review-01"));

        restartedStudio.ReleaseReviewRestartFence("kori-review-01"); // only after remote terminal confirmation
        Assert.Equal(AttemptWriteStatus.Accepted, restartedStudio.ClaimReview(review.AttemptId,
            "kori-review-01-runner", "kori-review-01", 3600, "claim-after-terminal").Status);
    }

    [Fact]
    public void Pull_gate_reader_counts_only_queued_jobs_and_fences_claims_through_restart()
    {
        var pull = new PullGateStore(Path.Combine(_root, "pull-gates"), _clock);
        pull.Register("kori-gate", "KORI");
        var bundle = Path.Combine(_root, "bundle");
        File.WriteAllText(bundle, "bundle");
        var runId = Guid.NewGuid().ToString("N");
        pull.Enqueue(new RemoteGateInvocation(1, runId, new string('b', 64),
            new BuildTestGateRequest("source.git", BaseSha, "test")
            { InfrastructureTimeout = TimeSpan.FromMinutes(1) },
            null, null, PostStepMode.Fail, 60, 120), bundle);
        var gateConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = "kori-gate",
        }).Build();
        var slots = new GateSlotUsageTracker(gateConfig, _clock);
        slots.RemoteWaiting(0, 2);
        var reader = new ServiceWorkloadReader(new V1ReviewExecutorRegistry(_clock,
                new ProviderRouteExclusions(() => _root)), Authority(),
            new BatchGateStore(Path.Combine(_root, "gates")),
            slots, pull, gateConfig);

        Assert.Equal((0, 1), (reader.Read("kori-gate", "gate", Now)!.BusySlots,
            reader.Read("kori-gate", "gate", Now)!.ClaimableWaiting));
        slots.RemoteWaiting(2, 2); // Workers from an earlier Studio process still hold both slots.
        Assert.Equal(2, reader.Read("kori-gate", "gate", Now)!.BusySlots);
        Assert.Null(reader.TryFenceClaims("kori-gate", "gate"));
        slots.RemoteWaiting(0, 2);
        using (var queued = slots.TryBeginDispatch("gate-already-queued"))
        {
            Assert.NotNull(queued);
            Assert.Equal(1, reader.Read("kori-gate", "gate", Now)!.BusySlots);
            Assert.Null(reader.TryFenceClaims("kori-gate", "gate"));
        }
        var fence = Assert.IsAssignableFrom<IRestartClaimFence>(reader.TryFenceClaims("kori-gate", "gate"));
        Assert.Null(pull.Claim("kori-gate"));
        Assert.Null(slots.TryBeginDispatch("gate-arriving-while-restart-queued"));
        fence.Dispose();
        Assert.NotNull(pull.Claim("kori-gate"));
        Assert.Equal((1, 0), (reader.Read("kori-gate", "gate", Now)!.BusySlots,
            reader.Read("kori-gate", "gate", Now)!.ClaimableWaiting));
        Assert.Null(reader.TryFenceClaims("kori-gate", "gate"));
        Assert.Null(reader.Read("unknown-gate", "gate", Now));
    }

    [Fact]
    public void A_torn_exclusions_file_never_breaks_the_review_capability_path()
    {
        var path = Path.Combine(_root, ".watcher", "provider-route-exclusions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"kori-review-01|codex\": ");
        var exclusions = new ProviderRouteExclusions(() => _root);
        var registry = new V1ReviewExecutorRegistry(_clock, exclusions);
        Register(registry, "kori-review-01");

        Assert.Contains("provider-auth:codex", registry.AdmittedReviewCapabilities("kori-review-01-runner", "kori-review-01:1"));
        Assert.False(exclusions.IsExcluded("kori-review-01", "kori-review-01-runner", "codex"));

        exclusions.Exclude("kori-review-01", "codex", "si-1");
        Assert.True(new ProviderRouteExclusions(() => _root).IsExcluded("kori-review-01", "kori-review-01-runner", "codex"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Requeue_reads_the_keyed_card_and_pins_its_last_reviewed_delivery_after_a_rate_limit()
    {
        var folder = WriteCard(TaskStates.Ready, "rate-limited-slug", "AGT-9001");
        var authority = Authority();
        var delivery = Deliver(authority, "AGT-9001", reviewed: true);
        _clock.Advance(TimeSpan.FromHours(1));
        Fail(authority, "AGT-9001", "codex: rate limit exceeded (429 Too Many Requests)");
        var control = new DeliveryContinuationControl(Scanner(), authority, _clock);

        var candidate = Assert.Single(control.RateLimitedRuns(["AGT-9001"]));
        Assert.Equal((TaskStates.Ready, StallContinuationBases.Missing, true, "refs/heads/agent-results/AGT-9001/r1", ResultSha),
            (candidate.State, candidate.BaseKind, candidate.LastRunRateLimited, candidate.LastDeliveryRef, candidate.LastDeliverySha));

        var pin = control.PinLastDelivery("AGT-9001", "si-1");
        Assert.Equal((ResultSha, DeliveryContinuationControl.Reason), (pin!.Sha, pin.Reason));
        Assert.Equal(StallContinuationBases.Delivery, control.RateLimitedRuns(["AGT-9001"])[0].BaseKind);
        Assert.Null(control.PinLastDelivery("AGT-9001", "si-2"));
        Assert.True(control.Clear("AGT-9001", "si-1"));

        // A base at the start commit of an attempt carries no work: an empty salvage.
        ContinuationBaseStore.Save(folder, new ContinuationBaseRecord("refs/heads/agent-studio/salvage/AGT-9001",
            BaseSha, "run-timeout-salvage", delivery.AttemptId, Now));
        Assert.Equal(StallContinuationBases.EmptySalvage, control.RateLimitedRuns(["AGT-9001"])[0].BaseKind);
        Assert.Equal(ResultSha, control.PinLastDelivery("AGT-9001", "si-3")!.Sha);
    }

    [Fact]
    public void Requeue_preserves_a_human_base_written_after_candidate_selection()
    {
        var folder = WriteCard(TaskStates.Ready, "rate-limited-race", "AGT-9010");
        var authority = Authority();
        Deliver(authority, "AGT-9010", reviewed: true);
        _clock.Advance(TimeSpan.FromHours(1));
        Fail(authority, "AGT-9010", "codex: rate limit exceeded (429 Too Many Requests)");
        var human = new ContinuationBaseRecord("refs/heads/human-choice", new string('3', 40),
            "human-choice", "operator", Now);
        var control = new DeliveryContinuationControl(Scanner(), authority, _clock,
            () => Assert.True(ContinuationBaseStore.Save(folder, human)));

        Assert.Null(control.PinLastDelivery("AGT-9010", "si-race"));
        Assert.Equal(human, ContinuationBaseStore.Read(folder));
    }

    [Fact]
    public void Requeue_undo_preserves_a_human_base_written_after_its_live_read()
    {
        var folder = WriteCard(TaskStates.Ready, "undo-race", "AGT-9012");
        var authority = Authority();
        Deliver(authority, "AGT-9012", reviewed: true);
        _clock.Advance(TimeSpan.FromHours(1));
        Fail(authority, "AGT-9012", "codex: rate limit exceeded (429 Too Many Requests)");
        var human = new ContinuationBaseRecord("refs/heads/human-choice", new string('3', 40),
            "human-choice", "operator", Now);
        var control = new DeliveryContinuationControl(Scanner(), authority, _clock, beforeClear: () =>
            Assert.True(ContinuationBaseStore.Save(folder, human)));

        Assert.NotNull(control.PinLastDelivery("AGT-9012", "si-undo-race"));
        Assert.False(control.Clear("AGT-9012", "si-undo-race"));
        Assert.Equal(human, ContinuationBaseStore.Read(folder));
    }

    [Fact]
    public void Requeue_refuses_a_card_moved_out_of_ready_after_candidate_selection()
    {
        var folder = WriteCard(TaskStates.Ready, "rate-limited-lane-race", "AGT-9011");
        var authority = Authority();
        Deliver(authority, "AGT-9011", reviewed: true);
        _clock.Advance(TimeSpan.FromHours(1));
        Fail(authority, "AGT-9011", "codex: rate limit exceeded (429 Too Many Requests)");
        var moved = Path.Combine(ProjectPath, TaskStates.HumanReview, Path.GetFileName(folder));
        var control = new DeliveryContinuationControl(Scanner(), authority, _clock,
            () => Directory.Move(folder, moved));

        Assert.Null(control.PinLastDelivery("AGT-9011", "si-lane-race"));
        Assert.Null(ContinuationBaseStore.Read(moved));
    }

    [Fact]
    public void Requeue_refuses_an_unreviewed_delivery_and_a_run_that_did_not_end_on_a_rate_limit()
    {
        WriteCard(TaskStates.Ready, "unreviewed-slug", "AGT-9002");
        WriteCard(TaskStates.Ready, "compile-error-slug", "AGT-9003");
        var authority = Authority();
        Deliver(authority, "AGT-9002", reviewed: false);
        Fail(authority, "AGT-9002", "rate limit exceeded");
        Deliver(authority, "AGT-9003", reviewed: true);
        Fail(authority, "AGT-9003", "error CS1002: ; expected");
        var control = new DeliveryContinuationControl(Scanner(), authority, _clock);

        var candidates = control.RateLimitedRuns(["AGT-9002", "AGT-9003"]);

        Assert.Equal((true, null), (candidates[0].LastRunRateLimited, candidates[0].LastDeliverySha));
        Assert.Equal((false, ResultSha), (candidates[1].LastRunRateLimited, candidates[1].LastDeliverySha));
        Assert.Null(control.PinLastDelivery("AGT-9002", "si-1"));
        Assert.Null(control.PinLastDelivery("AGT-9003", "si-1"));
    }

    [Fact]
    public void Batch_gate_drops_only_frozen_members_whose_keyed_card_is_positively_completed_or_stale()
    {
        var gates = Path.Combine(_root, "batch-gates");
        using var factory = Factory(gates);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        WriteCard(TaskStates.Completed, "completed-slug", "AGT-9101");
        WriteCard(TaskStates.Ready, "ready-slug", "AGT-9102");
        WriteCard(TaskStates.AutoReview, "review-slug", "AGT-9103");
        WriteCard(TaskStates.HumanReview, "human-slug", "AGT-9104");
        foreach (var (key, order) in new[] { ("AGT-9101", 0), ("AGT-9102", 1), ("AGT-9103", 2), ("AGT-9104", 3), ("AGT-9199", 4) })
            store.Enqueue(new BatchGatePendingRecord("review-" + key, Subject(key, order), ProjectPath, ProjectPath,
                Path.Combine(ProjectPath, "unknown"), "direct-merge", "task", _clock.GetUtcNow().AddHours(-8)));
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
        var control = factory.Services.GetRequiredService<IBatchGateControl>();

        var facts = control.Read(Project, Now)!;
        Assert.Equal(new (string, string?)[]
            {
                ("AGT-9101", TaskStates.Completed), ("AGT-9102", TaskStates.Ready),
                ("AGT-9103", TaskStates.AutoReview), ("AGT-9104", TaskStates.HumanReview), ("AGT-9199", null),
            }, facts.Members.Select(item => (item.Card, item.State)).ToArray());
        Assert.Equal(8, facts.OpenBatchWaitHours!.Value, 3);
        var dropped = control.Drop(Project, facts.Members.Select(item => item.ReviewAttemptId).ToArray(), "ai-operator:si-1");
        Assert.Equal(["review-AGT-9101", "review-AGT-9102"], dropped);
        Assert.False(control.IsPending(Project, "review-AGT-9101"));
        Assert.True(control.IsPending(Project, "review-AGT-9103"));
        Assert.True(control.IsPending(Project, "review-AGT-9104"));
        Assert.True(control.IsPending(Project, "review-AGT-9199"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> Factory(string gates) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = ProjectPath,
            ["WatchPaths:0:RootPath"] = ProjectPath,
            ["ReviewDecisionOrchestrator:Enabled"] = "false",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<BatchGateStore>();
            services.AddSingleton(new BatchGateStore(gates));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
            services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(BatchGatePilotHostedService)));
        });
    });

    private BatchGateSubject Subject(string key, int order) => new(
        key, Project, "repo", "develop", "full", "digest", "v1",
        $"refs/heads/agent-results/{key}/r1", ResultSha, "run-" + key, 1, 1, ResultSha,
        true, true, true, true, false, null, true, _clock.GetUtcNow().AddHours(-9).AddMinutes(order), order + 1);

    private static void Register(V1ReviewExecutorRegistry registry, string host)
        => registry.Register(host + "-runner", new Contract.RegisterRunnerRequest(host + "-runner", host, host + ":1", "1.0.0",
            Contract.TaskServerProtocol.Current, [Contract.ReviewCapabilities.ReviewExecutor, "provider-auth:codex"]));

    private AttemptAuthorityService Authority()
        => new(_configuration, NullLogger<AttemptAuthorityService>.Instance, () => _clock.GetUtcNow().UtcDateTime);

    private TaskScannerService Scanner()
        => new(_configuration, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _configuration));

    private RunAttemptDto Deliver(AttemptAuthorityService authority, string key, bool reviewed)
    {
        var run = authority.AcquireRun(key, RepositoryId, null, "coding-runner", "coding-host", 600, "acquire-" + key).RunAttempt!;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + key),
            Outcome = "done",
            ResultSha = ResultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(RepositoryId, run.AttemptId, BaseSha, ResultSha,
                $"refs/heads/agent-results/{key}/r1", null, new string('a', 64)),
        });
        Assert.True(settled.Accepted, settled.Message);
        if (!reviewed) return settled.RunAttempt!;
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(key, RepositoryId, ResultSha, run.AttemptId,
            "requirements", "policy", [], "create-" + key, ResultRef: $"refs/heads/agent-results/{key}/r1"));
        var review = authority.ClaimReview(created.ReviewAttempt!.AttemptId, "review-executor", "review-host", 600, "claim-" + key)
            .ReviewAttempt!;
        Assert.True(authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(review.AttemptId, review.LastFence, review.AuthorityEpoch, "pass-" + key),
            ResultSha, ReviewTerminalOutcome.Pass)).Accepted);
        return settled.RunAttempt!;
    }

    private void Fail(AttemptAuthorityService authority, string key, string reason)
    {
        _clock.Advance(TimeSpan.FromMinutes(10));
        var run = authority.AcquireRun(key, RepositoryId, null, "coding-runner", "coding-host", 600, "acquire-2-" + key).RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "fail-" + key),
            Outcome = "failed",
            Reason = reason,
            RequireResultSha = false,
        }).Accepted);
    }

    private string WriteCard(string state, string folderName, string key)
    {
        var folder = Path.Combine(ProjectPath, state, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = folderName, key, title = key, state, order = 1, agent = "codex", cliType = "codex",
        }));
        return folder;
    }

    private void WriteTelemetry(string clientId, IEnumerable<HostTelemetrySample> samples)
    {
        var directory = Path.Combine(_root, "telemetry");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, clientId + ".json"),
            JsonSerializer.Serialize(samples.ToList(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
