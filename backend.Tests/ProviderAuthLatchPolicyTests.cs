using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3948: a provider-auth binding that stays indeterminate behind an active
/// status-command session for 30 minutes is named by the queue watchdogs,
/// with the restart as the operator action, instead of a bare
/// <c>unclaimable-plan-requirements</c> or capability mismatch.
/// </summary>
public sealed class ProviderAuthLatchPolicyTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 17, 13, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agt-3948-latch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private V1ReviewExecutorRegistry Registry(string runnerId, string hostId, string role,
        string outcome = "indeterminate", bool? sessionActive = true, TimeSpan? indeterminateFor = null)
    {
        var registry = new V1ReviewExecutorRegistry(_clock);
        // Registration and advertisement name the executor role with different keys.
        var registered = role == "review"
            ? Contract.ReviewCapabilities.ReviewExecutor : Contract.ReviewCapabilities.CodingExecutor;
        var executor = role == "review"
            ? Contract.CapabilityProtocol.ReviewExecutor : Contract.CapabilityProtocol.CodingExecutor;
        registry.Register(runnerId, new Contract.RegisterRunnerRequest(runnerId, hostId, runnerId + ":1", "1.0.0",
            Contract.TaskServerProtocol.Current, [registered, "provider-auth:claude"]));
        registry.AdvertiseCapabilities(runnerId, new Contract.CapabilityAdvertisementRequest(
            runnerId, runnerId + ":1", Contract.CapabilityProtocol.CurrentSchemaVersion, Now, 900, 1,
            [
                new Contract.AdvertisedCapabilityDto(executor, "executor"),
                new Contract.AdvertisedCapabilityDto("provider-auth:claude", "provider-auth", "ready",
                    Detail: "'claude auth status --text' confirmed an active session. Daemon environment credential is active; native file metadata is shadowed.",
                    Signal: "indeterminate", HealthOutcome: outcome,
                    Since: Now - (indeterminateFor ?? TimeSpan.FromMinutes(45)),
                    StatusSessionActive: sessionActive),
            ],
            CredentialHealthVersion: 2));
        return registry;
    }

    [Fact]
    public void Latch_is_named_after_thirty_minutes_behind_an_active_session()
    {
        var latch = Assert.Single(ProviderAuthLatchPolicy.Detect(
            Registry("kori-review", "kori-docker", "review").ListCapabilitySnapshots(), Now));
        Assert.Equal(("kori-review", "kori-docker", "review", "provider-auth:claude"),
            (latch.RunnerId, latch.HostId, latch.Role, latch.CapabilityKey));
        Assert.Equal(Now.AddMinutes(-45), latch.IndeterminateSince);
        Assert.Equal("restart the runner daemon on host kori-docker", latch.OperatorAction);
        Assert.Contains("restart the runner daemon on host kori-docker", ProviderAuthLatchPolicy.Describe(latch));
    }

    [Theory]
    [InlineData("indeterminate", true, 29)]
    [InlineData("indeterminate", false, 45)]
    [InlineData("indeterminate", null, 45)]
    [InlineData("healthy", true, 45)]
    [InlineData("credential_invalid", true, 45)]
    public void Short_or_other_states_are_not_a_latch(string outcome, bool? sessionActive, int minutes)
        => Assert.Empty(ProviderAuthLatchPolicy.Detect(Registry("kori-review", "kori-docker", "review",
            outcome, sessionActive, TimeSpan.FromMinutes(minutes)).ListCapabilitySnapshots(), Now));

    [Fact]
    public void Expired_advertisement_is_not_a_latch()
        => Assert.Empty(ProviderAuthLatchPolicy.Detect(
            Registry("kori-review", "kori-docker", "review").ListCapabilitySnapshots(), Now.AddMinutes(16)));

    [Fact]
    public void Review_stagnation_names_the_latch_and_the_restart()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["AutoReviewQueueStagnation:ThresholdMinutes"] = "20",
        }).Build();
        var authority = new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance,
            () => Now.AddHours(-1));
        var run = authority.AcquireRun("AGT-1", "PROJ-1", null, "coding-runner", "coding-host", 60, "acquire").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle"),
            Outcome = "done",
            ResultSha = "sha-1",
        });
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-1", run.AttemptId, "requirements", "policy", [], "create")).ReviewAttempt!;
        var unclaimable = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance, _clock);
        unclaimable.Record("kori-review",
            [new Contract.ReviewUnclaimableAttemptDto(review.AttemptId, "AGT-1", review.CreatedAt, ["provider-auth:claude"])]);
        var watchdog = new AutoReviewQueueStagnationWatchdog(new AutoReviewPostProcessingQueue(), authority,
            unclaimable, new AutoReviewStatusSnapshot(), config,
            NullLogger<AutoReviewQueueStagnationWatchdog>.Instance,
            Registry("kori-review", "kori-docker", "review"));

        var snapshot = watchdog.Refresh(Now);

        Assert.True(snapshot.IsStagnant);
        Assert.Equal(Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements, snapshot.UnclaimableReason);
        Assert.Equal(ProviderAuthLatchPolicy.Cause, snapshot.StagnationCause);
        Assert.Equal("restart the runner daemon on host kori-docker", snapshot.OperatorAction);
        Assert.Equal("kori-review", Assert.Single(snapshot.ProviderAuthLatches).RunnerId);
    }

    [Fact]
    public void Ready_starvation_names_the_latch_on_the_refused_card()
    {
        var latches = ProviderAuthLatchPolicy.Detect(
            Registry("agent-runner-01", "runner-01", "coding").ListCapabilitySnapshots(), Now);
        var snapshot = new RemoteQueueStarvationSnapshot
        {
            Active = true,
            Items =
            [
                new RemoteQueueStarvationItem
                {
                    TaskKey = "AGT-10",
                    LastRejection = new RemoteDispatchRejection
                    {
                        Code = "capability-mismatch",
                        RunnerId = "agent-runner-01",
                        Reason = "Required capability 'provider-auth:claude' is held for indeterminate.",
                    },
                },
                new RemoteQueueStarvationItem { TaskKey = "AGT-11" },
            ],
        };

        var named = RemoteQueueStarvationLatchOverlay.Apply(snapshot, latches);

        Assert.Equal(ProviderAuthLatchPolicy.Cause, named.Items[0].BlockReasonCode);
        Assert.Contains("restart the runner daemon on host runner-01", named.Items[0].BlockReason);
        Assert.Null(named.Items[1].BlockReasonCode);
        Assert.Equal("restart the runner daemon on host runner-01", named.OperatorAction);
        Assert.Same(snapshot, RemoteQueueStarvationLatchOverlay.Apply(snapshot,
            ProviderAuthLatchPolicy.Detect(Registry("kori-review", "kori-docker", "review")
                .ListCapabilitySnapshots(), Now)));
    }
}
