using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderProbeEvidenceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void September_401_requires_applicable_incident_evidence()
    {
        var request = new ProviderProbeRequest("codex", "chatgpt", "codex-exec", "native-cli-store", "g1", At,
            new ProcessResult(1, "", "HTTP 401 Unauthorized: Incorrect API key provided: sk-svcacct-REDACTED"));
        var unknown = ProviderProbeClassifier.Classify(request);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, unknown.Outcome);
        Assert.DoesNotContain("sk-svcacct", unknown.Detail, StringComparison.Ordinal);

        var incident = new ProviderIncidentEvidence("openai-incident-1", "codex", "codex-exec", At.AddMinutes(-5), null,
            At.AddMinutes(1), "official-status");
        Assert.Equal(ProviderProbeOutcome.ProviderIncident,
            ProviderProbeClassifier.Classify(request with { Incidents = [incident] }).Outcome);
        Assert.Equal(ProviderProbeOutcome.Indeterminate,
            ProviderProbeClassifier.Classify(request with { Incidents = [incident with { Service = "sora" }] }).Outcome);
        Assert.Equal(ProviderProbeOutcome.Indeterminate,
            ProviderProbeClassifier.Classify(request with { Incidents = [incident with { ObservedAt = At.AddMinutes(-20) }] }).Outcome);
        var comparison = new ProviderComparisonEvidence("codex", "codex-exec", "minimal-text-v1",
            "unauthorized:service-account-shaped", At.AddMinutes(-3), At, true, true,
            "independent-host", "other-credential", true);
        Assert.Equal(ProviderProbeOutcome.ProviderIncident,
            ProviderProbeClassifier.Classify(request with
            {
                HostId = "affected-host", CredentialIdentity = "affected-credential", Comparison = comparison,
            }).Outcome);
        Assert.Equal(ProviderProbeOutcome.Indeterminate,
            ProviderProbeClassifier.Classify(request with
            {
                HostId = "affected-host", CredentialIdentity = "affected-credential",
                Comparison = comparison with { CredentialIdentity = "affected-credential" },
            }).Outcome);
    }

    [Fact]
    public void Distinct_local_failures_stay_distinct()
    {
        var baseRequest = new ProviderProbeRequest("codex", "chatgpt", "codex-exec", "native-cli-store", "g1", At);
        Assert.Equal(ProviderProbeOutcome.CredentialInvalid,
            ProviderProbeClassifier.Classify(baseRequest with { EffectiveSource = "absent" }).Outcome);
        Assert.Equal(ProviderProbeOutcome.CredentialInvalid,
            ProviderProbeClassifier.Classify(baseRequest with { ExplicitNonRefreshableExpiry = At.AddMinutes(-1) }).Outcome);
        Assert.Equal(ProviderProbeOutcome.QuotaExhausted,
            ProviderProbeClassifier.Classify(baseRequest with { RealRequest = new ProcessResult(1, "", "insufficient_quota; resets at 2026-09-26T13:00:00Z") }).Outcome);
        Assert.Equal(ProviderProbeOutcome.NetworkFailure,
            ProviderProbeClassifier.Classify(baseRequest with { RealRequest = new ProcessResult(1, "", "DNS lookup failed") }).Outcome);
        Assert.Equal(ProviderProbeOutcome.Indeterminate,
            ProviderProbeClassifier.Classify(baseRequest with { StatusCommandUnsupported = true }).Outcome);
        Assert.Equal(ProviderProbeOutcome.Healthy,
            ProviderProbeClassifier.Classify(baseRequest with { RealRequest = new ProcessResult(0, "OK", "") }).Outcome);
    }

    [Fact]
    public async Task Official_status_is_cached_and_only_whitelisted_incident_fields_are_used()
    {
        var calls = 0;
        var json = """
            {"incidents":[
              {"id":"incident-1","name":"Codex request errors","created_at":"2026-09-26T11:55:00Z","resolved_at":null,"incident_updates":[{"body":"secret-shaped text must not be retained"}]},
              {"id":"incident-2","name":"Sora rendering errors","created_at":"2026-09-26T11:55:00Z","resolved_at":null}]}
            """;
        var adapter = new ProviderStatusIncidentAdapter((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(json);
        }, () => At);
        var first = await adapter.GetAsync("codex", CancellationToken.None);
        var second = await adapter.GetAsync("codex", CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.True(first.Available);
        Assert.Equal("incident-1", Assert.Single(first.Incidents).Id);
        Assert.Equal("official-status", first.Provenance);
    }

    [Fact]
    public async Task Degraded_status_does_not_refresh_last_good_forever()
    {
        var now = At;
        var calls = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new ProcessResult(0, "Logged in", "")
                : new ProcessResult(2, "", "unsupported command")),
            executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "unknown"));
        var good = await probe.RefreshAsync("codex", CancellationToken.None);
        now = now.AddMinutes(6);
        var firstDegraded = await probe.RefreshAsync("codex", CancellationToken.None);
        now = now.AddMinutes(6);
        var expired = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(good.ObservedAt, firstDegraded.ObservedAt);
        Assert.Equal(good.ObservedAt, expired.ObservedAt);
        Assert.Equal(ProviderAuthProbe.Ready, expired.Status);
        Assert.Equal("indeterminate", expired.Signal);
    }

    [Fact]
    public async Task Successful_work_is_real_evidence_only_for_the_observed_generation()
    {
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        await probe.RefreshAsync("codex", CancellationToken.None);
        probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""), credentialGeneration: "old");
        Assert.Null(probe.Current("codex").LastRealSuccessAt);
        probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""), credentialGeneration: "g1");
        Assert.Equal(At, probe.Current("codex").LastRealSuccessAt);
    }

    [Fact]
    public async Task Missing_source_is_typed_without_changing_legacy_admission_status()
    {
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "missing",
                EffectiveSource: "absent"));
        var status = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderProbeOutcome.CredentialInvalid, status.Outcome);
        Assert.Equal("absent", status.EffectiveSource);
        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
    }

    [Fact]
    public void Work_that_overlaps_a_native_store_replacement_is_not_generation_matched()
    {
        var status = new ProviderAuthStatus(ProviderAuthProbe.Ready, "status", At,
            CredentialGeneration: "g1", EffectiveSource: "native-cli-store");
        var effective = new ProviderCredentialFreshness(null, At.AddMinutes(1), "native store",
            EffectiveSource: "native-cli-store", CredentialGeneration: "g1");
        Assert.Null(RemoteTaskRunner.SameGenerationForCompletedWork(status, effective, At.UtcDateTime));
        Assert.Equal("g1", RemoteTaskRunner.SameGenerationForCompletedWork(
            status, effective with { ModifiedAt = At.AddMinutes(-1) }, At.UtcDateTime));
        Assert.Null(RemoteTaskRunner.SameGenerationForCompletedWork(
            status, effective with { CredentialGeneration = "g2" }, At.AddMinutes(2).UtcDateTime));
    }

    [Fact]
    public async Task Bounded_real_request_uses_cached_official_evidence_without_echoing_token()
    {
        var launched = 0;
        var statusFetches = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        var adapter = new ProviderStatusIncidentAdapter((_, _) =>
        {
            Interlocked.Increment(ref statusFetches);
            return Task.FromResult("""
                {"incidents":[{"id":"incident-401","name":"Codex authentication errors",
                "created_at":"2026-09-26T11:55:00Z","resolved_at":null}]}
                """);
        }, () => At);
        probe.UseRealRequest((_, _, _) =>
        {
            Interlocked.Increment(ref launched);
            return Task.FromResult(new ProcessResult(1, "", "HTTP 401 Incorrect API key provided: sk-svcacct-REDACTED"));
        }, adapter);
        var first = await probe.RefreshAsync("codex", CancellationToken.None);
        var second = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderProbeOutcome.ProviderIncident, first.Outcome);
        Assert.Equal(ProviderProbeOutcome.ProviderIncident, second.Outcome);
        Assert.Equal(2, launched);
        Assert.Equal(1, statusFetches);
        Assert.DoesNotContain("sk-svcacct", first.Detail, StringComparison.Ordinal);
        Assert.Equal("unauthorized:service-account-shaped", first.EvidenceExcerpt);
    }

    [Fact]
    public async Task Status_401_still_makes_a_real_request_and_keeps_uncorroborated_failure_indeterminate()
    {
        var calls = 0;
        var now = At;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized"));
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            now = now.AddMinutes(1);
            var observed = await probe.RefreshAsync("codex", CancellationToken.None);
            Assert.Equal(attempt, calls);
            Assert.Equal(ProviderProbeOutcome.Indeterminate, observed.Outcome);
            Assert.Equal("indeterminate", observed.Signal);
            Assert.Equal("unauthorized", observed.EvidenceExcerpt);
            Assert.Equal("native-cli-store", observed.EffectiveSource);
            Assert.Equal("g1", observed.CredentialGeneration);
        }
    }

    [Fact]
    public async Task Live_probe_consumes_independent_host_comparison_metadata()
    {
        var comparisonCalls = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => At),
            (query, _) =>
            {
                Interlocked.Increment(ref comparisonCalls);
                Assert.Equal("unauthorized", query.FailureSignature);
                return Task.FromResult(new ProviderComparisonSnapshot("local-credential",
                    new ProviderComparisonEvidence("codex", "codex-exec", "minimal-text-v1",
                        "unauthorized", At.AddMinutes(-3), At, true, true,
                        "other-host", "other-credential", true)));
            }, "local-host");

        var observed = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(1, comparisonCalls);
        Assert.Equal(ProviderProbeOutcome.ProviderIncident, observed.Outcome);
    }

    [Fact]
    public async Task Degraded_status_cannot_republish_an_old_real_request_failure_as_fresh_comparison()
    {
        var now = At;
        var statusCalls = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(Interlocked.Increment(ref statusCalls) == 1
                ? new ProcessResult(0, "Logged in", "")
                : new ProcessResult(2, "", "unsupported command")),
            executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest((_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));
        Assert.Equal("unauthorized", (await probe.RefreshAsync("codex", CancellationToken.None)).EvidenceExcerpt);
        now = now.AddMinutes(6);
        Assert.Null((await probe.RefreshAsync("codex", CancellationToken.None)).EvidenceExcerpt);
    }

    [Fact]
    public async Task Status_401_does_not_spend_a_real_request_while_quota_limit_is_active()
    {
        var calls = 0;
        var statusCalls = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(Interlocked.Increment(ref statusCalls) == 1
                ? new ProcessResult(1, "", "usage limit reached; resets at 2026-09-26T13:00:00Z")
                : new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => At));
        await probe.RefreshAsync("codex", CancellationToken.None);
        await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Real_requests_stop_at_the_daily_budget()
    {
        var calls = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ProcessResult(1, "", "unclassified provider refusal"));
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => At));
        for (var i = 0; i < ProviderAuthProbe.DailyRealRequestBudget + 4; i++)
            await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.DailyRealRequestBudget, calls);
    }

    [Fact]
    public async Task Exhausted_budget_expires_healthy_when_its_real_success_is_too_old()
    {
        var now = At;
        var calls = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest((_, _, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(call == ProviderAuthProbe.DailyRealRequestBudget
                ? new ProcessResult(0, "OK", "")
                : new ProcessResult(1, "", "unclassified provider refusal"));
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));

        for (var i = 0; i < ProviderAuthProbe.DailyRealRequestBudget; i++)
            await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderProbeOutcome.Healthy, probe.Current("codex").Outcome);

        now = now.Add(ProviderAuthProbe.HealthyRealCheckCeiling).AddSeconds(1);
        var stale = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, stale.Outcome);
        Assert.Equal("indeterminate", stale.Signal);
        Assert.Equal(At, stale.LastRealSuccessAt);
        Assert.Equal(ProviderAuthProbe.DailyRealRequestBudget, calls);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, probe.Current("codex").Outcome);

        now = now.AddSeconds(1);
        var recovered = probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""),
            credentialGeneration: "g1");
        Assert.Equal(ProviderProbeOutcome.Healthy, recovered.Outcome);
        Assert.Equal(ProviderAuthProbe.SignalOk, recovered.Signal);
        Assert.False(recovered.ProbeDegraded);
        Assert.Equal(now, recovered.LastRealSuccessAt);
        Assert.Equal(ProviderAuthProbe.DailyRealRequestBudget, calls);
    }

    [Fact]
    public async Task Cached_healthy_outcome_expires_before_a_background_refresh_completes()
    {
        var now = At;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")),
            new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));
        Assert.Equal(ProviderProbeOutcome.Healthy,
            (await probe.RefreshAsync("codex", CancellationToken.None)).Outcome);

        now = now.Add(ProviderAuthProbe.HealthyRealCheckCeiling).AddSeconds(1);
        var stale = probe.Current("codex");
        Assert.Equal(ProviderProbeOutcome.Indeterminate, stale.Outcome);
        Assert.Equal("indeterminate", stale.Signal);
        Assert.Equal(At, stale.LastRealSuccessAt);
    }

    [Fact]
    public async Task In_flight_probe_cannot_replace_newer_successful_work_on_the_same_generation()
    {
        var now = At;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: "g1"));
        probe.UseRealRequest(async (_, _, _) =>
        {
            entered.TrySetResult();
            return await release.Task;
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));

        var inFlight = probe.RefreshAsync("codex", CancellationToken.None);
        await entered.Task;
        now = now.AddMinutes(1);
        var work = probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""),
            credentialGeneration: "g1");
        Assert.Equal(ProviderProbeOutcome.Healthy, work.Outcome);
        Assert.Equal(now, work.LastRealSuccessAt);

        now = now.AddMinutes(1);
        release.SetResult(new ProcessResult(1, "", "HTTP 401 Unauthorized"));
        var completed = await inFlight;
        Assert.Equal(ProviderProbeOutcome.Healthy, completed.Outcome);
        Assert.Equal(work.LastRealSuccessAt, completed.LastRealSuccessAt);
        Assert.Equal(ProviderProbeOutcome.Healthy, probe.Current("codex").Outcome);
        Assert.Null(probe.Current("codex").EvidenceExcerpt);
    }

    [Fact]
    public async Task Concurrent_refreshes_keep_one_real_request_in_flight()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true,
            clock: () => At,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store"));
        probe.UseRealRequest(async (_, _, _) =>
        {
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return new ProcessResult(0, "OK", "");
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => At));
        var first = probe.RefreshAsync("codex", CancellationToken.None);
        await entered.Task;
        var second = probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(1, peak);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, peak);
    }

    private static ProviderCredentialFreshness G1(string _) => new(null, null, "native store",
        EffectiveSource: "native-cli-store", CredentialGeneration: "g1");

    /// <summary>A status launcher whose next answer waits until the test releases it.</summary>
    private sealed class HeldStatus
    {
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<ProcessResult>? _release;
        private TaskCompletionSource<ProcessResult>? _waiting;
        public ProcessResult Answer { get; set; } = new(0, "Logged in", "");
        public Task Entered => _entered.Task;

        public void Hold()
        {
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release(ProcessResult answer) => _waiting!.SetResult(answer);

        public Task<ProcessResult> Launch(string _, IReadOnlyList<string> __, CancellationToken ___)
        {
            var release = _release;
            if (release is null) return Task.FromResult(Answer);
            _waiting = release;
            _release = null;
            _entered.TrySetResult();
            return release.Task;
        }
    }

    [Fact]
    public async Task Status_answer_started_before_successful_work_cannot_demote_it()
    {
        var now = At;
        var realCalls = 0;
        var status = new HeldStatus();
        var probe = new ProviderAuthProbe(status.Launch, executableExists: _ => true,
            clock: () => now, credentialFreshness: G1);
        await probe.RefreshAsync("codex", CancellationToken.None);
        probe.UseRealRequest((_, _, _) =>
        {
            Interlocked.Increment(ref realCalls);
            return Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized"));
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));

        status.Hold();
        now = now.AddMinutes(6);
        var refresh = probe.RefreshAsync("codex", CancellationToken.None);
        await status.Entered;
        now = now.AddSeconds(10);
        var work = probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""),
            credentialGeneration: "g1");
        Assert.Equal(ProviderProbeOutcome.Healthy, work.Outcome);

        now = now.AddSeconds(10);
        status.Release(new ProcessResult(1, "", "HTTP 401 Unauthorized"));
        var completed = await refresh;

        Assert.Equal(ProviderProbeOutcome.Healthy, completed.Outcome);
        Assert.Equal(ProviderAuthProbe.SignalOk, completed.Signal);
        Assert.False(completed.ProbeDegraded);
        Assert.Equal(work.LastRealSuccessAt, completed.LastRealSuccessAt);
        Assert.Equal(ProviderProbeOutcome.Healthy, probe.Current("codex").Outcome);
        Assert.Equal(0, realCalls);
    }

    [Fact]
    public async Task Status_answer_started_before_run_failure_cannot_clear_it()
    {
        var now = At;
        var status = new HeldStatus();
        var probe = new ProviderAuthProbe(status.Launch, executableExists: _ => true,
            clock: () => now, credentialFreshness: G1);
        await probe.RefreshAsync("codex", CancellationToken.None);

        status.Hold();
        now = now.AddMinutes(6);
        var refresh = probe.RefreshAsync("codex", CancellationToken.None);
        await status.Entered;
        var run = probe.RecordProcessResult("codex", new ProcessResult(1, "", "HTTP 401 Unauthorized"));
        Assert.True(run.ProbeDegraded);

        status.Release(new ProcessResult(0, "Logged in", ""));
        var completed = await refresh;

        Assert.True(completed.ProbeDegraded);
        Assert.Equal(run.Signal, completed.Signal);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, completed.Outcome);
        Assert.True(probe.Current("codex").ProbeDegraded);
    }

    [Fact]
    public async Task Status_answer_for_a_new_credential_generation_still_publishes_after_run_evidence()
    {
        var now = At;
        var generation = "g1";
        var status = new HeldStatus();
        var probe = new ProviderAuthProbe(status.Launch, executableExists: _ => true,
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "native store",
                EffectiveSource: "native-cli-store", CredentialGeneration: generation));
        await probe.RefreshAsync("codex", CancellationToken.None);

        status.Hold();
        generation = "g2";
        var refresh = probe.RefreshAsync("codex", CancellationToken.None);
        await status.Entered;
        probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""), credentialGeneration: "g1");
        status.Release(new ProcessResult(0, "Logged in", ""));
        var completed = await refresh;

        Assert.Equal("g2", completed.CredentialGeneration);
        Assert.Null(completed.LastRealSuccessAt);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, completed.Outcome);
    }

    [Fact]
    public async Task Successful_work_resets_an_earlier_logout_confirmation()
    {
        var now = At;
        var status = new HeldStatus();
        var probe = new ProviderAuthProbe(status.Launch, executableExists: _ => true,
            clock: () => now, credentialFreshness: G1);
        await probe.RefreshAsync("codex", CancellationToken.None);

        status.Answer = new ProcessResult(1, "", "Not logged in");
        now = now.AddMinutes(6);
        var first = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Ready, first.Status);

        now = now.AddMinutes(1);
        probe.RecordProcessResult("codex", new ProcessResult(0, "OK", ""), credentialGeneration: "g1");

        now = now.AddMinutes(6);
        var afterWork = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Ready, afterWork.Status);
        Assert.NotEqual(ProviderAuthProbe.SignalSignedOut, afterWork.Signal);

        now = now.AddMinutes(6);
        var confirmed = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Unavailable, confirmed.Status);
    }

    [Fact]
    public async Task In_flight_real_request_cannot_replace_newer_run_failure_evidence()
    {
        var now = At;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true, clock: () => now, credentialFreshness: G1);
        probe.UseRealRequest(async (_, _, _) =>
        {
            entered.TrySetResult();
            return await release.Task;
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => now));

        var inFlight = probe.RefreshAsync("codex", CancellationToken.None);
        await entered.Task;
        now = now.AddSeconds(10);
        var run = probe.RecordProcessResult("codex", new ProcessResult(1, "", "HTTP 401 Unauthorized"));
        Assert.True(run.ProbeDegraded);

        release.SetResult(new ProcessResult(0, "OK", ""));
        var completed = await inFlight;

        Assert.Equal(ProviderProbeOutcome.Indeterminate, completed.Outcome);
        Assert.True(completed.ProbeDegraded);
        Assert.Null(completed.LastRealSuccessAt);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, probe.Current("codex").Outcome);
    }

    [Fact]
    public async Task Unexpected_status_feed_failure_stays_unknown_and_keeps_the_real_request_result()
    {
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            executableExists: _ => true, clock: () => At, credentialFreshness: G1);
        probe.UseRealRequest(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "HTTP 401 Unauthorized")),
            new ProviderStatusIncidentAdapter((_, _) => throw new IOException("socket closed"), () => At));

        var observed = await probe.RefreshAsync("codex", CancellationToken.None);

        Assert.Equal(ProviderProbeOutcome.Indeterminate, observed.Outcome);
        Assert.Equal("unauthorized", observed.EvidenceExcerpt);
        Assert.Equal("unauthorized", probe.Current("codex").EvidenceExcerpt);
    }

    [Fact]
    public async Task One_cancelled_caller_does_not_poison_the_cached_status_feed()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        var adapter = new ProviderStatusIncidentAdapter((_, ct) =>
        {
            Interlocked.Increment(ref fetches);
            return release.Task.WaitAsync(ct);
        }, () => At);
        using var cancelled = new CancellationTokenSource();
        var first = adapter.GetAsync("codex", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = adapter.GetAsync("codex", CancellationToken.None);
        release.SetResult("""
            {"incidents":[{"id":"incident-401","name":"Codex authentication errors",
            "created_at":"2026-09-26T11:55:00Z","resolved_at":null}]}
            """);
        var snapshot = await second;

        Assert.True(snapshot.Available);
        Assert.Equal("incident-401", Assert.Single(snapshot.Incidents).Id);
        Assert.Equal(1, fetches);
    }
}
