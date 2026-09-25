using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteGateDaemonTests
{
    [Theory]
    [InlineData("exit 0", "passed", null)]
    [InlineData("exit 2", "product-failed", GateClassifications.ProductFailure)]
    public async Task Exact_result_ref_runs_frozen_gate_and_submits_fenced_report(
        string shellCommand, string expectedOutcome, string? expectedClassification)
    {
        var root = Path.Combine(Path.GetTempPath(), "remote-gate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (origin, sha) = await SeedOriginAsync(root);
            var (claim, options) = Claim(root, origin, sha, shellCommand);
            var server = new GateReportServer(claim);
            using var http = new HttpClient(server) { BaseAddress = new Uri("http://localhost") };
            using var client = new TaskServerClient(http, "gate-a");
            var daemon = new RemoteGateDaemon(options, client, _ => { });

            await daemon.RunClaimedAsync(claim, default);

            Assert.NotNull(server.Report);
            Assert.Equal(expectedOutcome, server.Report!.Report.Outcome);
            Assert.Equal(expectedClassification, server.Report.Report.Classification);
            Assert.Equal(sha, server.Report.Report.TestedSha);
            Assert.False(server.Report.Report.DirtyBefore);
            Assert.Equal("complete", server.Report.Report.CleanupStatus);
            Assert.Equal([GateStates.Materializing, GateStates.Running,
                GateStates.Reporting, GateStates.Cleaning], server.Phases);
            Assert.False(Directory.Exists(Path.Combine(options.ReviewWorkDir,
                claim.Lease!.ResourceNamespace)));
        }
        finally
        {
            ResilientDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Cleanup_failure_is_reported_even_after_a_passing_command()
    {
        var root = Path.Combine(Path.GetTempPath(), "remote-gate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (origin, sha) = await SeedOriginAsync(root);
            var (claim, options) = Claim(root, origin, sha, "exit 0");
            var server = new GateReportServer(claim);
            using var http = new HttpClient(server) { BaseAddress = new Uri("http://localhost") };
            using var client = new TaskServerClient(http, "gate-a");
            var daemon = new RemoteGateDaemon(options, client, _ => { })
            {
                CleanupOverride = async (workspace, attemptId) =>
                {
                    await workspace.CleanupAsync(attemptId);
                    return false;
                },
            };

            await daemon.RunClaimedAsync(claim, default);

            Assert.Equal("failed", server.Report!.Report.CleanupStatus);
            Assert.Equal(sha, server.Report.Report.TestedSha);
        }
        finally
        {
            ResilientDirectory.Delete(root);
        }
    }

    private static (GateClaimResponse Claim, RunnerOptions Options) Claim(
        string root, string origin, string sha, string shellCommand)
    {
        var plan = new GatePlan("post-build-test-gate", 1,
            [new GateCommand("verify", "sh", ["-c", shellCommand], "", 10)],
            "", 30, [], 4096, "always");
        var subject = new GateSubject("subject-a", "task-a", "run-a",
            RepositoryIdentityContract.FromUrl(origin)!, origin, sha,
            "refs/heads/result", null, null, "plan-hash", "policy-hash", 1,
            "selection-hash", plan, DateTime.UtcNow.AddMinutes(5), 1, DateTime.UtcNow);
        var attempt = new GateAttempt("gat_test", subject.SubjectId, 1, GateStates.Claimed,
            "gate-a", "host-a", null, null, DateTime.UtcNow, DateTime.UtcNow, null, null);
        var lease = new GateLease("lease-a", attempt.AttemptId, "gate-a", "instance-a", "host-a",
            1, 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "gate-gat_test-1");
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "gate-a",
            RunnerName = "gate-a",
            Hostname = "host-a",
            BackendName = "gate",
            Role = "gate",
            WorkDir = Path.Combine(root, "coding"),
            ReviewWorkDir = Path.Combine(root, "gate-work"),
            StateDir = Path.Combine(root, "state"),
            BaseBranch = "main",
            CliBin = "unused",
            CliArgs = "",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            PollSeconds = 1,
        };
        return (new GateClaimResponse("claimed", subject, attempt, lease), options);
    }

    private static async Task<(string Origin, string Sha)> SeedOriginAsync(string root)
    {
        var origin = Path.Combine(root, "origin.git");
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await GitAsync(root, "init", "--bare", "--initial-branch=main", origin);
        await GitAsync(source, "init", "--initial-branch=main");
        await GitAsync(source, "config", "user.name", "Gate Test");
        await GitAsync(source, "config", "user.email", "gate@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "exact gate subject\n");
        await GitAsync(source, "add", "README.md");
        await GitAsync(source, "commit", "-m", "seed");
        await GitAsync(source, "remote", "add", "origin", origin);
        await GitAsync(source, "push", "origin", "main");
        var sha = (await GitAsync(source, "rev-parse", "HEAD")).Trim();
        await GitAsync(root, "--git-dir", origin, "update-ref", "refs/heads/result", sha);
        return (origin, sha);
    }

    private static async Task<string> GitAsync(string cwd, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut;
    }

    private sealed class GateReportServer(GateClaimResponse claim) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public List<string> Phases { get; } = [];
        public SubmitGateReportRequest? Report { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/phase", StringComparison.Ordinal))
            {
                var phase = await request.Content!.ReadFromJsonAsync<GatePhaseRequest>(Json, cancellationToken);
                Phases.Add(phase!.State);
                return JsonResponse(claim.Attempt! with { State = phase.State });
            }
            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                Report = await request.Content!.ReadFromJsonAsync<SubmitGateReportRequest>(Json, cancellationToken);
                return JsonResponse(new GateAttemptView(claim.Attempt! with
                {
                    State = Report!.Report.Outcome,
                }, claim.Lease, Report.Report));
            }
            throw new InvalidOperationException("Unexpected gate API path: " + path);
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
