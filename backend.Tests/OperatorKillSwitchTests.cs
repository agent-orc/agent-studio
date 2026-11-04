using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-2: the kill switch coordinator, the out-of-band command
/// on the control plane host (with the API stopped and with the API running),
/// the per-session kill, and the drill against a test principal.
/// </summary>
public sealed class OperatorKillSwitchTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-kill-unit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly AccessSecurityStore _store;
    private readonly FakeRevocation _revocation = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly OperatorKillSwitch _killSwitch;
    private readonly string _ownerId;

    public OperatorKillSwitchTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        _ownerId = _store.Bootstrap(new BootstrapRequest("first.owner", Password, null)).User.Id;
        _killSwitch = new OperatorKillSwitch(_store, new OperatorKillSwitchLog(_store.SecurityFolder), _revocation, _notifier, _clock);
    }

    [Fact]
    public async Task Ending_all_access_disables_every_operator_agent_revokes_every_session_and_audits_one_distinct_event()
    {
        var (first, firstSecret) = StartAgent("ops console");
        var (second, secondSecret) = StartAgent("release bot");
        var start = _store.ControlPlaneAudit.HeadSequence;

        var record = await _killSwitch.EndAllAsync(OperatorKillActor.Owner(_ownerId), OperatorKillTriggers.Studio, "  loop  ", null, default);

        Assert.Null(_store.AuthenticateServiceAccount(firstSecret));
        Assert.Null(_store.AuthenticateServiceAccount(secondSecret));
        Assert.All(_store.ListOperatorAgents(), agent => Assert.NotNull(agent.DisabledAt));
        Assert.Equal(new[] { first, second }.Order(), record.OperatorAgentIds.Order());
        Assert.Equal(2, record.SessionIds.Count);
        Assert.Equal("loop", record.Reason);
        Assert.Equal(OperatorKillPropagationStates.Revoked, record.OperationsServer.State);
        Assert.Equal((true, true), (_revocation.Calls.Single().All, _revocation.Calls.Single().AgentIds.Count == 0));
        Assert.NotNull(record.NotifiedAt);
        var notice = Assert.Single(_notifier.Notices);
        Assert.True(notice.Acute);

        var entries = _store.ControlPlaneAudit.Read(start + 1, 10);
        Assert.Equal([OperatorKillEvents.All, OperatorKillEvents.OperationsServer], entries.Select(entry => entry.Operation));
        Assert.Equal((ControlPlaneAuditStreams.KillSwitch, $"human:{_ownerId}", "operator-agents:all"),
            (entries[0].Stream, entries[0].Actor, entries[0].Target));
        Assert.Contains(record.Id, entries[0].Raw, StringComparison.Ordinal);
        Assert.Throws<SecurityOperationException>(() =>
            _store.StartOperatorSession(first, new StartOperatorSessionRequest(null, null), _ownerId));
    }

    [Fact]
    public async Task Only_an_active_owner_ends_operator_access_from_studio()
    {
        StartAgent("ops console");
        var admin = _store.InviteUser(new InviteUserRequest("workspace.admin", "Admin", "", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), _ownerId).User.Id;
        var refused = await Assert.ThrowsAsync<SecurityOperationException>(() =>
            _killSwitch.EndAllAsync(OperatorKillActor.Owner(admin), OperatorKillTriggers.Studio, null, null, default));
        Assert.Equal("owner-required", refused.Code);
        Assert.All(_store.ListOperatorAgents(), agent => Assert.Null(agent.DisabledAt));
    }

    [Fact]
    public async Task A_failed_operations_server_revocation_stays_pending_and_is_retried_until_it_succeeds()
    {
        StartAgent("ops console");
        _revocation.FailNext = 2;
        var record = await _killSwitch.EndAllAsync(OperatorKillActor.Owner(_ownerId), OperatorKillTriggers.Studio, null, null, default);
        Assert.Equal((OperatorKillPropagationStates.Pending, 1, "http-503"),
            (record.OperationsServer.State, record.OperationsServer.Attempts, record.OperationsServer.LastError));

        await _killSwitch.RetryPendingAsync(default);
        Assert.Equal(1, _revocation.Calls.Count);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await _killSwitch.RetryPendingAsync(default);
        _clock.Advance(TimeSpan.FromSeconds(4));
        await _killSwitch.RetryPendingAsync(default);

        var stored = _killSwitch.Log.Find(record.Id)!;
        Assert.Equal((OperatorKillPropagationStates.Revoked, 3), (stored.OperationsServer.State, stored.OperationsServer.Attempts));
        var propagation = _store.ControlPlaneAudit.ReadNewest(entry => entry.Operation == OperatorKillEvents.OperationsServer, 10);
        Assert.Equal(["ok", "failed"], propagation.Select(entry => entry.Outcome));
    }

    [Fact]
    public async Task A_session_kill_ends_only_that_session_and_has_its_own_event()
    {
        var agent = _store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null), _ownerId);
        var runaway = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest("runaway", null), _ownerId);
        var healthy = _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest("healthy", null), _ownerId);

        var record = await _killSwitch.KillSessionAsync(agent.Id, runaway.Session.Id, _ownerId, null, default);

        Assert.Null(_store.AuthenticateServiceAccount(runaway.Secret));
        Assert.NotNull(_store.AuthenticateServiceAccount(healthy.Secret));
        Assert.Equal((OperatorKillKinds.Session, OperatorKillPropagationStates.NotRequired), (record.Kind, record.OperationsServer.State));
        Assert.Empty(_revocation.Calls);
        var entry = Assert.Single(_store.ControlPlaneAudit.ReadNewest(candidate => candidate.Stream == ControlPlaneAuditStreams.KillSwitch, 10));
        Assert.Equal((OperatorKillEvents.Session, $"operator-agent:{agent.Id}"), (entry.Operation, entry.Target));
        Assert.Equal("operator-session-ended", (await Assert.ThrowsAsync<SecurityOperationException>(() =>
            _killSwitch.KillSessionAsync(agent.Id, runaway.Session.Id, _ownerId, null, default))).Code);
    }

    [Fact]
    public async Task A_drill_ends_its_test_principal_records_the_measured_time_and_leaves_real_sessions_working()
    {
        var (_, realSecret) = StartAgent("ops console");

        var drill = await _killSwitch.RunDrillAsync(OperatorKillTriggers.ManualDrill, _ownerId, default);

        Assert.True(drill.Passed, drill.Failure);
        Assert.NotNull(drill.StudioRefusedMs);
        Assert.True(drill.DurationMs >= 0);
        Assert.Equal(OperatorKillPropagationStates.Revoked, drill.OperationsServerState);
        Assert.True(drill.OtherSessionsKept);
        Assert.NotNull(_store.AuthenticateServiceAccount(realSecret));
        // The drill's test principal is hidden from the operator list and named alone in the revocation.
        Assert.DoesNotContain(_store.ListOperatorAgents(), agent => agent.Id == drill.OperatorAgentId);
        Assert.Equal((false, drill.OperatorAgentId), (_revocation.Calls.Single().All, _revocation.Calls.Single().AgentIds.Single()));
        var entry = Assert.Single(_store.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == OperatorKillEvents.Drill, 10));
        Assert.Equal(("passed", $"operator-agent:{drill.OperatorAgentId}"), (entry.Outcome, entry.Target));
        Assert.Contains("durationMs", entry.Raw, StringComparison.Ordinal);
        Assert.Equal(drill.Id, Assert.Single(_killSwitch.Log.Read().Drills).Id);
        Assert.False(Assert.Single(_notifier.Notices).Acute);
    }

    [Fact]
    public async Task A_drill_fails_with_a_named_reason_when_the_operations_server_cannot_be_reached()
    {
        _revocation.FailNext = 1;
        var drill = await _killSwitch.RunDrillAsync(OperatorKillTriggers.ScheduledDrill, null, default);
        Assert.False(drill.Passed);
        Assert.Equal("operations-server-unreachable", drill.Failure);
        Assert.Equal("failed", Assert.Single(_store.ControlPlaneAudit.ReadNewest(entry => entry.Operation == OperatorKillEvents.Drill, 10)).Outcome);
        Assert.True(Assert.Single(_notifier.Notices).Acute);
    }

    [Fact]
    public async Task The_scheduled_drill_runs_one_interval_after_start_and_then_every_interval()
    {
        var options = new OperatorKillSwitchOptions(TimeSpan.FromSeconds(2), TimeSpan.FromDays(30));
        var service = new OperatorKillSwitchService(_killSwitch, _store, options, _clock, NullLogger<OperatorKillSwitchService>.Instance);

        await service.TickAsync(default);
        Assert.Empty(_killSwitch.Log.Read().Drills);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(30), _killSwitch.Log.Read().NextDrillAt);

        _clock.Advance(TimeSpan.FromDays(30));
        await service.TickAsync(default);
        var drill = Assert.Single(_killSwitch.Log.Read().Drills);
        Assert.Equal((OperatorKillTriggers.ScheduledDrill, true), (drill.Trigger, drill.Passed));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(30), _killSwitch.Log.Read().NextDrillAt);

        await service.TickAsync(default);
        Assert.Single(_killSwitch.Log.Read().Drills);
    }

    [Fact]
    public async Task The_out_of_band_command_applies_the_kill_itself_while_the_api_is_stopped_and_writes_the_same_event()
    {
        var (agent, secret) = StartAgent("ops console");
        var output = new StringWriter();

        var exit = await OperatorKillSwitchCommand.RunAsync(["--reason", "Studio unreachable"], output, _config, _clock);

        Assert.Equal(OperatorKillSwitchCommand.ExitOk, exit);
        Assert.Contains("The Studio API is stopped", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Operations Server principals: not-configured", output.ToString(), StringComparison.Ordinal);
        // A store opened afterwards (the API's next start) sees the kill, and the latch refuses until it is enforced.
        var restarted = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        Assert.Null(restarted.AuthenticateServiceAccount(secret));
        Assert.NotNull(restarted.ListOperatorAgents().Single(candidate => candidate.Id == agent).DisabledAt);
        Assert.True(restarted.OperatorKillRequested);

        var entry = Assert.Single(restarted.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == OperatorKillEvents.All, 10));
        Assert.Equal((OperatorKillActors.OutOfBand, "ok"), (entry.Actor, entry.Outcome));
        Assert.Contains(OperatorKillTriggers.OutOfBand, entry.Raw, StringComparison.Ordinal);
        Assert.Contains("Studio unreachable", entry.Raw, StringComparison.Ordinal);
        Assert.True(restarted.ControlPlaneAudit.VerifyChain().Ok);

        // The API's first pass removes the latch and sends the Owner notice the command could not.
        var log = new OperatorKillSwitchLog(restarted.SecurityFolder);
        var record = Assert.Single(log.Read().Kills);
        Assert.Null(record.NotifiedAt);
        var api = new OperatorKillSwitch(restarted, log, _revocation, _notifier, _clock);
        var service = new OperatorKillSwitchService(api, restarted, new OperatorKillSwitchOptions(TimeSpan.FromSeconds(2), TimeSpan.Zero),
            _clock, NullLogger<OperatorKillSwitchService>.Instance);
        await service.TickAsync(default);
        Assert.False(restarted.OperatorKillRequested);
        Assert.NotNull(log.Find(record.Id)!.NotifiedAt);
        Assert.Single(_notifier.Notices);
        Assert.Single(restarted.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == OperatorKillEvents.All, 10));
    }

    [Fact]
    public async Task With_the_api_running_the_command_latches_at_once_and_the_api_applies_the_same_event()
    {
        var (_, secret) = StartAgent("ops console");
        Assert.NotNull(_store.AuthenticateServiceAccount(secret));
        var options = new OperatorKillSwitchOptions(TimeSpan.FromSeconds(2), TimeSpan.Zero);
        var service = new OperatorKillSwitchService(_killSwitch, _store, options, _clock, NullLogger<OperatorKillSwitchService>.Instance);
        using var apiLock = OperatorKillSwitchHostLock.TryAcquire(_store.SecurityFolder);
        Assert.NotNull(apiLock);
        // Lock files are exclusive per open, so a second holder is refused even inside one process.
        Assert.Null(OperatorKillSwitchHostLock.TryAcquire(_store.SecurityFolder));

        var output = new StringWriter();
        var command = OperatorKillSwitchCommand.RunAsync(["--wait-seconds", "30"], output, _config, _clock);
        await WaitUntil(() => _store.OperatorKillRequested);
        // Refused from the moment the latch exists, before the API applied anything.
        Assert.Null(_store.AuthenticateServiceAccount(secret));
        Assert.Equal("operator-kill-pending", Assert.Throws<SecurityOperationException>(() =>
            _store.StartOperatorSession(_store.ListOperatorAgents().Single().Id, new StartOperatorSessionRequest(null, null), _ownerId)).Code);

        await service.TickAsync(default);
        // The command polls until the API confirmed; the fake clock never ends its wait, so bound it here.
        Assert.Equal(OperatorKillSwitchCommand.ExitOk, await command.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("The Studio API is running", output.ToString(), StringComparison.Ordinal);
        Assert.False(_store.OperatorKillRequested);
        Assert.Null(_store.AuthenticateServiceAccount(secret));
        var entry = Assert.Single(_store.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == OperatorKillEvents.All, 10));
        Assert.Equal(OperatorKillActors.OutOfBand, entry.Actor);
        Assert.Equal(OperatorKillTriggers.OutOfBand, Assert.Single(_killSwitch.Log.Read().Kills).Trigger);
    }

    [Fact]
    public async Task An_api_that_loaded_its_state_before_the_command_re_enforces_the_kill_before_removing_the_latch()
    {
        var (_, secret) = StartAgent("ops console");
        // This store stands for an API that read the access state before the command wrote it.
        Assert.NotNull(_store.AuthenticateServiceAccount(secret));
        Assert.Equal(0, await OperatorKillSwitchCommand.RunAsync([], new StringWriter(), _config, _clock));
        Assert.Null(_store.AuthenticateServiceAccount(secret));

        var service = new OperatorKillSwitchService(_killSwitch, _store, new OperatorKillSwitchOptions(TimeSpan.FromSeconds(2), TimeSpan.Zero),
            _clock, NullLogger<OperatorKillSwitchService>.Instance);
        await service.TickAsync(default);

        Assert.False(_store.OperatorKillRequested);
        Assert.Null(_store.AuthenticateServiceAccount(secret));
        Assert.All(_store.ListOperatorAgents(), agent => Assert.NotNull(agent.DisabledAt));
        Assert.Single(_store.ControlPlaneAudit.ReadNewest(entry => entry.Operation == OperatorKillEvents.Enforced, 10));
        // The command appended to the chain while this store held its head; the next entry still follows it.
        var chain = _store.ControlPlaneAudit.VerifyChain();
        Assert.True(chain.Ok, chain.Message);
    }

    private (string AgentId, string Secret) StartAgent(string name)
    {
        var agent = _store.CreateOperatorAgent(new CreateOperatorAgentRequest(name, null), _ownerId);
        return (agent.Id, _store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), _ownerId).Secret);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(25);
        Assert.True(condition());
    }

    private sealed class FakeRevocation : IOperatorPrincipalRevocation
    {
        public int FailNext { get; set; }
        public List<(IReadOnlyList<string> AgentIds, bool All)> Calls { get; } = [];
        public bool Configured => true;

        public Task<OperatorRevocationOutcome> RevokeAsync(IReadOnlyList<string> operatorAgentIds, bool all, string correlationId,
            CancellationToken ct)
        {
            Calls.Add((operatorAgentIds, all));
            if (FailNext-- > 0) return Task.FromResult(new OperatorRevocationOutcome(false, [], 0, "http-503"));
            return Task.FromResult(new OperatorRevocationOutcome(true, ["operator-console"], 1, null));
        }
    }

    private sealed class RecordingNotifier : IOperatorKillNotifier
    {
        public List<OperatorKillNotice> Notices { get; } = [];

        public Task<bool> NotifyAsync(OperatorKillNotice notice, CancellationToken ct)
        {
            Notices.Add(notice);
            return Task.FromResult(true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
