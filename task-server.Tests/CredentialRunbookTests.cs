using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class CredentialRunbookTests
{
    [Theory]
    [InlineData("claude_native_login", "credential_invalid", true, null, null, "RB-CLAUDE-ROTATION", "guided-repair")]
    [InlineData("codex_chatgpt_login", "indeterminate", false, "unauthorized", null, "RB-CODEX-INCIDENT", "diagnosis-only")]
    [InlineData("codex_chatgpt_login", "provider_incident", false, "unauthorized", "official-applicable", "RB-CODEX-INCIDENT", "bounded-retry")]
    [InlineData("github_deploy_key", "credential_invalid", false, null, null, "RB-GITHUB-WORKSPACE", "guided-repair")]
    public void Reported_incidents_select_typed_policy(string kind, string outcome, bool shared,
        string? code, string? corroboration, string expectedRunbook, string expectedPolicy)
    {
        var fact = new CredentialIncidentFacts(kind, outcome, shared, code, corroboration,
            kind == "github_deploy_key" ? "workspace" : null,
            kind == "github_deploy_key" ? false : null);
        var match = CredentialRunbookCatalog.Classify(fact);
        Assert.NotNull(match);
        Assert.Equal(expectedRunbook, match.RunbookId);
        Assert.Equal(expectedPolicy, match.Policy);
    }

    [Fact]
    public async Task Claimed_side_effect_is_not_reissued_after_restart_without_reconciliation()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        await store.UpsertCredentialRegistryAsync(new(RegistryFixture(outcome: "credential_invalid",
            evidence: "evidence:1"), "instance-1", null,
            clock.GetUtcNow().UtcDateTime), "actor-1", default);
        var request = new BeginCredentialRunbookRequest("op-1", "host-1", "cred-1", "gen-1", "incident-1",
            "evidence:1", new CredentialIncidentFacts("github_deploy_key", "credential_invalid", false,
                null, null, "workspace", false));
        var first = await store.BeginCredentialRunbookAsync(request, "actor-1", default);
        await store.RegisterRunnerAsync("runner-1", new RegisterRunnerRequest(
            "runner-1", "host-1", "instance-1", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "actor-1", default);
        await store.RegisterRunnerAsync("runner-2", new RegisterRunnerRequest(
            "runner-2", "host-2", "instance-1", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "actor-1", default);
        Assert.True(await store.RunnerOwnsCredentialRunbookHostAsync("runner-1", "op-1", "instance-1", default));
        Assert.False(await store.RunnerOwnsCredentialRunbookHostAsync("runner-2", "op-1", "instance-1", default));
        Assert.False(await store.RunnerOwnsCredentialRunbookHostAsync("runner-1", "op-1", "old-instance", default));
        await store.RegisterRunnerAsync("runner-1", new RegisterRunnerRequest(
            "runner-1", "host-1", "instance-2", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "actor-1", default);
        Assert.False(await store.RunnerOwnsCredentialRunbookHostAsync("runner-1", "op-1", "instance-1", default));
        Assert.False(await store.RunnerOwnsCredentialRunbookHostAsync("runner-1", "op-1", "instance-2", default));
        var repeated = await store.BeginCredentialRunbookAsync(request, "actor-1", default);
        Assert.Equal(first, repeated);
        var changedFacts = request with { Facts = request.Facts with { RefreshSessionSharedAcrossHosts = true } };
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.BeginCredentialRunbookAsync(changedFacts, "actor-1", default));
        Assert.Equal("runbook-idempotency-conflict", conflict.Code);
        var claim = await store.ClaimCredentialRunbookStepAsync("op-1", "host-1", "gen-1", default);
        Assert.Equal("discover_repository_binding", claim.StepId);
        var reopened = new TaskServerStore(options, clock);
        await reopened.InitializeAsync();
        var ambiguous = await reopened.ClaimCredentialRunbookStepAsync("op-1", "host-1", "gen-1", default);
        Assert.Equal("reconcile-required", ambiguous.Status);
        var syntheticCredential = "sk_" + new string('x', 40);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.CompleteCredentialRunbookStepAsync(
            "op-1", "discover_repository_binding",
            new CompleteCredentialRunbookStepRequest("host-1", "gen-1", "verified", "gen-1",
                [syntheticCredential]), default));
        var completed = await reopened.CompleteCredentialRunbookStepAsync("op-1", "discover_repository_binding",
            new CompleteCredentialRunbookStepRequest("host-1", "gen-1", "verified", "gen-1", ["evidence-2"]), default);
        Assert.Equal("verified", completed.Outcome);
        var replay = await reopened.CompleteCredentialRunbookStepAsync("op-1", "discover_repository_binding",
            new CompleteCredentialRunbookStepRequest("host-1", "gen-1", "verified", "gen-1", ["evidence-2"]), default);
        Assert.Equal(completed.OperationId, replay.OperationId);
        Assert.Equal(completed.OccurredAt, replay.OccurredAt);
        Assert.Equal(completed.EvidenceRefs, replay.EvidenceRefs);
    }

    [Theory]
    [InlineData("claude_native_login", "credential_invalid", "RB-CLAUDE-ROTATION")]
    [InlineData("codex_chatgpt_login", "provider_incident", "RB-CODEX-INCIDENT")]
    [InlineData("github_deploy_key", "credential_invalid", "RB-GITHUB-WORKSPACE")]
    public async Task Fake_host_replay_recovers_each_boundary_with_one_receipt(
        string kind, string outcome, string runbookId)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        var hostCount = kind == "claude_native_login" ? 2 : 1;
        for (var hostNumber = 1; hostNumber <= hostCount; hostNumber++)
        {
            var hostId = $"host-{hostNumber}";
            var credentialId = $"cred-{hostNumber}";
            await store.UpsertCredentialRegistryAsync(new(RegistryFixture(kind, hostId, credentialId,
                outcome, $"evidence:{hostNumber}"),
                "instance-1", null, clock.GetUtcNow().UtcDateTime), "actor-1", default);
            var request = new BeginCredentialRunbookRequest($"op-{hostNumber}", hostId,
                credentialId, "gen-1", $"incident-{hostNumber}", $"evidence:{hostNumber}",
                new CredentialIncidentFacts(kind, outcome,
                    kind == "claude_native_login", kind == "codex_chatgpt_login" ? "unauthorized" : null,
                    kind == "codex_chatgpt_login" ? "official-applicable" : null,
                    kind == "github_deploy_key" ? "workspace" : null,
                    kind == "github_deploy_key" ? false : null,
                    kind == "github_deploy_key" ? true : null));
            var operation = await store.BeginCredentialRunbookAsync(request, "actor-1", default);
            Assert.Equal(runbookId, operation.RunbookId);
            var steps = CredentialRunbookCatalog.StepsFor(runbookId, operation.Policy);
            foreach (var step in steps)
            {
                var claim = await store.ClaimCredentialRunbookStepAsync(operation.OperationId,
                    hostId, "gen-1", default);
                Assert.Equal(step, claim.StepId);
                if (claim.Status == "claimed")
                {
                    store = new TaskServerStore(options, clock);
                    await store.InitializeAsync();
                    Assert.Equal("reconcile-required", (await store.ClaimCredentialRunbookStepAsync(
                        operation.OperationId, hostId, "gen-1", default)).Status);
                }
                else Assert.Equal("human-required", claim.Status);
                var observed = kind == "codex_chatgpt_login" ? "gen-1" :
                    step is "verify_repository_access" or "switch_repository_identity" or
                        "install_generation" or "verify_real_request" or "advertise_generation" or
                        "retire_old_generation" ? "gen-2" : "gen-1";
                if (step is "install_generation" or "switch_repository_identity")
                {
                    clock.Advance(TimeSpan.FromSeconds(1));
                    var updated = RegistryFixture(kind, hostId, credentialId,
                        outcome, $"evidence:{hostNumber}") with
                    {
                        Generation = "gen-2", Supersedes = "gen-1"
                    };
                    await store.UpsertCredentialRegistryAsync(new(updated, "instance-1", "gen-1",
                        clock.GetUtcNow().UtcDateTime), "actor-1", default);
                }
                var verification = step switch
                {
                    "verify_real_request" when kind == "codex_chatgpt_login" => "unauthorized",
                    "verify_real_request" or "verify_recovery_canary" => "healthy",
                    "verify_repository_access" => "fetch-and-push-verified",
                    "advertise_generation" => "advertised",
                    _ => null
                };
                var completion = new CompleteCredentialRunbookStepRequest(hostId, "gen-1",
                    claim.Status == "human-required" ? "human-approved" : "verified", observed,
                    [$"evidence-{hostNumber}-{step}"], VerificationResult: verification);
                if (step == "verify_recovery_canary")
                    await Assert.ThrowsAsync<TaskServerConflictException>(() =>
                        store.CompleteCredentialRunbookStepAsync(operation.OperationId, step,
                            completion with { VerificationResult = "unauthorized" }, default));
                var receipt = claim.Status == "human-required"
                    ? await store.CompleteCredentialRunbookHumanStepAsync(operation.OperationId, step, completion, default)
                    : await store.CompleteCredentialRunbookStepAsync(operation.OperationId, step, completion, default);
                Assert.Equal(step, receipt.StepId);
                Assert.Equal(hostId, receipt.HostId);
                Assert.Equal($"incident-{hostNumber}", operation.IncidentId);
                Assert.Equal(steps.TakeWhile(item => item != step).Count() + 1,
                    (await store.ListCredentialRunbookReceiptsAsync(operation.OperationId, default)).Count);
            }
            Assert.Equal("complete", (await store.GetCredentialRunbookAsync(operation.OperationId, default))!.Status);
        }
    }

    [Fact]
    public void A_network_failure_cannot_start_workspace_key_repair()
    {
        Assert.Null(CredentialRunbookCatalog.Classify(new CredentialIncidentFacts(
            "github_deploy_key", "network_failure", false, null, null, "workspace", false)));
    }

    [Fact]
    public async Task Stale_registry_evidence_cannot_authorize_repair()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await store.UpsertCredentialRegistryAsync(new(RegistryFixture(outcome: "credential_invalid",
            evidence: "evidence:1"), "instance-1", null, clock.GetUtcNow().UtcDateTime), "actor-1", default);
        clock.Advance(TimeSpan.FromMinutes(11));
        var request = new BeginCredentialRunbookRequest("stale-op", "host-1", "cred-1", "gen-1",
            "incident-1", "evidence:1", new CredentialIncidentFacts("github_deploy_key",
                "credential_invalid", false, null, null, "workspace", false));
        var error = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.BeginCredentialRunbookAsync(request, "actor-1", default));
        Assert.Equal("runbook-evidence-mismatch", error.Code);
    }

    private static CredentialRegistryRecordDto RegistryFixture(string kind = "github_deploy_key",
        string hostId = "host-1", string credentialId = "cred-1",
        string outcome = "not_verified", string evidence = "none") => new(
        "installation-1", hostId, credentialId, "provider", "actor-1", "recovery-owner",
        "gen-1", CredentialRegistryProtocol.RenewalMethods[kind], 1, kind, null, ["scope:host"],
        [new("coding", "workspace", "local")], new("ssh-private-key", "host-key", "active"),
        "unknown", new Dictionary<string, string>
        {
            ["createdAt"] = "unknown", ["discoveredAt"] = "unknown",
            ["lastVerifiedAt"] = "unknown", ["lastRenewedAt"] = "unknown",
            ["expiresAt"] = "unknown", ["rotationDueAt"] = "unknown",
            ["accessTokenExpiresAt"] = "unknown", ["lastRealSuccessAt"] = "unknown",
            ["nextProbeAt"] = "unknown"
        }, null, null, null, null, null, outcome, [evidence],
        null, null, null, null, null, null, null, null, null);
}
