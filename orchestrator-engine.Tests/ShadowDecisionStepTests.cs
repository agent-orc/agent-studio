using AgentStudio.OrchestrationDecisions;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace OrchestratorEngine.Tests;

/// <summary>AGT-3063 engine shadow model step: degraded mode and bounded alternate retry.</summary>
public sealed class ShadowDecisionStepTests
{
    private static readonly DateTime At = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Review_input_stage_claim_uses_the_active_bounded_model_receipt()
    {
        var input = Input(alternate: false) with { Mode = DecisionReceiptPolicy.ActiveMode };
        var plane = new FakePlane(input, (_, job) => job with
        {
            Status = DecisionJobStatuses.Completed,
            Result = Result(true,
                """{"chosenActions":[{"action":"ask","target":"operator:ai"}],"confidence":0.7,"reasoning":"The bounded evidence is inconclusive.","citedFacts":["run.outcome"]}"""),
        });
        var run = new OrchestrationRunDto("orch_test", "project", "AGT-1", 1, "leased",
            OrchestrationStage.ReviewInputDecision, "{}", 0, At, At, null);

        var stage = await new ReviewInputDecisionLoop(Step(plane)).ExecuteAsync(run, default);

        Assert.Equal(OrchestrationAction.Escalate, stage.Action);
        Assert.Contains("activeChoice", stage.OutputJson);
        Assert.Equal(DecisionReceiptPolicy.ActiveMode, plane.Stored?.Mode);
        Assert.Single(plane.Requests);
    }

    [Fact]
    public async Task Occurrence_without_frozen_grounds_has_no_shadow_step()
    {
        var plane = new FakePlane(null, (_, _) => throw new InvalidOperationException("no job expected"));

        Assert.Null(await Step(plane).RecordAsync("orch_none", default));
        Assert.Empty(plane.Requests);
    }

    [Fact]
    public async Task Transport_failure_retries_once_on_the_alternate_route()
    {
        var plane = new FakePlane(Input(alternate: true), (request, job) => request.Attempt == 1
            ? job with { Status = DecisionJobStatuses.Failed, Result = Result(false, null, transport: true) }
            : job with { Status = DecisionJobStatuses.Completed, Result = Result(true,
                """{"chosenActions":[{"action":"ask","target":"operator:ai"}],"confidence":0.6,"reasoning":"Unclear.","citedFacts":["run.outcome"]}""") });

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Equal([("codex", "gpt-6-sol"), ("claude", "claude-opus-5-5")],
            plane.Requests.Select(request => (request.Cli, request.Model)));
        Assert.Equal(DecisionCallStatuses.Completed, receipt.Call.Status);
        Assert.True(receipt.Call.AlternateUsed);
        Assert.Equal(2, receipt.Call.Attempts);
        Assert.False(receipt.MatchesDeterministic);
        Assert.Equal("prepare(review-subject)", receipt.DeterministicAction);
    }

    [Fact]
    public async Task Non_transport_failure_does_not_retry_and_records_ask()
    {
        var plane = new FakePlane(Input(alternate: true), (_, job) => job with
        {
            Status = DecisionJobStatuses.Failed, Result = Result(false, "prose only", transport: false),
        });

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Single(plane.Requests);
        Assert.Equal(DecisionCallStatuses.Unavailable, receipt.Call.Status);
        Assert.Equal(DecisionActionKinds.Ask, Assert.Single(receipt.Chosen).Action);
    }

    [Fact]
    public async Task Expired_wait_records_decision_unavailable_without_a_model_answer()
    {
        var plane = new FakePlane(Input(alternate: false), (_, job) => job with { CreatedAt = job.CreatedAt.AddHours(-2) });

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Equal(DecisionCallStatuses.Unavailable, receipt.Call.Status);
        Assert.Contains("not completed within 60 s", receipt.Call.Error);
        Assert.Equal(DecisionPriceStatuses.NoUsage, receipt.Call.Price.Status);
        Assert.Same(receipt, plane.Stored);
    }

    [Fact]
    public async Task Incomplete_pack_asks_without_creating_a_model_job()
    {
        var input = Input(alternate: false);
        input = input with
        {
            Grounds = input.Grounds with
            {
                Alternatives = [.. input.Grounds.Alternatives.Select(item => item with { Available = false })],
            },
        };
        var plane = new FakePlane(input, (_, _) => throw new InvalidOperationException("no job expected"));

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Empty(plane.Requests);
        Assert.Equal(DecisionCallStatuses.IncompletePack, receipt.Call.Status);
    }

    [Fact]
    public async Task Changed_artefact_digest_asks_and_does_not_call_the_model()
    {
        var bytes = Encoding.UTF8.GetBytes("bounded source");
        var input = Input(alternate: false);
        input = input with { Grounds = input.Grounds with
        {
            Facts = [.. input.Grounds.Facts, new GroundsFactV1("log.window", "signal", "Selected log",
                "bounded source", null,
                new GroundsSourceRefV1("artifact-window", "artifact:run-1/artifact-1", "sha256:old"),
                At, "complete", GroundsReliability.Reliable)],
        } };
        var plane = new FakePlane(input, (_, _) => throw new InvalidOperationException("no job expected"))
        {
            Artifact = new ArtifactContentDto("artifact-1", "run-1", "window.log", "text/plain",
                Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToBase64String(bytes), bytes.Length),
        };

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Empty(plane.Requests);
        Assert.Equal(DecisionCallStatuses.Unavailable, receipt.Call.Status);
        Assert.Equal(DecisionActionKinds.Ask, Assert.Single(receipt.Chosen).Action);
    }

    [Fact]
    public async Task Matching_immutable_window_allows_the_engine_model_call()
    {
        var content = "bounded source";
        var bytes = Encoding.UTF8.GetBytes(content);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var input = Input(alternate: false);
        input = input with { Grounds = input.Grounds with
        {
            Facts = [.. input.Grounds.Facts, new GroundsFactV1("log.window", "signal", "Selected log",
                content, null, new GroundsSourceRefV1("artifact-window", "artifact:run-1/artifact-1", digest),
                At, "complete", GroundsReliability.Reliable)],
        } };
        var plane = new FakePlane(input, (_, job) => job with { Status = DecisionJobStatuses.Completed,
            Result = Result(true,
                """{"chosenActions":[{"action":"ask","target":"operator:ai"}],"confidence":0.6,"reasoning":"Unclear.","citedFacts":["log.window"]}""") })
        {
            Artifact = new ArtifactContentDto("artifact-1", "run-1", "window.log", "text/plain",
                digest["sha256:".Length..], Convert.ToBase64String(bytes), bytes.Length),
        };

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Single(plane.Requests);
        Assert.Equal(DecisionCallStatuses.Completed, receipt.Call.Status);
    }

    [Fact]
    public async Task Engine_populates_an_empty_window_from_the_immutable_artefact()
    {
        var content = "verified window after source log rotation";
        var bytes = Encoding.UTF8.GetBytes(content);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var input = Input(alternate: false);
        input = input with { Grounds = input.Grounds with
        {
            Facts = [.. input.Grounds.Facts, new GroundsFactV1("log.window", "signal", "Selected log",
                "", null, new GroundsSourceRefV1("artifact-window", "artifact:run-1/artifact-1", digest),
                At, "complete", GroundsReliability.Reliable)],
        } };
        var plane = new FakePlane(input, (_, job) => job with { Status = DecisionJobStatuses.Completed,
            Result = Result(true,
                """{"chosenActions":[{"action":"ask","target":"operator:ai"}],"confidence":0.6,"reasoning":"Unclear.","citedFacts":["log.window"]}""") })
        {
            Artifact = new ArtifactContentDto("artifact-1", "run-1", "window.log", "text/plain",
                digest["sha256:".Length..], Convert.ToBase64String(bytes), bytes.Length,
                SourcePath: "/rotated/raw/cli-output.log"),
        };

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Single(plane.Requests);
        Assert.Equal(DecisionCallStatuses.Completed, receipt.Call.Status);
        Assert.DoesNotContain("/rotated/raw", plane.Requests[0].Prompt);
        Assert.Contains(content, plane.Requests[0].Prompt);
        Assert.Contains(receipt.Grounds.Facts, fact => fact.Id == "log.window"
            && fact.Source.Digest == digest && fact.Value == content);
    }

    [Fact]
    public async Task Engine_records_secret_omission_from_a_selected_artefact_window()
    {
        var secret = "api_key=" + new string('z', 24);
        var bytes = Encoding.UTF8.GetBytes(secret);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var input = Input(alternate: false);
        input = input with { Grounds = input.Grounds with
        {
            Facts = [.. input.Grounds.Facts, new GroundsFactV1("log.window", "signal", "Selected log",
                "", null, new GroundsSourceRefV1("artifact-window", "artifact:run-1/artifact-1", digest),
                At, "complete", GroundsReliability.Reliable)],
        } };
        var plane = new FakePlane(input, (_, job) => job with { Status = DecisionJobStatuses.Completed,
            Result = Result(true,
                """{"chosenActions":[{"action":"ask","target":"operator:ai"}],"confidence":0.6,"reasoning":"Unclear.","citedFacts":["log.window"]}""") })
        {
            Artifact = new ArtifactContentDto("artifact-1", "run-1", "window.log", "text/plain",
                digest["sha256:".Length..], Convert.ToBase64String(bytes), bytes.Length),
        };

        var receipt = (await Step(plane).RecordAsync("orch_test", default))!;

        Assert.Single(plane.Requests);
        Assert.DoesNotContain(secret, plane.Requests[0].Prompt);
        Assert.Contains(receipt.Grounds.Omissions, omission => omission.Field == "facts.log.window.value"
            && omission.Reason == "secret");
    }

    private static ShadowDecisionStep Step(IDecisionReceiptPlane plane)
        => new(plane, new EngineOptions
        {
            ServerUrl = "http://127.0.0.1", ClientId = "engine_test", PollSeconds = 1,
            ShadowDecisionTimeoutSeconds = 60,
        }, new FrozenTimeProvider(new DateTimeOffset(At)));

    private static OrchestratorDecisionJobResult Result(bool success, string? text, bool transport = false)
        => new(success, text, "codex", "gpt-6-sol", "medium",
            success ? new DecisionTokenUsageV1(5_000, 0, 0, 200) : DecisionTokenUsageV1.Zero,
            new DecisionPriceV1(success ? DecisionPriceStatuses.Priced : DecisionPriceStatuses.NoUsage,
                success ? 0.06m : null),
            success ? ["codex:thread"] : [], 0, At, At.AddSeconds(5), "runner-one",
            success ? null : "provider unreachable", transport);

    private static DecisionShadowInputV1 Input(bool alternate)
    {
        var deterministic = TransitionDecisionPolicy.Create(TransitionAction.CreateReview, "settled", "AGT-1",
            "attempt-1", 0, 0);
        var ledger = CardRunLedgerPolicy.Build([], [], [], (_, _) => new(DecisionPriceStatuses.NoUsage, null));
        var source = new GroundsSourceRefV1("run-attempt", "attempt:attempt-1");
        var choice = DecisionGroundsPolicy.CodingCompletionChoice(deterministic);
        return new(new DecisionGroundsInputV1("orch_test",
            new GroundsTriggerV1("step-transition", "CodingCompletion", At),
            new GroundsScopeV1("card", "project", "AGT-1", "attempt-1", 1, 3, null),
            "core", "4-auto-review", "test",
            new DecisionModelRouteV1("codex", "gpt-6-sol", "medium", DecisionModelCustody.RunnerHostJob,
                alternate ? "claude" : null, alternate ? "claude-opus-5-5" : null, alternate ? "high" : null),
            [new("run.outcome", "truth", "Typed run outcome", "done", null, source, At, "complete",
                GroundsReliability.Reliable, true)],
            DecisionGroundsPolicy.CodingCompletionAlternatives(deterministic, true),
            [], ledger.Ledger, ledger.Progress, []), choice.Action, choice.Target, "settled");
    }

    private sealed class FakePlane(
        DecisionShadowInputV1? input,
        Func<OrchestratorDecisionJobRequest, OrchestratorDecisionJobDto, OrchestratorDecisionJobDto> script)
        : IDecisionReceiptPlane, IEngineArtifactSource
    {
        private readonly Dictionary<string, OrchestratorDecisionJobDto> _jobs = new();
        public List<OrchestratorDecisionJobRequest> Requests { get; } = [];
        public OrchestratorDecisionReceiptV1? Stored { get; private set; }
        public ArtifactContentDto? Artifact { get; init; }

        public Task<ArtifactContentDto> GetArtifactContentAsync(string runId, string artifactId, CancellationToken ct)
            => Task.FromResult(Artifact ?? throw new InvalidDataException("Artefact missing."));

        public Task<IReadOnlyList<string>> GetPendingDecisionGroundsAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(input is null ? [] : [input.Grounds.OccurrenceId]);

        public Task<DecisionShadowInputV1?> GetGroundsInputAsync(string runId, CancellationToken ct)
            => Task.FromResult(input);

        public Task<OrchestratorDecisionJobDto> CreateDecisionJobAsync(OrchestratorDecisionJobRequest request,
            CancellationToken ct)
        {
            Requests.Add(request);
            var pending = new OrchestratorDecisionJobDto($"odj_{request.Attempt}", request.OccurrenceId,
                request.ProjectId, request.TaskId, request.Attempt, DecisionJobStatuses.Pending,
                DecisionModelCustody.RunnerHostJob, request.Cli, request.Model, request.ThinkingLevel,
                At, null, null, null, null);
            _jobs[pending.JobId] = script(request, pending);
            return Task.FromResult(_jobs[pending.JobId]);
        }

        public Task<OrchestratorDecisionJobDto> GetDecisionJobAsync(string jobId, CancellationToken ct)
            => Task.FromResult(_jobs[jobId]);

        public Task<OrchestratorDecisionReceiptV1> PutDecisionReceiptAsync(OrchestratorDecisionReceiptV1 receipt,
            CancellationToken ct)
            => Task.FromResult(Stored ??= receipt);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
