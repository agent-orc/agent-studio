using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3063 pure grounds, ledger, response and receipt rules (AGT-W73 OD-S1).</summary>
public sealed class DecisionGroundsPolicyTests
{
    private static readonly DateTime At = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    // $10 per million uncached input, $1 cached, $2 cache write, $50 output.
    private static DecisionPriceV1 Price(RunLedgerRowV1 row, DecisionTokenUsageV1 usage)
        => row.Model == "mystery-model"
            ? new(DecisionPriceStatuses.UnknownModel, null)
            : new(DecisionPriceStatuses.Priced, (usage.UncachedInput * 10m + usage.CachedInput * 1m
                + usage.CacheWrite * 2m + usage.Output * 50m) / 1_000_000m);

    private static RunLedgerRowV1 Row(string? call, string run, long input, long cached, long output,
        bool raw = false, string model = "gpt-6-sol", int minute = 0)
        => new(call, run, At.AddMinutes(minute), model, "medium", input, cached, 0, output, raw);

    [Fact]
    public void Duplicate_ledger_rows_are_excluded_before_aggregation_and_shown_for_audit()
    {
        var rows = new[]
        {
            Row("call-1", "attempt-1", 1_000, 0, 100),
            Row("call-1", "attempt-2", 1_000, 0, 100, minute: 5),
            Row(null, "attempt-1", 2_000, 0, 200, minute: 1),
            Row(null, "attempt-1", 2_000, 0, 200, minute: 1),
        };

        var ledger = CardRunLedgerPolicy.Build(rows, [], [], Price).Ledger;

        Assert.Equal(4, ledger.RawRows);
        Assert.Equal(2, ledger.CountedRows);
        Assert.Equal(2, ledger.ExcludedDuplicateRows);
        Assert.Equal(3_000, ledger.Tokens.UncachedInput);
        Assert.Equal(300, ledger.Tokens.Output);
        Assert.Equal(0.045m, ledger.PricedSubtotalUsd);
        Assert.Equal(0.045m, ledger.ExcludedDuplicateUsd);
        Assert.Contains("duplicates-excluded", ledger.DataQuality);
        Assert.Equal(DecisionPriceStatuses.Priced, ledger.PriceStatus);
    }

    [Fact]
    public void Cached_input_inside_a_raw_provider_total_is_not_priced_as_uncached_input()
    {
        var ledger = CardRunLedgerPolicy.Build(
            [Row("call-1", "attempt-1", 100_000, 90_000, 1_000, raw: true)], [], [], Price).Ledger;

        Assert.Equal(new DecisionTokenUsageV1(10_000, 90_000, 0, 1_000), ledger.Tokens);
        // 10k x $10 + 90k x $1 + 1k x $50 per million; a raw total would cost $1.05.
        Assert.Equal(0.24m, ledger.PricedSubtotalUsd);
    }

    [Fact]
    public void Unknown_price_stays_explicit_and_never_becomes_zero_spend()
    {
        var mixed = CardRunLedgerPolicy.Build(
        [
            Row("call-1", "attempt-1", 1_000, 0, 100),
            Row("call-2", "attempt-1", 5_000, 0, 500, model: "mystery-model", minute: 1),
        ], [], [], Price).Ledger;
        var unknown = CardRunLedgerPolicy.Build(
            [Row("call-3", "attempt-1", 5_000, 0, 500, model: "mystery-model")], [], [], Price).Ledger;

        Assert.Equal(DecisionPriceStatuses.Partial, mixed.PriceStatus);
        Assert.Equal(1, mixed.UnpricedRows);
        Assert.Equal(5_500, mixed.UnpricedTokens);
        Assert.Equal(["mystery-model"], mixed.UnpricedModels);
        Assert.Equal(0.015m, mixed.PricedSubtotalUsd);
        Assert.Equal(DecisionPriceStatuses.UnknownModel, unknown.PriceStatus);
        Assert.Contains("unpriced-rows", unknown.DataQuality);
    }

    [Fact]
    public void Progress_follows_the_rb_s1_tree_and_verification_definition()
    {
        var rounds = new[]
        {
            new RunLedgerRoundInputV1("attempt-1", 1, At, At.AddMinutes(30), "done", "a", "b", false, false),
            new RunLedgerRoundInputV1("attempt-2", 2, At.AddHours(1), At.AddHours(2), "timeout", "b", "b", false, true),
            new RunLedgerRoundInputV1("attempt-3", 3, At.AddHours(3), At.AddHours(4), "timeout", "b", "b", false, true),
        };

        var result = CardRunLedgerPolicy.Build(
            [Row("call-1", "attempt-2", 1_000, 0, 100, minute: 70)], rounds, ["b"], Price);

        Assert.Equal(["result-tree-differs-from-start", "unchanged-tree-without-new-verification",
                "unchanged-tree-without-new-verification"],
            result.Ledger.Rounds.Select(round => round.ProgressReason));
        Assert.Equal(1_000, result.Ledger.Rounds[1].Tokens.UncachedInput);
        Assert.Equal(2, result.Progress.RoundsWithoutProgress);
        Assert.Equal(2, result.Progress.ExhaustionsInDelivery);
        Assert.Equal(3, result.Progress.RoundsSinceAcceptedDelivery);
        Assert.Equal("b", result.Progress.LastVerifiedTreeSha);

        var newlyVerified = CardRunLedgerPolicy.Build([], [rounds[0] with { ResultSha = "a" }, rounds[1]],
            ["b"], Price);
        Assert.Equal("newly-verified-tree", newlyVerified.Ledger.Rounds[1].ProgressReason);
    }

    [Fact]
    public void Freeze_is_deterministic_across_wire_round_trips_and_the_digest_covers_content()
    {
        var input = Input();
        var first = DecisionGroundsPolicy.Freeze(input);
        var wire = JsonSerializer.Deserialize<DecisionGroundsInputV1>(
            JsonSerializer.Serialize(input, DecisionGroundsPolicy.Json), DecisionGroundsPolicy.Json)!;
        var replay = DecisionGroundsPolicy.Freeze(wire);
        var changed = DecisionGroundsPolicy.Freeze(input with
        {
            Facts = [.. input.Facts.Select(fact => fact.Id == "run.outcome" ? fact with { Value = "blocked" } : fact)],
        });

        Assert.StartsWith("sha256:", first.ContentDigest);
        Assert.Equal(first.ContentDigest, replay.ContentDigest);
        Assert.NotEqual(first.ContentDigest, changed.ContentDigest);
        Assert.True(DecisionGroundsPolicy.IsIntact(first));
        Assert.False(DecisionGroundsPolicy.IsIntact(first with { PolicyVersion = "edited" }));
        Assert.False(first.Pack.Incomplete);
    }

    [Fact]
    public void Freeze_bounds_the_pack_and_keeps_required_facts_and_candidates()
    {
        var noise = Enumerable.Range(0, 200).Select(index => new GroundsFactV1($"noise.{index}", "signal",
            "Noise", new string('x', 900), null, new("log-window", $"window:{index}"), At, "complete",
            GroundsReliability.Reliable)).ToArray();
        var input = Input() with { Facts = [.. Input().Facts, .. noise] };

        var grounds = DecisionGroundsPolicy.Freeze(input);

        Assert.True(grounds.Pack.Truncated);
        Assert.False(grounds.Pack.Incomplete);
        Assert.True(grounds.Pack.EstimatedTokens <= DecisionGroundsPolicy.PackTokenBudget);
        Assert.True(grounds.Pack.Bytes <= DecisionGroundsPolicy.MaxPackBytes);
        Assert.Contains(grounds.Facts, fact => fact.Id == "run.outcome");
        Assert.All(grounds.Facts.Where(fact => fact.Id.StartsWith("noise.", StringComparison.Ordinal)),
            fact => Assert.Contains("[truncated", fact.Value));
        Assert.Contains(grounds.Pack.TruncationMarkers, marker => marker.Contains("token budget"));
        Assert.Equal(input.Alternatives.Count, grounds.Alternatives.Count);
    }

    [Fact]
    public void Pack_without_an_available_candidate_is_incomplete_and_receipts_ask_without_a_call()
    {
        var input = Input() with
        {
            Alternatives = [.. Input().Alternatives.Select(item => item with { Available = false })],
        };
        var grounds = DecisionGroundsPolicy.Freeze(input);

        var receipt = DecisionReceiptPolicy.Build(new(grounds, Deterministic, "settled", null, 0, false, null));

        Assert.True(grounds.Pack.Incomplete);
        Assert.Equal(DecisionCallStatuses.IncompletePack, receipt.Call.Status);
        Assert.Equal(new DecisionChoiceV1(DecisionActionKinds.Ask, DecisionReceiptPolicy.DegradedTarget,
            DecisionReceiptPolicy.ShadowDispatch), Assert.Single(receipt.Chosen));
    }

    [Theory]
    [InlineData("""{"chosenActions":[{"action":"prepare","target":"review-subject"}],"confidence":0.8,"reasoning":"Settled.","citedFacts":["run.outcome"]}""", null)]
    [InlineData("""Answer: ```json {"chosenActions":[{"action":"retry","target":"card:ready"}],"confidence":0.4,"reasoning":"Retry.","citedFacts":["run.outcome","unknown"]} ```""", null)]
    [InlineData("""{"chosenActions":[{"action":"escalate","target":"x"}],"confidence":0.8,"reasoning":"r","citedFacts":["run.outcome"]}""", "action-outside-union")]
    [InlineData("""{"chosenActions":[{"action":"halt","target":"card:park"}],"confidence":0.8,"reasoning":"r","citedFacts":["run.outcome"]}""", "forbidden-action")]
    [InlineData("""{"chosenActions":[{"action":"prepare","target":"review-subject"},{"action":"ask","target":"operator:human-review"}],"confidence":0.8,"reasoning":"r","citedFacts":["run.outcome"]}""", "chosen-action-count")]
    [InlineData("""{"chosenActions":[{"action":"prepare","target":"review-subject"}],"confidence":1.7,"reasoning":"r","citedFacts":["run.outcome"]}""", "confidence-out-of-range")]
    [InlineData("""{"chosenActions":[{"action":"prepare","target":"review-subject"}],"confidence":0.8,"reasoning":"r","citedFacts":["invented"]}""", "insufficient-evidence")]
    [InlineData("The model wrote prose.", "no-json-object")]
    public void Model_response_must_choose_an_available_candidate_from_the_closed_union(string text, string? failure)
    {
        var parsed = DecisionReceiptPolicy.Parse(text, DecisionGroundsPolicy.Freeze(Input()));

        Assert.Equal(failure is null, parsed.Valid);
        Assert.Equal(failure, parsed.Failure);
        if (failure is null) Assert.Equal(["run.outcome"], parsed.CitedFacts);
    }

    [Fact]
    public void Receipt_records_model_choice_usage_price_and_a_stable_identity_without_dispatch()
    {
        var grounds = DecisionGroundsPolicy.Freeze(Input());
        var job = Job(new OrchestratorDecisionJobResult(true,
            """{"chosenActions":[{"action":"prepare","target":"review-subject"}],"confidence":0.9,"reasoning":"The envelope is settled.","citedFacts":["run.outcome"],"prediction":"Review passes."}""",
            "codex", "gpt-6-sol", "medium", new(6_000, 1_000, 0, 300),
            new(DecisionPriceStatuses.Priced, 0.0761m), ["codex:thread-1"], 1, At, At.AddSeconds(20),
            "runner-one", null, false));

        var receipt = DecisionReceiptPolicy.Build(new(grounds, Deterministic, "settled", job, 1, false, null));
        var again = DecisionReceiptPolicy.Build(new(grounds, Deterministic, "settled", job, 1, false, null));

        Assert.Equal(JsonSerializer.Serialize(again, DecisionGroundsPolicy.Json),
            JsonSerializer.Serialize(receipt, DecisionGroundsPolicy.Json));
        Assert.Equal(DecisionReceiptPolicy.ReceiptId(grounds.OccurrenceId, grounds.ContentDigest), receipt.ReceiptId);
        Assert.Equal("/api/orchestration/decision-receipts/" + receipt.ReceiptId, receipt.EvidenceRef);
        Assert.Equal(DecisionCallStatuses.Completed, receipt.Call.Status);
        Assert.True(receipt.MatchesDeterministic);
        Assert.Equal(DecisionReceiptPolicy.ShadowDispatch, receipt.DispatchOutcome);
        Assert.Equal(DecisionModelCustody.RunnerHostJob, receipt.Call.Custody);
        Assert.Equal("runner-one", receipt.Call.HostId);
        Assert.Equal(1, receipt.Call.ExcludedDuplicateFrames);
        Assert.False(receipt.Call.CallTokenCapExceeded);
        Assert.Equal(0.9, receipt.Confidence);
        Assert.Equal("Review passes.", receipt.Prediction);
        Assert.Contains(receipt.FloorRejected, item => item.Action == DecisionActionKinds.Halt);
    }

    [Fact]
    public void Unavailable_model_records_ask_and_keeps_an_unknown_price_explicit()
    {
        var grounds = DecisionGroundsPolicy.Freeze(Input());
        var failed = Job(new OrchestratorDecisionJobResult(false, null, "codex", "gpt-6-sol", "medium",
            DecisionTokenUsageV1.Zero, new(DecisionPriceStatuses.NoUsage, null), [], 0, At, At,
            "runner-one", "provider unreachable", true)) with { Status = DecisionJobStatuses.Failed };

        var receipt = DecisionReceiptPolicy.Build(new(grounds, Deterministic, "settled", failed, 2, true, null));

        Assert.Equal(DecisionCallStatuses.Unavailable, receipt.Call.Status);
        Assert.True(receipt.Call.AlternateUsed);
        Assert.Equal(2, receipt.Call.Attempts);
        Assert.Null(receipt.Call.Price.EstimatedUsd);
        Assert.False(receipt.MatchesDeterministic);
        Assert.Equal(DecisionActionKinds.Ask, Assert.Single(receipt.Chosen).Action);
        Assert.Contains("decision-unavailable", receipt.Reasoning);
    }

    internal static readonly DecisionChoiceV1 Deterministic =
        new(DecisionActionKinds.Prepare, "review-subject", "applied:deterministic");

    internal static OrchestratorDecisionJobDto Job(OrchestratorDecisionJobResult result)
        => new("odj_test", "orch_test", "project", "AGT-1", 1, DecisionJobStatuses.Completed,
            DecisionModelCustody.RunnerHostJob, "codex", "gpt-6-sol", "medium", At, At, "runner-one",
            result, null);

    internal static DecisionGroundsInputV1 Input()
    {
        var deterministic = TransitionDecisionPolicy.Create(TransitionAction.CreateReview, "settled", "AGT-1",
            "attempt-1", 0, 0);
        var ledger = CardRunLedgerPolicy.Build([Row("call-1", "attempt-1", 1_000, 0, 100)],
            [new("attempt-1", 1, At, At.AddMinutes(20), "done", "a", "b", false, false)], [], Price);
        var source = new GroundsSourceRefV1("run-attempt", "attempt:attempt-1");
        return new DecisionGroundsInputV1("orch_test",
            new GroundsTriggerV1("step-transition", "CodingCompletion", At),
            new GroundsScopeV1("card", "project", "AGT-1", "attempt-1", 1, 3, null),
            "core", "4-auto-review", "orchestration-decisions:test/od-s1",
            new DecisionModelRouteV1("codex", "gpt-6-sol", "medium", DecisionModelCustody.RunnerHostJob),
            [
                new("run.outcome", "truth", "Typed run outcome", "done", null, source, At, "complete",
                    GroundsReliability.Reliable, true),
                new("result.sha", "truth", "Result tree SHA", "b", null, source, At, "complete",
                    GroundsReliability.Reliable, true),
            ],
            DecisionGroundsPolicy.CodingCompletionAlternatives(deterministic, true),
            [], ledger.Ledger, ledger.Progress,
            [new("quota.window-head", "Not collected.")]);
    }
}
