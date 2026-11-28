using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerModelAcceptanceProbeTests
{
    [Fact]
    public async Task Fable52_ConfiguredRouteGetsItsOwnHostAcceptanceEvidence()
    {
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((provider, model, _) =>
        {
            Assert.Equal("claude", provider);
            Assert.Equal("claude-fable-5-2", model);
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "host-login");
        await probe.RefreshAsync([("claude", "claude-fable-5-2")], CancellationToken.None);
        var evidence = Assert.Single(probe.Snapshot("claude", ["claude-fable-5-2"]));
        Assert.Equal("ready", evidence.Status);
        Assert.Equal("model-acceptance:claude:claude-fable-5-2", evidence.Key);
    }

    [Fact]
    public async Task Host_flight_contention_is_retried_on_the_next_refresh()
    {
        var busy = true;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(busy
                ? new ProcessResult(1, "", ProviderRealRequestHostFlight.InProgress)
                : new ProcessResult(0, "OK", "")),
            _ => "host-a-login");
        await probe.RefreshAsync([("claude", "claude-haiku-4-5")], CancellationToken.None);
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("claude", ["claude-haiku-4-5"])).Status);

        busy = false;
        await probe.RefreshAsync([("claude", "claude-haiku-4-5")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("claude", ["claude-haiku-4-5"])).Status);
    }

    [Fact]
    public async Task Capacity_marks_only_the_selected_model_until_its_retry_window()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 15, 37, 0, TimeSpan.Zero));
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")),
            _ => "host-a-login", clock.GetUtcNow);
        await probe.RefreshAsync([("codex", "gpt-5.6-sol")], CancellationToken.None);

        probe.RecordCapacity("codex", "gpt-6-sol");
        Assert.Equal("unavailable", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", ["gpt-5.6-sol"])).Status);
        clock.Advance(RunnerModelAcceptanceProbe.CapacityTtl.Add(TimeSpan.FromSeconds(1)));
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
    }

    [Fact]
    public void Model_unavailable_clears_on_catalogue_refresh_but_capacity_keeps_its_timer()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 15, 37, 0, TimeSpan.Zero));
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")),
            _ => "login-a", clock.GetUtcNow);
        probe.NoteCatalogueRefresh("codex", "catalogue-1");
        probe.RecordRejection("codex", "gpt-6-luna", accountRestriction: true);
        probe.NoteCatalogueRefresh("codex", "catalogue-1");
        Assert.Equal("unavailable", Assert.Single(probe.Snapshot("codex", ["gpt-6-luna"])).Status);
        probe.RecordCapacity("codex", "gpt-6-sol");
        probe.NoteCatalogueRefresh("codex", "catalogue-2");

        Assert.Equal("unknown", Assert.Single(probe.Snapshot("codex", ["gpt-6-luna"])).Status);
        Assert.Equal("unavailable", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exhausted_window_is_reprobed_after_a_new_usage_reading(bool resetMoved)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(100);
        var reset = now.AddDays(3).UtcDateTime;
        var calls = 0;
        var probe = new RunnerModelAcceptanceProbe();
        var refused = new ProcessResult(1, "", ProviderRequestRejectionClassifierTests.Refusal);
        var auth = new ProviderAuthProbe(executableExists: _ => true, clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "Fixture login."));
        auth.RecordRunStarted("codex");
        probe.Configure((_, _, _) => Task.FromResult(++calls == 1 ? refused : new ProcessResult(0, "ok", "")),
            _ => "login", () => now,
            (provider, result, retry) => auth.RecordProcessResult(provider, result, usageRetry: retry));
        const string model = "gpt-6.1-sol";
        probe.ObserveUsage("codex", "subscription", now.UtcDateTime, [new("Weekly", 100, reset, null)]);
        await probe.RefreshAsync([("codex", model)], default);
        var held = Assert.Single(probe.Snapshot("codex", [model]));
        Assert.Equal("unknown", held.Status);
        Assert.Contains("usage", held.Detail);
        Assert.Equal(ProviderAuthProbe.Limited, auth.Current("codex").Status);
        await probe.RefreshAsync([("codex", model)], default);
        Assert.Equal(1, calls);
        now = now.AddMinutes(1);
        Assert.True(probe.ObserveUsage("codex", "subscription", now.UtcDateTime,
            [new("Weekly", resetMoved ? 100 : 2, resetMoved ? reset.AddDays(7) : reset, null)]));
        auth.UsageWindowChanged("codex", now.UtcDateTime);
        Assert.Equal(ProviderAuthProbe.Ready, auth.Current("codex").Status);
        await probe.RefreshAsync([("codex", model)], default);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", [model])).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Real_launch_rejected_at_weekly_limit_rechecks_a_previously_accepted_model()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1795854300);
        var end = now.AddHours(2).UtcDateTime;
        const string provider = "codex", model = "gpt-6.1-sol";
        var calls = 0;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "ok", ""));
        }, _ => "login", () => now);
        await probe.RefreshAsync([(provider, model)], default);
        probe.ObserveUsage(provider, "login", now.UtcDateTime, [new("Weekly", 100, end, null)]);
        const string refusal = "You've hit your usage limit.";
        var access = ProviderAccessClassifier.ClassifyRun(1, "", refusal, observedAt: now);
        Assert.Equal(ProviderAccessEvidenceKind.RateLimited, access.Kind);
        Assert.Equal(new DateTimeOffset(end), probe.RecordQuota(provider, model, access));
        await probe.RefreshAsync([(provider, model)], default);
        Assert.Equal("unknown", Assert.Single(probe.Snapshot(provider, [model])).Status);
        Assert.Equal(1, calls);
        now = now.AddMinutes(1);
        probe.ObserveUsage(provider, "login", now.UtcDateTime, [new("Weekly", 0, end.AddDays(7), null)]);
        await probe.RefreshAsync([(provider, model)], default);
        Assert.Equal(2, calls);
        Assert.Equal("ready", Assert.Single(probe.Snapshot(provider, [model])).Status);
    }

    [Fact]
    public async Task Launch_refusal_expires_and_successful_work_can_recover_it_early()
    {
        var now = DateTimeOffset.UnixEpoch;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")), _ => "login", () => now);
        const string model = "gpt-6.1-sol";
        probe.RecordRejection("codex", model);
        now += RunnerModelAcceptanceProbe.LaunchRefusalTtl;
        await probe.RefreshAsync([("codex", model)], default);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", [model])).Status);
        probe.RecordRejection("codex", model);
        probe.RecordSuccess("codex", model, "login");
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", [model])).Status);
    }

    [Fact]
    public async Task Server_that_missed_a_launch_refusal_receives_acknowledgement_then_fresh_proof()
    {
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "ok", "")), _ => "login");
        const string model = "gpt-6.1-sol";
        probe.RecordRejection("codex", model);
        probe.RecordSuccess("codex", model, "login");
        var ready = Assert.Single(probe.Snapshot("codex", [model]));
        probe.AcknowledgeAdvertisement(ready, "unavailable");
        await probe.RefreshAsync([("codex", model)], default);
        var acknowledgement = Assert.Single(probe.Snapshot("codex", [model]));
        Assert.Equal("unknown", acknowledgement.Status);
        probe.AcknowledgeAdvertisement(acknowledgement, "unavailable");
        await probe.RefreshAsync([("codex", model)], default);
        var recovered = Assert.Single(probe.Snapshot("codex", [model]));
        Assert.Equal("ready", recovered.Status);
        Assert.True(recovered.ProbeSequence > acknowledgement.ProbeSequence);
    }

    [Fact]
    public async Task Both_gpt61_models_are_probed_once_and_advertised_ready_for_a_login()
    {
        var calls = new List<string>();
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, model, _) =>
        {
            calls.Add(model);
            return Task.FromResult(new ProcessResult(0,
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}", ""));
        }, _ => "login-a");
        (string Provider, string Model)[] candidates =
        [
            ("codex", CodexCliRelease.Gpt61Sol),
            ("codex", CodexCliRelease.Gpt61Terra),
        ];

        await probe.RefreshAsync(candidates, CancellationToken.None);
        await probe.RefreshAsync(candidates, CancellationToken.None);

        Assert.Equal([CodexCliRelease.Gpt61Sol, CodexCliRelease.Gpt61Terra], calls);
        Assert.All(probe.Snapshot("codex", candidates.Select(item => item.Model).ToArray()),
            capability => Assert.Equal("ready", capability.Status));
    }

    [Theory]
    [InlineData(CodexCliRelease.Gpt62Sol)]
    [InlineData(CodexCliRelease.Gpt62Astra)]
    public async Task Gpt62_route_gets_its_own_host_acceptance_evidence(string model)
    {
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((provider, probed, _) =>
        {
            Assert.Equal("codex", provider);
            Assert.Equal(model, probed);
            return Task.FromResult(new ProcessResult(0,
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}", ""));
        }, _ => "login-a");

        await probe.RefreshAsync([("codex", model)], CancellationToken.None);

        var evidence = Assert.Single(probe.Snapshot("codex", [model]));
        Assert.Equal("ready", evidence.Status);
        Assert.Equal($"model-acceptance:codex:{model}", evidence.Key);
    }

    [Fact]
    public async Task Rejection_is_cached_for_one_credential_generation_and_refreshed_after_login()
    {
        var generation = "login-a";
        var calls = 0;
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new ProcessResult(1, "", "HTTP 400 {\"error\":{\"message\":\"The 'gpt-5.5' model is not supported when using Codex with a ChatGPT account.\",\"type\":\"invalid_request_error\"}}")
                : new ProcessResult(0, "OK", ""));
        }, _ => generation, () => now);

        await probe.RefreshAsync([("codex", "gpt-5.5")], CancellationToken.None);
        Assert.Equal("unavailable", Assert.Single(probe.Snapshot("codex", ["gpt-5.5"])).Status);
        await probe.RefreshAsync([("codex", "gpt-5.5")], CancellationToken.None);
        now = now.AddDays(7);
        probe.ObserveUsage("codex", "account", now.UtcDateTime, [new("weekly", 100, null, null)]);
        probe.ObserveUsage("codex", "account", now.AddSeconds(1).UtcDateTime, [new("weekly", 0, null, null)]);
        await probe.RefreshAsync([("codex", "gpt-5.5")], default);
        Assert.Equal(1, calls);

        generation = "login-b";
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("codex", ["gpt-5.5"])).Status);
        await probe.RefreshAsync([("codex", "gpt-5.5")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", ["gpt-5.5"])).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Model_rejection_parser_requires_the_requested_model()
    {
        var result = new ProcessResult(1, "", "HTTP 400 {\"error\":{\"message\":\"The gpt-6-sol model is not supported when using Codex with a ChatGPT account\",\"type\":\"invalid_request_error\"}}");
        Assert.True(RunnerModelAcceptanceProbe.IsModelRejection(result, "gpt-6-sol"));
        Assert.False(RunnerModelAcceptanceProbe.IsModelRejection(result, "gpt-6-luna"));
    }

    [Fact]
    public async Task Launch_rejection_recorded_during_a_probe_wins_and_orders_later_evidence()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var probe = new RunnerModelAcceptanceProbe();
        var generation = "login-a";
        var racingRejection = true;
        probe.Configure((provider, model, _) =>
        {
            // The coding run's refusal lands while this probe is in flight.
            if (racingRejection) probe.RecordRejection(provider, model);
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => generation, () => now);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var held = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));
        Assert.Equal("unavailable", held.Status);

        racingRejection = false;
        generation = "login-b";
        var unverified = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var recovered = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));

        Assert.Equal("unknown", unverified.Status);
        Assert.Equal("ready", recovered.Status);
        Assert.True(recovered.ProbeSequence > unverified.ProbeSequence);
        Assert.True(unverified.ProbeSequence >= held.ProbeSequence);
    }

    [Fact]
    public void Operator_configured_routes_outside_the_built_in_list_are_probe_candidates()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:5031",
            RunnerId = "coding-a",
            RunnerName = "coding-a",
            Hostname = "host-a",
            BackendName = "test",
            Role = "coding",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CodexCliBin = Path.Combine(Path.GetTempPath(), $"missing-codex-{Guid.NewGuid():N}"),
            ClaudeCliBin = Path.Combine(Path.GetTempPath(), $"missing-claude-{Guid.NewGuid():N}"),
        };
        var probe = new RunnerModelAcceptanceProbe();
        probe.UseConfiguredModels(
        [
            new ConfiguredModelRouteDto("Codex", " gpt-7-sol "),
            new ConfiguredModelRouteDto("claude", "claude-opus-6[1m]"),
            new ConfiguredModelRouteDto("codex", "../escape"),
            new ConfiguredModelRouteDto("codex", "gpt 6"),
            new ConfiguredModelRouteDto("gemini", "gemini-3-pro"),
        ]);

        var candidates = RunnerCapabilityProbe.ModelCandidates(options, probe.ConfiguredModels);

        Assert.Equal(("codex", "gpt-7-sol"), candidates[0]);
        Assert.Equal(("claude", "claude-opus-6[1m]"), candidates[1]);
        Assert.Contains(("codex", "gpt-7-sol"), candidates);
        Assert.Contains(("claude", "claude-opus-6[1m]"), candidates);
        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, item => item.Model.Contains("escape", StringComparison.Ordinal)
                                                  || item.Model.Contains(' ') || item.Provider == "gemini");
    }

    [Fact]
    public void Review_daemon_with_default_routes_probes_only_routed_models()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:5031",
            RunnerId = "review-a",
            RunnerName = "review-a",
            Hostname = "host-a",
            BackendName = "test",
            Role = "review",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            // Isolate routed-model selection from the installed CLI's version-gated models.
            CodexCliBin = Path.Combine(Path.GetTempPath(), "absent-codex-" + Guid.NewGuid().ToString("N")),
            ClaudeCliBin = Path.Combine(Path.GetTempPath(), "absent-claude-" + Guid.NewGuid().ToString("N")),
        };
        (string Provider, string Model)[] routes =
            [("codex", "gpt-5.4-mini"), ("codex", "gpt-6-sol")];

        Assert.Equal(routes, RunnerCapabilityProbe.ModelCandidates(options, routes));
    }

    [Fact]
    public async Task Inconclusive_probe_retries_after_bounded_backoff()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var calls = 0;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(1, "", "network unavailable"));
        }, _ => "login-a", () => now);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        Assert.Equal(1, calls);

        now = now.AddMinutes(11);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Session_bound_ready_verdict_is_rechecked_after_six_hours()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var calls = 0;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "daemon-session:test", () => now);

        await probe.RefreshAsync([("claude", "claude-opus-5-5")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("claude", ["claude-opus-5-5"])).Status);
        now = now.AddHours(6).AddMinutes(-1);
        await probe.RefreshAsync([("claude", "claude-opus-5-5")], CancellationToken.None);
        Assert.Equal(1, calls);
        now = now.AddMinutes(1);
        await probe.RefreshAsync([("claude", "claude-opus-5-5")], CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Completed_login_invalidates_a_ready_model_verdict()
    {
        var probe = new RunnerModelAcceptanceProbe();
        var calls = 0;
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "daemon-session:test");

        await probe.RefreshAsync([("claude", "claude-opus-5-5")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("claude", ["claude-opus-5-5"])).Status);
        probe.Invalidate("claude");
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("claude", ["claude-opus-5-5"])).Status);
        await probe.RefreshAsync([("claude", "claude-opus-5-5")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("claude", ["claude-opus-5-5"])).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Login_change_during_a_probe_discards_the_old_login_result()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure(async (_, _, _) =>
        {
            started.SetResult();
            return await release.Task;
        }, _ => "daemon-session:test");

        var refresh = probe.RefreshAsync([("claude", "claude-opus-5-5")], CancellationToken.None);
        await started.Task;
        probe.Invalidate("claude");
        release.SetResult(new ProcessResult(0, "OK", ""));
        await refresh;

        Assert.Equal("unknown", Assert.Single(probe.Snapshot("claude", ["claude-opus-5-5"])).Status);
    }
}
