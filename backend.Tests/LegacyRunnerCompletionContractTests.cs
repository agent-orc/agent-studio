extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AgentStudio.TestSupport;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RClaimResponse = Runner::AgentRunner.RunnerClaimResponse;
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;
using RClient = Runner::AgentRunner.TaskServerClient;
using RCompletionResponse = Runner::AgentRunner.RemoteRunCompletionResponse;
using REvidence = Runner::AgentRunner.SessionContinuationEvidence;
using ROptions = Runner::AgentRunner.RunnerOptions;
using ROutcome = Runner::AgentRunner.RunOutcome;
using ROutcomeKind = Runner::AgentRunner.RunOutcomeKind;
using RPreflight = Runner::AgentRunner.RunnerProjectPreflightReport;
using RStateStore = Runner::AgentRunner.RunnerStateStore;
using RTaskRunner = Runner::AgentRunner.RemoteTaskRunner;
using RTeardown = Runner::AgentRunner.WorktreeTeardownResult;
using RWorkspace = Runner::AgentRunner.GitWorkspace;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2985 legacy-plane completion contract. On 2026-09-27 Stable 0.9.3
/// rejected every production completion with 400 "Session continuation
/// evidence does not match the fenced attempt.": on the legacy runner plane the
/// claim carries no run id, the runner's slot used the lease id as its attempt
/// id, and the continuation evidence built from that slot named an attempt the
/// server never fenced. No gate saw it, because the deployment scenario drives
/// the v1 plane, where both ids agree, and the Task Server tests build the
/// evidence by hand.
///
/// <para>
/// This test takes every step the production runner takes on the legacy plane
/// against the real backend endpoints: claim over <c>/api/runner/claim</c>,
/// <see cref="RStateStore.Create"/> without a run id,
/// <see cref="REvidence.Build"/>, <see cref="RTaskRunner.BuildCompletionRequest"/>,
/// and <c>POST /api/runner/completion</c> through the runner's own client. It
/// fails on the 0.9.3 runner code and passes on the fix. It runs in process
/// with no Git or CLI subprocess, so it is not MachineBound and the promotion
/// gate's unit-test pass runs it.
/// </para>
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class LegacyRunnerCompletionContractTests : IDisposable
{
    private const string ProjectName = "agent-runner-01";
    private const string RunnerId = "agent-runner-legacy-contract";
    private const string TaskKey = "AGT-LEGACY-COMPLETION";
    private const string RepositoryUrl = "https://github.com/agent-orc/agent-studio.git";
    private const string BaseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";

    private readonly string _workspace;
    private readonly string _watchPath;

    public LegacyRunnerCompletionContractTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-legacy-completion-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Legacy_plane_completion_with_production_continuation_evidence_is_accepted()
    {
        SeedReadyTask();
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var clock = factory.Services.GetRequiredService<TimeProvider>();
        var options = Options();
        using var client = new RClient(http, RunnerId, options: options);
        var ct = CancellationToken.None;
        await RegisterCodingRunnerAsync(client, http, clock, ct);
        await PrepareRemoteProjectAsync(http, ct);

        // 1. Claim on the legacy plane. The claim carries the server's attempt
        //    id on the lease and no run id, exactly as production sees it.
        var claim = await ClaimAsync(client, clock, ct);
        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.False(client.UsesDurableTaskServer);
        Assert.Null(claim.RunId);
        var lease = Assert.IsType<Runner::AgentRunner.RunLeaseInfoDto>(claim.Lease);
        Assert.False(string.IsNullOrWhiteSpace(lease.AttemptId));
        Assert.NotEqual(lease.LeaseId, lease.AttemptId);

        // 2. The production slot and workspace, built as RunClaimedAsync does.
        var workspace = new RWorkspace(
            options,
            claim.TaskKey!,
            _ => { },
            claim.ProjectId,
            claim.RepositoryUrl,
            claim.DefaultBranch,
            isProjectClone: true,
            sourceRunAttemptId: claim.RunId ?? lease.AttemptId ?? lease.LeaseId,
            fencingToken: lease.FencingToken);
        var slot = new RStateStore(options.StateDir).Create(
            claim.TaskKey!, lease, workspace.RepoPath, claim.RunId, claim.LeaseInstanceId,
            claim.ProjectId, claim.RepositoryUrl, claim.DefaultBranch, claim.TaskKind, claim.RunSpec);

        // 3. The teardown the runner reports for a delivered Done attempt, and
        //    the continuation evidence it derives from the slot.
        var teardown = new RTeardown(
            SecuredWork: true,
            Branch: workspace.WorkBranch,
            CommitSha: ResultSha,
            BranchUrl: null,
            ResultSha: ResultSha,
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.AttemptId!, lease.FencingToken, ResultSha));
        var evidence = REvidence.Build(slot, workspace, teardown, options.Hostname, "claude");

        // 4. The completion the runner posts, through the runner's own client.
        var outcomeDecision = Contract.ExecutionOutcomeAdapter.Classify(new Contract.ExecutionRawFacts(
            lease.AttemptId!,
            Contract.ExecutionAttemptKind.Coding,
            StdOut: "Implemented and verified.\n[[TASK_DONE]]",
            ExitCode: 0,
            DurableOutputState: Contract.DurableOutputState.Published));
        var request = RTaskRunner.BuildCompletionRequest(
            options,
            claim.TaskKey!,
            lease,
            new ROutcome(ROutcomeKind.Done, null),
            outcomeDecision,
            teardown,
            claim.RepositoryUrl,
            BaseSha,
            "refs/heads/develop",
            new string('a', 64),
            ["Implemented and verified.", "[[TASK_DONE]]"],
            sourceMutated: true,
            sessionContinuation: evidence);

        // Negative control: the server check is live. Evidence naming the lease
        // id (what 0.9.3 sent) is refused before any state changes.
        var mismatched = await http.PostAsJsonAsync(
            "/api/runner/completion",
            request with
            {
                SessionContinuation = evidence with { AttemptId = lease.LeaseId },
                IdempotencyKey = request.IdempotencyKey + ":lease-id-evidence",
            },
            ct);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        Assert.Contains(
            "Session continuation evidence does not match the fenced attempt.",
            await mismatched.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);

        // On the 0.9.3 runner code this call throws the production symptom:
        // 400 "Session continuation evidence does not match the fenced attempt."
        RCompletionResponse? completion = await client.CompleteRunAsync(request, ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        Assert.Equal(lease.AttemptId, completion.RunAttemptId);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey)));
        // Runner and server ids are one concept: the slot, the evidence, and the
        // completion all name the attempt the server fenced.
        Assert.Equal(lease.AttemptId, slot.AttemptId);
        Assert.Equal(lease.AttemptId, evidence.AttemptId);
        Assert.Equal(lease.AttemptId, request.AttemptId);
    }

    private static async Task<RClaimResponse> ClaimAsync(RClient client, TimeProvider clock, CancellationToken ct)
    {
        var request = new RClaim(
            RunnerId, ProjectName, "legacy-contract-host", 4242, "remote-runner",
            AvailableSlots: 1,
            ActiveSlots: 0,
            IdempotencyKey: "legacy-contract-claim");
        var offered = await client.ClaimAsync(request, ct);
        Assert.True(
            offered.Status == RClaimStatus.PreflightRequired,
            $"{offered.Status}: {offered.Message} {offered.AdmissionReason}");
        return await client.ClaimAsync(request with
        {
            ProjectPreflight = new RPreflight(
                offered.ProjectId!, offered.RegistrationFingerprint!, true,
                "clone/fetch URLs match registration; write probe succeeded",
                clock.GetUtcNow().UtcDateTime, offered.RepositoryUrl!, offered.RepositoryUrl!),
        }, ct);
    }

    private static async Task RegisterCodingRunnerAsync(RClient client, HttpClient http, TimeProvider clock, CancellationToken ct)
    {
        await client.RegisterAsync(ProjectName, "service", ct);
        var instanceId = client.RunnerInstanceId;
        (await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                ProjectName,
                "legacy-contract-host",
                instanceId,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor]),
            ct)).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/capabilities",
            new Contract.CapabilityAdvertisementRequest(
                RunnerId,
                instanceId,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime,
                180,
                clock.GetUtcNow().Ticks,
                [
                    new(Contract.CapabilityProtocol.CodingExecutor, "executor"),
                    new(Contract.CapabilityProtocol.GitFetch, "source"),
                    new(Contract.CapabilityProtocol.GitPush, "source"),
                    new(Contract.CapabilityProtocol.RepositoryAccess, "source"),
                    new(Contract.CapabilityProtocol.Disk, "foundation"),
                    new(Contract.CapabilityProtocol.TaskServerConnectivity, "foundation"),
                    new(Contract.CapabilityProtocol.CliExecution("claude"), "cli-execution", "ready"),
                    new(Contract.CapabilityProtocol.ProviderAuthentication("claude"), "provider-auth", "ready"),
                ]),
            ct)).EnsureSuccessStatusCode();
    }

    private static async Task PrepareRemoteProjectAsync(HttpClient http, CancellationToken ct)
    {
        (await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true },
            ct)).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync(
            "/api/projects/PROJ-001/urls",
            new { label = "repo", url = RepositoryUrl },
            ct)).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/integration-branch",
            new { branch = "develop" },
            ct)).EnsureSuccessStatusCode();
    }

    private void SeedReadyTask()
    {
        var dir = Path.Combine(_watchPath, TaskStates.Ready, TaskKey);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id = TaskKey,
            title = "Legacy-plane completion contract",
            state = TaskStates.Ready,
            order = 1,
            agent = "claude",
            kind = TaskKinds.Task,
            cliType = "claude",
        }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Make a trivial change.");
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");
    }

    private ROptions Options() => new()
    {
        ServerUrl = "http://in-process",
        RunnerId = RunnerId,
        RunnerName = ProjectName,
        Hostname = "legacy-contract-host",
        BackendName = "remote-runner",
        WorkDir = Path.Combine(_workspace, "remote-runner-work"),
        StateDir = Path.Combine(_workspace, "remote-runner-work", ".runner-state"),
        BaseBranch = "main",
        ClaudeCliBin = "claude",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 30,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    }));
            });
}
