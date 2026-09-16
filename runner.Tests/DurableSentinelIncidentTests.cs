using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2820, durable plane. The first fix for the missing-sentinel incident
/// covered the legacy completion path only. A runner talking to a versioned
/// Task Server never takes that path: it hands its completion to the outbox,
/// which posts <c>CompleteRunRequest</c> to
/// <c>/api/v1/runs/{runId}/completion</c>. On that plane the incident was not
/// decided at all, and the request had nowhere to carry it, so the plane the
/// fleet actually runs on reported a bare <c>ProtocolInconclusive</c> and the
/// card said nothing about an unreviewed delivery.
/// </summary>
public sealed class DurableSentinelIncidentTests
{
    private static readonly string BaseSha = new('0', 40);
    private static readonly string ResultSha = new('a', 40);
    private const string ImmutableRef =
        "refs/heads/agent-studio/results/run-durable/fence-7/"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What <c>GitWorkspace.SecureForHandoffAsync</c> returns for a delivered
    /// durable run: work secured, the immutable result ref pushed and verified,
    /// and the proof's commit equal to the result SHA.
    /// </summary>
    private static WorktreeTeardownResult HandedOff() => new(
        SecuredWork: true,
        Branch: "runner/agent-runner-01/AGT-2819",
        CommitSha: ResultSha,
        BranchUrl: null,
        ResultSha: ResultSha,
        Reconciliation: null,
        ImmutableResultRef: ImmutableRef,
        DeliveryProof: new RemoteDeliveryProof(
            "https://github.com/org/repo.git",
            ImmutableRef,
            ResultSha));

    /// <summary>
    /// The shape AGT-2794, AGT-2817 and AGT-2819 actually had: the worker
    /// exited zero, committed real work, and never wrote a terminal sentinel.
    /// Stamped with the durable output the same way the run loop stamps it
    /// immediately before completion, so the facts the incident reads are the
    /// facts the durable plane really reports.
    /// </summary>
    private static ExecutionOutcomeDecision Inconclusive()
        => RemoteTaskRunner.WithDurableOutput(
            ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
                "run-durable",
                ExecutionAttemptKind.Coding,
                ExitCode: 0,
                FinalAssistantOutput: "Committed 61 files.")),
            HandedOff());

    [Fact]
    public void Durable_completion_names_the_missing_sentinel_incident()
    {
        var payload = RemoteTaskRunner.BuildDurableCompletion(
            new RunOutcome(RunOutcomeKind.Unknown, "Committed 61 files."),
            Inconclusive(),
            HandedOff(),
            BaseSha,
            envelopeDigest: new string('c', 64),
            "agent-runner-01");

        Assert.NotNull(payload.GateItems);
        var gateItem = Assert.Single(payload.GateItems!);
        Assert.Contains(
            MissingSentinelIncidentPolicy.GateKey,
            gateItem,
            StringComparison.Ordinal);
        Assert.Contains($"{ImmutableRef}@{ResultSha}", gateItem, StringComparison.Ordinal);
        Assert.Contains("agent-runner-01", gateItem, StringComparison.Ordinal);
        Assert.Contains("not a completion", gateItem, StringComparison.Ordinal);
        Assert.Contains(
            "exited cleanly but emitted no sentinel",
            gateItem,
            StringComparison.Ordinal);

        // The reason on the run is the incident, not the agent's last words.
        Assert.Contains("unreviewed", payload.Summary!, StringComparison.Ordinal);
        Assert.DoesNotContain("Committed 61 files.", payload.Summary!, StringComparison.Ordinal);

        // The typed outcome is untouched: the Task Server re-validates it
        // against the shared decision and already routes it to the review lane.
        Assert.Equal(
            ExecutionOutcomeKind.ProtocolInconclusive.ToString(),
            payload.Outcome);
    }

    [Fact]
    public void A_run_that_reached_its_sentinel_carries_no_incident()
    {
        var concluded = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "run-durable",
            ExecutionAttemptKind.Coding,
            ExitCode: 0,
            FinalAssistantOutput: "[[TASK_DONE]]"));

        var payload = RemoteTaskRunner.BuildDurableCompletion(
            new RunOutcome(RunOutcomeKind.Done, "Delivered the change."),
            concluded,
            HandedOff(),
            BaseSha,
            envelopeDigest: new string('c', 64),
            "agent-runner-01");

        Assert.Null(payload.GateItems);
        Assert.Equal("Delivered the change.", payload.Summary);
    }

    [Fact]
    public void An_unproven_delivery_is_not_promoted_into_an_incident()
    {
        var payload = RemoteTaskRunner.BuildDurableCompletion(
            new RunOutcome(RunOutcomeKind.Unknown, "No delivery was secured."),
            Inconclusive(),
            HandedOff() with { DeliveryProof = null },
            BaseSha,
            envelopeDigest: null,
            "agent-runner-01");

        Assert.Null(payload.GateItems);
        // Nothing was rewritten either: with no proven delivery there is no
        // incident sentence to put in place of the run's own reason.
        Assert.Equal("No delivery was secured.", payload.Summary);
    }

    /// <summary>
    /// The incident has to survive the transport. Before AGT-2820
    /// <c>CompleteRunRequest</c> had no gate-item field at all, so the outbox
    /// serialized the incident and the client dropped it on the way out.
    /// </summary>
    [Fact]
    public async Task The_durable_transport_delivers_the_incident_to_the_versioned_plane()
    {
        var authority = new RunOutboxAuthority(
            "run-durable",
            "AGT-2819",
            "agent-runner-01",
            "agent-runner-01:7",
            "lease-d",
            7);
        using var temp = new TempDirectory();
        var outbox = DurableRunOutbox.Open(
            Path.Combine(temp.Path, "outbox"),
            authority);
        var payload = RemoteTaskRunner.BuildDurableCompletion(
            new RunOutcome(RunOutcomeKind.Unknown, "Committed 61 files."),
            Inconclusive(),
            HandedOff(),
            BaseSha,
            envelopeDigest: null,
            "agent-runner-01");
        var item = outbox.Enqueue(
            "completion",
            JsonSerializer.Serialize(payload, WebJson));

        var handler = new CompletionRecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "agent-runner-01",
            usesDurableTaskServer: true);

        await client.SendOutboxItemAsync(authority, item, default);

        Assert.NotNull(handler.LastCompletion);
        var gateItem = Assert.Single(handler.LastCompletion!.GateItems!);
        Assert.Contains(
            MissingSentinelIncidentPolicy.GateKey,
            gateItem,
            StringComparison.Ordinal);
        Assert.Contains($"{ImmutableRef}@{ResultSha}", gateItem, StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "runner-durable-sentinel-tests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class CompletionRecordingHandler : HttpMessageHandler
    {
        public CompleteRunRequest? LastCompletion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/completion", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected request to '{path}'.");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastCompletion = JsonSerializer.Deserialize<CompleteRunRequest>(body, WebJson);
            var runId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RunDto(
                    runId,
                    "task-2819",
                    LastCompletion!.Outcome,
                    LastCompletion.RunnerId,
                    LastCompletion.Fence,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    DateTime.UtcNow)),
            };
        }
    }
}
