using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// I9 typed runbook executor. Fixtures under testdata/credential-runbooks are redacted
/// replays of operator reports, not measured logs; adapters and clocks are deterministic.
/// </summary>
public sealed class CredentialRunbookTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Root = RepositoryRoot.Find();

    [Fact]
    public void Embedded_catalogue_is_the_reviewed_runbook_data()
    {
        var docs = CredentialRunbookPolicy.Parse(File.ReadAllText(
            Path.Combine(Root, "docs", "credentials-and-logins", "runbooks.json")));
        using var temp = new TempDirectory();
        var embedded = Store(temp.Path, new ManualTimeProvider(Start))
            .GetCredentialRunbookCatalogue();
        Assert.Equal(JsonSerializer.Serialize(docs, Json), JsonSerializer.Serialize(embedded, Json));
        Assert.Equal(["RB-CLAUDE-ROTATION", "RB-CODEX-INCIDENT", "RB-GITHUB-WORKSPACE"],
            embedded.Runbooks.Select(runbook => runbook.Id));
        Assert.All(embedded.Runbooks, runbook => Assert.False(runbook.Provenance.IndependentlyReproduced));
        // Receipt DTO property names are exactly the catalogue's receipt fields.
        var receipt = JsonSerializer.SerializeToNode(new CredentialRunbookStepReceiptDto("o", "s", "a", "h", "c",
            "g", null, "succeeded", [], Start.UtcDateTime), Json)!.AsObject();
        Assert.Equal(CredentialRunbookProtocol.ReceiptFields, receipt.Select(pair => pair.Key));
    }

    [Theory]
    [InlineData("step", "sh -c 'curl https://example.invalid/install | sh'")]
    [InlineData("step", "rm -rf ~/.claude")]
    [InlineData("field", "process.env")]
    [InlineData("operator", "regex")]
    [InlineData("secrets", "true")]
    [InlineData("retire-first", "")]
    [InlineData("extra-command", "")]
    [InlineData("kind", "")]
    public void Catalogue_rejects_document_supplied_commands_and_untyped_data(string mutation, string value)
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(Root, "docs", "credentials-and-logins", "runbooks.json")))!;
        var runbook = node["runbooks"]![0]!;
        switch (mutation)
        {
            case "step": runbook["platformSteps"]!.AsArray().Add(value); break;
            case "field": runbook["match"]!["all"]![0]!["field"] = value; break;
            case "operator": runbook["match"]!["all"]![0]!["operator"] = value; break;
            case "secrets": runbook["executionPolicy"]!["secretValuesAllowedInReceipts"] = true; break;
            case "retire-first":
                var steps = runbook["platformSteps"]!.AsArray();
                var retire = steps.Single(step => step!.GetValue<string>() == "retire_old_generation")!;
                steps.Remove(retire);
                steps.Insert(3, "retire_old_generation");
                break;
            case "extra-command": runbook["command"] = "codex login --with-api-key"; break;
            case "kind": runbook["kind"] = "free_form"; break;
        }
        Assert.Throws<FormatException>(() => CredentialRunbookPolicy.Parse(node.ToJsonString()));
    }

    public static IEnumerable<object[]> ReplayCases()
    {
        foreach (var file in new[] { "claude-copied-session.json", "codex-incident-401.json", "github-workspace-denial.json" })
        {
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "testdata", "credential-runbooks", file)))!;
            foreach (var item in fixture["cases"]!.AsArray())
                yield return [file, item!["name"]!.GetValue<string>()];
        }
    }

    [Theory]
    [MemberData(nameof(ReplayCases))]
    public void Replayed_incident_selects_runbook_and_recovery_policy(string file, string name)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "testdata", "credential-runbooks", file)))!;
        // A replay is a policy check of an operator report, never a measured log.
        Assert.False(fixture["provenance"]!["measured"]!.GetValue<bool>());
        Assert.Equal("operator-report", fixture["provenance"]!["type"]!.GetValue<string>());
        var item = fixture["cases"]!.AsArray().Single(entry => entry!["name"]!.GetValue<string>() == name)!;
        var facts = ReplayFacts(fixture, item);
        var expected = item["expected"]!;

        var selection = CredentialRunbookPolicy.Select(
            CredentialRunbookPolicy.Parse(File.ReadAllText(
                Path.Combine(Root, "docs", "credentials-and-logins", "runbooks.json"))), facts);

        Assert.Equal(expected["mode"]!.GetValue<string>(), selection.Mode);
        Assert.Equal(expected["runbookId"]?.GetValue<string>(), selection.RunbookId);
        Assert.Equal(expected["operatorItem"]!.GetValue<string>(), selection.Policy.OperatorItem);
        Assert.Equal(expected["holdScope"]!.GetValue<string>(), selection.Policy.HoldScope);
        Assert.Equal(expected["humanBoundaries"]!.AsArray().Select(value => value!.GetValue<string>()),
            selection.Policy.HumanBoundaries);
        Assert.Equal(expected["signInAdvice"]!.GetValue<bool>(), selection.Policy.SignInAdvice);
        Assert.Equal(expected["rotation"]!.GetValue<bool>(), selection.Policy.CredentialRotationAllowed);
        if (expected["retrySeconds"] is JsonArray retry)
            Assert.Equal(retry.Select(value => value!.GetValue<int>()), selection.Policy.RetrySeconds);
    }

    [Fact]
    public async Task Codex_incident_replay_holds_the_cohort_without_an_item_and_resumes_after_canary()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "codex-login", "codex_chatgpt_login", "codex", "native-cli-store");
        var key = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAsync(store, clock, "runner-a", "instance-a", "host-a", CapabilityProtocol.CodingExecutor);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseAsync(store, clock, "runner-a", "instance-a", 2, CapabilityProtocol.CodingExecutor, key,
            "provider_incident", "generation-a", "native-cli-store",
            evidenceId: "fixture-incident-1", excerpt: "unauthorized:service-account-shaped");

        var run = await store.BeginCredentialRunbookAsync(Begin("host-a", "codex-login", "diagnose",
            new(RequestNormalizedCode: "unauthorized", IncidentCorroboration: "official-applicable")), "operator", default);

        Assert.Equal("RB-CODEX-INCIDENT", run.Operation!.RunbookId);
        Assert.Equal(CredentialRunbookStates.AwaitingRecovery, run.Operation.State);
        Assert.Equal("verify_recovery_canary", run.Operation.CurrentStep);
        Assert.Equal("incident:codex:fixture-incident-1", run.Operation.IncidentRef);
        Assert.Equal(["discover_effective_source", "verify_real_request", "correlate_incident",
            "pause_affected_claims", "schedule_bounded_retry"], run.Receipts.Select(receipt => receipt.StepId));
        Assert.All(run.Receipts, receipt => Assert.Equal("generation-a", receipt.ExpectedGeneration));
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
        Assert.Equal(0, await CountAsync(temp.Path, "provider_renewal_operations"));

        // A healthy answer that is not the scheduled canary leaves the hold and the operation waiting.
        clock.Advance(TimeSpan.FromSeconds(70));
        Assert.True((await store.ReserveProviderCanaryAsync("runner-a", "instance-a", "codex", default)).Allowed);
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("diagnose"), "operator", default);
        Assert.Equal(CredentialRunbookStates.AwaitingRecovery, run.Operation!.State);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseAsync(store, clock, "runner-a", "instance-a", 3, CapabilityProtocol.CodingExecutor, key,
            "healthy", "generation-a", "native-cli-store", success: clock.GetUtcNow().UtcDateTime);
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("diagnose"), "operator", default);

        Assert.Equal(CredentialRunbookStates.Complete, run.Operation!.State);
        Assert.Equal(store.GetCredentialRunbookCatalogue().Runbooks.Single(book => book.Id == "RB-CODEX-INCIDENT").PlatformSteps,
            run.Receipts.Select(receipt => receipt.StepId));
        Assert.Equal("generation-a", run.Receipts.Single(receipt => receipt.StepId == "advertise_generation").ObservedGeneration);
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
        Assert.Equal(0, await CountAsync(temp.Path, "provider_renewal_operations"));
    }

    [Fact]
    public async Task Uncorroborated_401_selects_no_runbook_and_starts_nothing()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "codex-login", "codex_chatgpt_login", "codex", "native-cli-store");

        var run = await store.BeginCredentialRunbookAsync(Begin("host-a", "codex-login", "repair",
            new(RequestNormalizedCode: "unauthorized", IncidentCorroboration: "none")), "operator", default);

        Assert.Equal(CredentialRunbookModes.None, run.Selection.Mode);
        Assert.False(run.Selection.Policy.SignInAdvice);
        Assert.Null(run.Operation);
        Assert.Equal(0, await CountAsync(temp.Path, "credential_runbook_operations"));
    }

    [Fact]
    public async Task Unknown_predicate_is_diagnosis_only_and_never_reserves_a_login()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "claude-env", "claude_oauth_token", "claude", "environment-file");
        await RegisterAsync(store, clock, "runner-a", "instance-a", "host-a", CapabilityProtocol.CodingExecutor);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseAsync(store, clock, "runner-a", "instance-a", 2, CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.ProviderAuthentication("claude"), "credential_invalid", "generation-a", "environment-file");

        var run = await store.BeginCredentialRunbookAsync(Begin("host-a", "claude-env", "repair",
            new(ProbeOutcome: "credential_invalid")), "operator", default);

        Assert.Equal(CredentialRunbookModes.DiagnosisOnly, run.Operation!.SelectionMode);
        Assert.Equal(CredentialRunbookStates.DiagnosisOnly, run.Operation.State);
        Assert.Equal("begin_host_login", run.Operation.CurrentStep);
        Assert.Equal(["evidence.refreshSessionSharedAcrossHosts"], run.Selection.UnknownFields);
        Assert.Equal(0, await CountAsync(temp.Path, "provider_renewal_operations"));
    }

    [Fact]
    public async Task Facts_that_contradict_the_registry_or_a_stale_generation_cannot_start()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "claude-env", "claude_oauth_token", "claude", "environment-file");

        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.BeginCredentialRunbookAsync(
            Begin("host-a", "claude-env", "repair", new(CredentialKind: "codex_chatgpt_login",
                RequestNormalizedCode: "unauthorized", IncidentCorroboration: "official-applicable")), "operator", default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.BeginCredentialRunbookAsync(
            Begin("host-a", "claude-env", "repair", ClaudeFacts) with { ExpectedGeneration = "generation-old" },
            "operator", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.BeginCredentialRunbookAsync(
            Begin("host-a", "claude-env", "repair", ClaudeFacts) with { EvidenceRefs = [string.Concat("evidence:", "sk", "-ant-x")] },
            "operator", default));
        Assert.Equal(0, await CountAsync(temp.Path, "credential_runbook_operations"));
    }

    [Theory]
    [InlineData("one-box", "agent-host.service")]
    [InlineData("distributed", "agent-runner.service")]
    public async Task Claude_rotation_drill_pauses_for_consent_and_retires_only_after_two_unit_proof(
        string topology, string codingUnit)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var host = topology + "-host";
        var key = CapabilityProtocol.ProviderAuthentication("claude");
        await SeedCredentialAsync(store, clock, host, "claude-env", "claude_oauth_token", "claude", "environment-file",
            source: "instance-coding");
        await RegisterAsync(store, clock, "coding", "instance-coding", host, CapabilityProtocol.CodingExecutor);
        await RegisterAsync(store, clock, "review", "instance-review", host, CapabilityProtocol.ReviewExecutor);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseAsync(store, clock, "coding", "instance-coding", 2, CapabilityProtocol.CodingExecutor, key,
            "credential_invalid", "generation-a", "environment-file");

        var run = await store.BeginCredentialRunbookAsync(Begin(host, "claude-env", "diagnose", ClaudeFacts),
            "operator", default);
        Assert.Equal(CredentialRunbookStates.AwaitingRepair, run.Operation!.State);
        Assert.Equal(0, await CountAsync(temp.Path, "provider_renewal_operations"));

        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);
        Assert.Equal(CredentialRunbookStates.AwaitingHuman, run.Operation!.State);
        Assert.Equal(CredentialRunbookHumanBoundaries.BrowserConsent, run.Operation.Detail);
        var renewalKey = run.Operation.LinkedRenewalKey!;
        // The browser step is explicit: an operator cannot assert a Task Server-owned step.
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RecordCredentialRunbookStepAsync(
            run.Operation.OperationId, new("begin_host_login", "succeeded", null, []), "operator", default));

        // The existing sign-in flow attaches with the same key and gets the same reservation.
        var renewal = await store.BeginProviderRenewalAsync(new(Installation, host, "claude-env", "generation-a",
            "R1", renewalKey, Start.UtcDateTime.AddMinutes(10)), "operator", default);
        Assert.Equal(run.Operation.LinkedRenewalOperationId, renewal.OperationId);
        Assert.Equal(1, await CountAsync(temp.Path, "provider_renewal_operations"));
        foreach (var step in new[] { "preflight", "awaiting-human", "staged", "installed" })
            await store.AdvanceProviderRenewalAsync(renewal.OperationId, new(step, null, null, [], false, []),
                "operator", default);
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);
        Assert.Equal(CredentialRunbookStates.AwaitingHost, run.Operation!.State);
        Assert.Equal("reconcile_units_safely", run.Operation.CurrentStep);

        clock.Advance(TimeSpan.FromSeconds(30));
        await AdvertiseAsync(store, clock, "coding", "instance-coding", 3, CapabilityProtocol.CodingExecutor, key,
            "healthy", "generation-b", "environment-file", success: clock.GetUtcNow().UtcDateTime);
        await AdvertiseAsync(store, clock, "review", "instance-review", 3, CapabilityProtocol.ReviewExecutor, key,
            "healthy", "generation-b", "environment-file", success: clock.GetUtcNow().UtcDateTime);
        await store.AdvanceProviderRenewalAsync(renewal.OperationId, new("verified", "generation-b", "environment-file",
            [codingUnit, "agent-runner-review.service"], true, ["evidence:real-request-coding"]), "operator", default);
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);
        // Proof is recorded, but the old generation is not retired until the host retires it.
        Assert.Equal("retire_old_generation", run.Operation!.CurrentStep);
        Assert.DoesNotContain(run.Receipts, receipt => receipt.StepId == "retire_old_generation" &&
            receipt.Outcome == CredentialRunbookStepOutcomes.Succeeded);

        await store.AdvanceProviderRenewalAsync(renewal.OperationId, new("retired", null, null, [], false, []), "operator", default);
        await store.AdvanceProviderRenewalAsync(renewal.OperationId, new("complete", null, null, [], false, []), "operator", default);
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);

        Assert.Equal(CredentialRunbookStates.Complete, run.Operation!.State);
        Assert.Equal(9, run.Receipts.Count);
        Assert.All(run.Receipts, receipt =>
        {
            Assert.Equal(run.Operation.OperationId, receipt.OperationId);
            Assert.Equal(host, receipt.HostId);
            Assert.Equal("claude-env", receipt.CredentialId);
            Assert.Equal("generation-a", receipt.ExpectedGeneration);
            Assert.Equal(CredentialRunbookStepOutcomes.Succeeded, receipt.Outcome);
        });
        Assert.Equal("generation-b", run.Receipts.Single(receipt => receipt.StepId == "verify_real_request").ObservedGeneration);
        Assert.Equal("generation-b", Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
        Assert.Equal(1, await CountAsync(temp.Path, "provider_renewal_operations"));
        await WriteDrillEvidenceAsync(temp.Path, topology, run);
    }

    [Fact]
    public async Task Failed_install_rolls_back_to_the_proved_prior_generation_as_bounded_recovery()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "claude-env", "claude_oauth_token", "claude", "environment-file",
            source: "instance-coding");
        await RegisterAsync(store, clock, "coding", "instance-coding", "host-a", CapabilityProtocol.CodingExecutor);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseAsync(store, clock, "coding", "instance-coding", 2, CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.ProviderAuthentication("claude"), "credential_invalid", "generation-a", "environment-file");
        var run = await store.BeginCredentialRunbookAsync(Begin("host-a", "claude-env", "repair", ClaudeFacts),
            "operator", default);
        var renewalId = run.Operation!.LinkedRenewalOperationId!;
        foreach (var step in new[] { "preflight", "awaiting-human", "staged", "installed" })
            await store.AdvanceProviderRenewalAsync(renewalId, new(step, null, null, [], false, []), "operator", default);
        clock.Advance(TimeSpan.FromMinutes(16));
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);
        Assert.Equal(CredentialRunbookStates.AwaitingHost, run.Operation!.State);
        Assert.Equal("provider-renewal-recovery-required", run.Operation.Detail);

        await store.AdvanceProviderRenewalAsync(renewalId, new("cancelled", "generation-a", "environment-file",
            ["agent-runner.service", "agent-runner-review.service"], true, []), "operator", default);
        run = await store.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);

        Assert.Equal(CredentialRunbookStates.Failed, run.Operation!.State);
        Assert.Equal("prior-generation-restored", run.Operation.Detail);
        Assert.Equal("generation-a", Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
        Assert.DoesNotContain(run.Receipts, receipt => receipt.StepId == "retire_old_generation");
    }

    [Fact]
    public async Task Workspace_repair_keeps_admin_registration_explicit_and_refuses_unproved_retirement()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "workspace-key", "github_deploy_key", "github",
            "ssh-private-key", repositoryPurpose: "workspace");
        var run = await store.BeginCredentialRunbookAsync(Begin("host-a", "workspace-key", "repair",
            new(RepositoryRequiredAccessVerified: false)), "operator", default);
        var id = run.Operation!.OperationId;
        Assert.Equal("RB-GITHUB-WORKSPACE", run.Operation.RunbookId);
        Assert.Equal(("probe_exact_origin", CredentialRunbookStates.AwaitingHost),
            (run.Operation.CurrentStep!, run.Operation.State));

        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RecordCredentialRunbookStepAsync(
            id, new("retire_old_generation", "succeeded", null, []), "operator", default));
        await Record("probe_exact_origin", "succeeded", null, "evidence:exact-origin-denied");
        Assert.Equal("generate_host_key", (await store.GetCredentialRunbookAsync(id, default))!.Operation!.CurrentStep);
        await Record("generate_host_key", "started", null);
        await Record("generate_host_key", "started", null);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordCredentialRunbookStepAsync(id,
            new("generate_host_key", "succeeded", string.Concat("gh", "p_", "fixture0value"), []), "host", default));
        await Record("generate_host_key", "succeeded", "key-generation-b", "evidence:public-fingerprint");
        Assert.Equal("runbook-receipt-conflict", (await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RecordCredentialRunbookStepAsync(id, new("generate_host_key", "succeeded", "key-generation-c", []),
                "host", default))).Code);
        run = (await store.GetCredentialRunbookAsync(id, default))!;
        Assert.Equal((CredentialRunbookStates.AwaitingHuman, CredentialRunbookHumanBoundaries.RepositoryAdministration),
            (run.Operation!.State, run.Operation.Detail));

        await Record("register_public_key", "succeeded", "key-generation-b", "evidence:deploy-key-registered", actor: "repo-admin");
        // A fetch with the old identity is not proof of the new key.
        await Record("verify_repository_access", "succeeded", "generation-a", "evidence:fetch-exact-origin");
        await Record("switch_repository_identity", "succeeded", "key-generation-b");
        run = (await store.GetCredentialRunbookAsync(id, default))!;

        Assert.Equal(CredentialRunbookStates.RecoveryRequired, run.Operation!.State);
        Assert.Equal("unverified-generation", run.Operation.Detail);
        Assert.DoesNotContain(run.Receipts, receipt => receipt.StepId == "retire_old_generation");
        Assert.Equal(1, run.Receipts.Count(receipt => receipt.StepId == "generate_host_key"));
        Assert.Equal("repo-admin", run.Receipts.Single(receipt => receipt.StepId == "register_public_key").ActorId);

        Task Record(string step, string outcome, string? generation, string? evidence = null, string actor = "host")
            => store.RecordCredentialRunbookStepAsync(id,
                new(step, outcome, generation, evidence is null ? [] : [evidence]), actor, default);
    }

    /// <summary>
    /// Crash after every durable write and after every side effect, then restart from
    /// the journal. Each step keeps one receipt, the host issues and retires once, and
    /// the secret held by the fake host never reaches the store.
    /// </summary>
    [Fact]
    public async Task Crash_at_every_side_effect_boundary_resumes_with_one_receipt_and_no_leak()
    {
        var crashPoints = 0;
        for (var crashAt = 1; ; crashAt++)
        {
            using var temp = new TempDirectory();
            var clock = new ManualTimeProvider(Start);
            var host = new FakeHost();
            var store = Store(temp.Path, clock);
            await store.InitializeAsync();
            await SeedCredentialAsync(store, clock, "host-a", "claude-env", "claude_oauth_token", "claude", "environment-file");
            var writes = 0;
            var crashed = false;
            store.RunbookAdapterOverride = _ => host.Adapters(() => ++writes == crashAt && (crashed = true));
            store.RunbookJournalDecorator = inner => new CrashingJournal(inner, () => ++writes == crashAt && (crashed = true));
            var error = await Record.ExceptionAsync(() => store.BeginCredentialRunbookAsync(
                Begin("host-a", "claude-env", "repair", ClaudeFacts), "operator", default));
            if (!crashed)
            {
                Assert.Null(error);
                break;
            }
            Assert.IsType<SimulatedCrash>(error);
            crashPoints++;

            var restarted = Store(temp.Path, clock);
            await restarted.InitializeAsync();
            restarted.RunbookAdapterOverride = _ => host.Adapters(() => false);
            var run = await restarted.BeginCredentialRunbookAsync(Begin("host-a", "claude-env", "repair", ClaudeFacts),
                "operator", default);
            run = await restarted.BeginCredentialRunbookAsync(Begin("host-a", "claude-env", "repair", ClaudeFacts),
                "operator", default);

            Assert.True(CredentialRunbookStates.Complete == run.Operation!.State, $"crash point {crashAt}: {run.Operation.State}");
            Assert.Equal(9, await CountAsync(temp.Path, "credential_runbook_receipts"));
            Assert.Equal(1, await CountAsync(temp.Path, "credential_runbook_operations"));
            Assert.All(host.SideEffects.GroupBy(effect => effect), group => Assert.Single(group));
            Assert.True(host.SideEffects.IndexOf("retire") > host.SideEffects.IndexOf("verify"),
                $"crash point {crashAt}: retired before verification");
            Assert.False(StoreContains(temp.Path, host.Secret), $"crash point {crashAt}: secret reached the store");

            // A late retry of a finished step never replaces its final receipt.
            ICredentialRunbookJournal? journal = null;
            restarted.RunbookJournalDecorator = inner => journal = inner;
            await restarted.ContinueCredentialRunbookAsync(run.Operation.OperationId, new("repair"), "operator", default);
            var retired = run.Receipts.Single(receipt => receipt.StepId == "retire_old_generation");
            Assert.Equal(JsonSerializer.Serialize(retired, Json), JsonSerializer.Serialize(
                await journal!.RecordReceiptAsync(run.Operation,
                    retired with { Outcome = CredentialRunbookStepOutcomes.Started }, default), Json));
            await Assert.ThrowsAsync<TaskServerConflictException>(() => journal.RecordReceiptAsync(run.Operation,
                retired with { Outcome = CredentialRunbookStepOutcomes.Failed }, default));
        }
        Assert.True(crashPoints >= 15, $"Only {crashPoints} crash points were exercised.");
    }

    [Fact]
    public async Task Diagnosis_begin_retry_after_repair_upgrade_keeps_the_same_operation()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "claude-env", "claude_oauth_token", "claude", "environment-file");
        store.RunbookAdapterOverride = _ => new FakeHost().Adapters(() => false);
        var request = Begin("host-a", "claude-env", "diagnose", ClaudeFacts);
        var diagnosed = await store.BeginCredentialRunbookAsync(request, "operator", default);
        Assert.Equal(CredentialRunbookStates.AwaitingRepair, diagnosed.Operation!.State);
        await store.ContinueCredentialRunbookAsync(diagnosed.Operation.OperationId, new("repair"), "operator", default);
        var retried = await store.BeginCredentialRunbookAsync(request, "operator", default);
        Assert.Equal(diagnosed.Operation.OperationId, retried.Operation!.OperationId);
        Assert.Equal(CredentialRunbookStates.Complete, retried.Operation.State);
        Assert.Equal("repair", retried.Operation.Intent);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.BeginCredentialRunbookAsync(
            request with { Intent = "repair" }, "operator", default));
    }

    [Fact]
    public async Task Evidence_accepts_task_identifiers_but_rejects_provider_token_prefixes()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "claude-env", "claude_oauth_token", "claude", "environment-file");
        var request = Begin("host-a", "claude-env", "diagnose", ClaudeFacts);
        var run = await store.BeginCredentialRunbookAsync(request with { EvidenceRefs = ["evidence:task-2979"] },
            "operator", default);
        Assert.Contains("evidence:task-2979", run.Operation!.EvidenceRefs);
        foreach (var prefix in new[] { string.Concat("s", "k-"), string.Concat("gh", "p_"), string.Concat("gh", "o_"),
                     string.Concat("gh", "s_"), string.Concat("github", "_pat_"), string.Concat("xox", "b-") })
            await Assert.ThrowsAsync<ArgumentException>(() => store.BeginCredentialRunbookAsync(
                request with { EvidenceRefs = ["evidence:" + prefix + Guid.NewGuid().ToString("N")] }, "operator", default));
    }

    [Fact]
    public async Task Stale_journal_update_and_receipt_cannot_overwrite_newer_operation_state()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedCredentialAsync(store, clock, "host-a", "workspace-key", "github_deploy_key", "github",
            "ssh-private-key", repositoryPurpose: "workspace");
        ICredentialRunbookJournal? journal = null;
        store.RunbookJournalDecorator = inner => journal = inner;
        var run = await store.BeginCredentialRunbookAsync(Begin("host-a", "workspace-key", "repair",
            new(RepositoryRequiredAccessVerified: false)), "operator", default);
        var original = run.Operation!;
        // Fake time does not advance: concurrency must be fenced by state, not timestamp alone.
        await journal!.UpdateOperationAsync(original with { Detail = "newer-state" }, default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => journal.UpdateOperationAsync(
            original with { State = CredentialRunbookStates.Failed }, default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => journal.RecordReceiptAsync(original,
            new(original.OperationId, "probe_exact_origin", "host", original.HostId, original.CredentialId,
                original.ExpectedGeneration, null, "succeeded", [], clock.GetUtcNow().UtcDateTime), default));
        var current = (await store.GetCredentialRunbookAsync(original.OperationId, default))!;
        Assert.Equal("newer-state", current.Operation!.Detail);
        Assert.DoesNotContain(current.Receipts, receipt => receipt.StepId == "probe_exact_origin");
    }

    [Fact]
    public async Task Cancel_route_is_idempotent_and_releases_only_a_binding_without_custody_effects()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var factory = new RunbookApiFactory(temp.Path, clock);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "runbook-test");
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await SeedCredentialAsync(store, clock, "host-a", "workspace-key", "github_deploy_key", "github",
            "ssh-private-key", repositoryPurpose: "workspace");
        var request = Begin("host-a", "workspace-key", "repair", new(RepositoryRequiredAccessVerified: false));
        var run = await store.BeginCredentialRunbookAsync(request, "operator", default);
        var id = run.Operation!.OperationId;
        var route = $"/api/v1/management/credential-runbook-operations/{id}/cancel";
        var response = await client.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = (await response.Content.ReadFromJsonAsync<CredentialRunbookRunDto>(Json))!;
        Assert.Equal("cancelled", cancelled.Operation!.State);
        var retry = await client.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(cancelled, Json),
            JsonSerializer.Serialize(await retry.Content.ReadFromJsonAsync<CredentialRunbookRunDto>(Json), Json));
        Assert.Equal("cancelled", (await store.ContinueCredentialRunbookAsync(id, new("repair"), "operator", default)).Operation!.State);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RecordCredentialRunbookStepAsync(id,
            new("probe_exact_origin", "succeeded", null, []), "host", default));
        var replacement = await store.BeginCredentialRunbookAsync(request with { IdempotencyKey = "replacement" }, "operator", default);
        Assert.NotEqual(id, replacement.Operation!.OperationId);
        id = replacement.Operation.OperationId;
        await store.RecordCredentialRunbookStepAsync(id, new("probe_exact_origin", "succeeded", null, []), "host", default);
        // Even before a host submits 'started', authorizing the custody step makes cancellation unsafe.
        var refused = await client.PostAsync($"/api/v1/management/credential-runbook-operations/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("generate_host_key", (await store.GetCredentialRunbookAsync(id, default))!.Operation!.CurrentStep);
    }

    [Theory]
    [InlineData("awaiting-repair", "begin_host_login", false, null, true)]
    [InlineData("awaiting-host", "probe_exact_origin", false, "discover_repository_binding", true)]
    [InlineData("awaiting-host", "generate_host_key", false, null, false)]
    [InlineData("awaiting-human", "register_public_key", false, null, false)]
    [InlineData("requested", null, true, null, false)]
    [InlineData("requested", null, false, "install_generation", false)]
    [InlineData("requested", null, false, "begin_host_login", false)]
    public void Cancellation_policy_requires_no_dispatched_custody_work(
        string state, string? step, bool linked, string? receiptStep, bool allowed)
        => Assert.Equal(allowed, CredentialRunbookPolicy.CanCancel(state, step, linked,
            receiptStep is null ? [] : [receiptStep]));

    private sealed class RunbookApiFactory(string dataDirectory, TimeProvider clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                }));
            builder.ConfigureServices(services => services.AddSingleton(clock));
        }
    }

    private static readonly CredentialIncidentFacts ClaudeFacts =
        new(RefreshSessionSharedAcrossHosts: true, ProbeOutcome: "credential_invalid");

    private const string Installation = "installation";

    private static BeginCredentialRunbookRequest Begin(string host, string credential, string intent,
        CredentialIncidentFacts facts)
        => new(Installation, host, credential, "generation-a", "operator-key-1", intent, facts);

    private static CredentialIncidentFacts ReplayFacts(JsonNode fixture, JsonNode item)
    {
        var facts = item["facts"].Deserialize<CredentialIncidentFacts>(Json)!;
        var registry = fixture["registry"]!;
        return facts with
        {
            CredentialKind = registry["kind"]!.GetValue<string>(),
            RepositoryPurpose = facts.RepositoryPurpose ??
                item["registryOverride"]?["repositoryPurpose"]?.GetValue<string>() ??
                registry["repositoryPurpose"]?.GetValue<string>(),
        };
    }

    private static TaskServerStore Store(string path, TimeProvider clock)
        => new(Options.Create(new TaskServerOptions { DataDirectory = path }), clock);

    private static Task SeedCredentialAsync(TaskServerStore store, ManualTimeProvider clock, string host,
        string credential, string kind, string provider, string adapter, string source = "instance-seed",
        string? repositoryPurpose = null)
    {
        var unknown = new Dictionary<string, string>();
        foreach (var field in new[] { "createdAt", "discoveredAt", "lastVerifiedAt", "lastRenewedAt",
                     "expiresAt", "rotationDueAt", "accessTokenExpiresAt", "lastRealSuccessAt", "nextProbeAt" })
            unknown[field] = "not-observed";
        var record = new CredentialRegistryRecordDto(Installation, host, credential, provider,
            "owner", "recovery-owner", "generation-a", CredentialRegistryProtocol.RenewalMethods[kind], 1, kind, null,
            ["scope:host"], [new("coding", "execution", "source:fixture")],
            new(adapter, "host-local-reference", "active"), "unknown", unknown,
            null, null, null, null, null, "not_verified", [],
            null, null, null, null, null, null, null, null, null, RepositoryPurpose: repositoryPurpose);
        return store.UpsertCredentialRegistryAsync(new(record, source, null, clock.GetUtcNow().UtcDateTime), "test", default);
    }

    private static Task RegisterAsync(TaskServerStore store, ManualTimeProvider clock, string runner, string instance,
        string host, string executor)
        => store.RegisterRunnerAsync(runner, new RegisterRunnerRequest(runner, host, instance, "1.0",
            TaskServerProtocol.Current, executor == CapabilityProtocol.ReviewExecutor
                ? [ReviewCapabilities.ReviewExecutor, ReviewCapabilities.GitMaterialization,
                   ReviewCapabilities.SemanticReview, ReviewCapabilities.VisionReview]
                : [ReviewCapabilities.CodingExecutor]), runner, default);

    private static Task AdvertiseAsync(TaskServerStore store, ManualTimeProvider clock, string runner, string instance,
        long generation, string executor, string key, string outcome, string credentialGeneration, string source,
        DateTime? success = null, string? evidenceId = null, string? excerpt = null)
        => store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            runner, instance, 2, clock.GetUtcNow().UtcDateTime, 180, generation,
            [new AdvertisedCapabilityDto(executor, "executor"),
             new AdvertisedCapabilityDto(key, "provider-auth", "ready", EvidenceId: evidenceId,
                 EvidenceExcerpt: excerpt, CredentialGeneration: credentialGeneration,
                 CredentialObservedAt: clock.GetUtcNow().UtcDateTime, LastRealSuccessAt: success,
                 EffectiveSource: source, HealthOutcome: outcome,
                 ServiceAvailability: outcome == "healthy" ? "available" : "unavailable",
                 CredentialHealth: outcome == "healthy" ? "valid" : "unknown")],
            CredentialHealthVersion: 2), runner, default);

    private static async Task<long> CountAsync(string path, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(path, "task-server.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static bool StoreContains(string path, string value)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(value, StringComparison.Ordinal);
        });

    /// <summary>Writes the correlated journal to CREDENTIAL_RUNBOOK_DRILL_OUT when set.</summary>
    private static async Task WriteDrillEvidenceAsync(string path, string topology, CredentialRunbookRunDto run)
    {
        var output = Environment.GetEnvironmentVariable("CREDENTIAL_RUNBOOK_DRILL_OUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var audit = new List<JsonNode?>();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(path, "task-server.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT occurred_at, actor_id, action, target_id, detail_json FROM audit
                 WHERE action LIKE 'credential-runbook.%' OR action LIKE 'provider-renewal.%' ORDER BY sequence;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                audit.Add(new JsonObject
                {
                    ["occurredAt"] = reader.GetString(0), ["actorId"] = reader.GetString(1),
                    ["action"] = reader.GetString(2), ["credentialId"] = reader.GetString(3),
                    ["detail"] = JsonNode.Parse(reader.GetString(4)),
                });
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, $"credential-runbook-drill-{topology}.json"),
            JsonSerializer.Serialize(new
            {
                provenance = "deterministic fake-clock drill against the Task Server store; not a live host run",
                topology, run.Operation, run.Receipts, audit,
            }, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }

    private sealed class SimulatedCrash : Exception;

    private sealed class CrashingJournal(ICredentialRunbookJournal inner, Func<bool> crashAfterWrite) : ICredentialRunbookJournal
    {
        public Task<CredentialRunbookOperationDto?> GetOperationAsync(string operationId, CancellationToken ct)
            => inner.GetOperationAsync(operationId, ct);
        public Task<IReadOnlyList<CredentialRunbookStepReceiptDto>> ListReceiptsAsync(string operationId, CancellationToken ct)
            => inner.ListReceiptsAsync(operationId, ct);
        public Task<CredentialGenerationFence> ReadGenerationAsync(CredentialRunbookOperationDto operation, CancellationToken ct)
            => inner.ReadGenerationAsync(operation, ct);

        public async Task<CredentialRunbookStepReceiptDto> RecordReceiptAsync(CredentialRunbookOperationDto operation,
            CredentialRunbookStepReceiptDto receipt, CancellationToken ct)
        {
            var stored = await inner.RecordReceiptAsync(operation, receipt, ct);
            if (crashAfterWrite()) throw new SimulatedCrash();
            return stored;
        }

        public async Task<CredentialRunbookOperationDto> UpdateOperationAsync(CredentialRunbookOperationDto operation,
            CancellationToken ct)
        {
            var stored = await inner.UpdateOperationAsync(operation, ct);
            if (crashAfterWrite()) throw new SimulatedCrash();
            return stored;
        }
    }

    /// <summary>
    /// A deterministic host. Side effects are keyed by attempt so a resumed attempt
    /// returns the first result; the issued secret stays here and never in a result.
    /// </summary>
    private sealed class FakeHost
    {
        private readonly Dictionary<string, string> _attempts = new(StringComparer.Ordinal);
        public List<string> SideEffects { get; } = [];
        public string Secret { get; } = string.Concat("sk", "-ant-", "oat01-", Guid.NewGuid().ToString("N"));

        public IReadOnlyDictionary<string, ICredentialRunbookStepAdapter> Adapters(Func<bool> crashAfterEffect)
        {
            var result = new Dictionary<string, ICredentialRunbookStepAdapter>(StringComparer.Ordinal);
            foreach (var step in CredentialRunbookProtocol.Steps.Keys)
                result[step] = new Adapter(context => Execute(context, crashAfterEffect));
            return result;
        }

        private CredentialRunbookStepResult Execute(CredentialRunbookStepContext context, Func<bool> crashAfterEffect)
        {
            var effect = context.StepId switch
            {
                "begin_host_login" => "issue",
                "stage_local_secret" => "stage",
                "install_generation" => "install",
                "reconcile_units_safely" => "restart",
                "verify_real_request" when context.CustodyChanged => "verify",
                "retire_old_generation" => "retire",
                _ => null,
            };
            if (effect is not null && !_attempts.ContainsKey(context.AttemptKey))
            {
                _attempts[context.AttemptKey] = effect == "issue" ? Secret : effect;
                SideEffects.Add(effect);
                if (crashAfterEffect()) throw new SimulatedCrash();
            }
            var generation = context.CustodyChanged || effect is "install" ? "generation-b" : "generation-a";
            return CredentialRunbookStepResult.Succeeded(
                context.StepId is "stage_local_secret" or "begin_host_login" ? null : generation,
                "evidence:fake-host:" + context.StepId);
        }

        private sealed class Adapter(Func<CredentialRunbookStepContext, CredentialRunbookStepResult> execute)
            : ICredentialRunbookStepAdapter
        {
            public Task<CredentialRunbookStepResult> ExecuteAsync(CredentialRunbookStepContext context, CancellationToken ct)
                => Task.FromResult(execute(context));
        }
    }
}
