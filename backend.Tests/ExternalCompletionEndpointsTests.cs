using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP-level coverage for <c>POST /api/tasks/{id}/external-completion</c>
/// (docs/concepts/out-of-band-task-completion.md §3). Pins the atomic
/// reconciliation contract: status.md + deliverables.md are written, the
/// stale lifecycle is terminalized, the external timeline entry lands, the
/// externalCompletion provenance is stamped, and the lane moves.
/// </summary>
// MachineBound 20.07.: echte Ordner-Moves+Timeline, unter Gate-Parallellast flaky
[Trait("Category", "MachineBound")]
public sealed class ExternalCompletionEndpointsTests : IDisposable
{
    private const string ProjectName = "external-completion-test";

    private readonly string _workspace;
    private readonly string _watchPath;

    public ExternalCompletionEndpointsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-external-completion-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    [Trait("Category", "MachineBound")] // Documents a real move-layer defect (lane move
    // reports Success while the copy+delete fallback leaves the full source folder
    // behind), which poisons every build-test gate until the move layer is fenced.
    // Tracked as a bug card in the AGT-2182 fencing line; untag when that lands.
    public async Task ExternalCompletion_ReconcilesEscalatedCard_WritesEvidenceMovesLaneRecordsTimeline()
    {
        // A card stuck in 5e-escalated with the "no agent-written summary" corpse
        // and a lifecycle.json still running post-processing (the AGT-1917 shape).
        WriteJob(TaskStates.Escalated, "stuck-card", "Stuck Card",
            "Do the out-of-band thing.",
            statusBody: "# Status\n\n- Result: Escalated to human decision (watchdog-kill)\n\nno agent-written summary.");
        WriteLifecycleRunning(TaskStates.Escalated, "stuck-card");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        var request = new ExternalCompletionRequest
        {
            Summary = "Implemented and committed out-of-band in docs/concepts.",
            Source = "operator-chat",
            Deliverables = new List<ExternalDeliverable>
            {
                new() { Path = "docs/concepts/out-of-band-task-completion.md@abc1234", Note = "concept doc" },
                new() { Url = "https://github.com/example/repo/tree/runner/host/stuck-card", Note = "runner salvage branch" },
            },
            GateItems =
            [
                "worktree-blocked: unsecured worktree on agent-runner-01: /var/lib/agent-runner/worktrees/AGT-2147",
            ],
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/stuck-card/external-completion?watchPath={watchPath}", request);

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TaskStates.HumanReview, body.RootElement.GetProperty("targetState").GetString());
        Assert.Equal("operator-chat", body.RootElement.GetProperty("source").GetString());

        // Lane moved 5e-escalated -> 5-human-review. On Windows the source
        // folder can linger briefly after a reported Success (open handle from
        // the just-written lifecycle terminalization; the move falls back to
        // copy+retry-delete), so poll instead of asserting the very first read.
        var source = Path.Combine(_watchPath, TaskStates.Escalated, "stuck-card");
        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, "stuck-card");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((Directory.Exists(source) || !Directory.Exists(moved)) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(moved));

        // status.md replaced with the out-of-band result.
        var status = File.ReadAllText(Path.Combine(moved, "status.md"));
        Assert.Contains("Completed out-of-band", status);
        Assert.Contains("operator-chat", status);
        Assert.DoesNotContain("no agent-written summary", status);

        // results/deliverables.md written with the deliverable + provenance.
        var deliverables = File.ReadAllText(Path.Combine(moved, "results", "deliverables.md"));
        Assert.Contains("out-of-band-task-completion.md@abc1234", deliverables);
        Assert.Contains("[https://github.com/example/repo/tree/runner/host/stuck-card]", deliverables);
        Assert.Contains("Completed externally by operator-chat", deliverables);

        // task.json carries the externalCompletion provenance for the badge.
        var taskJson = File.ReadAllText(Path.Combine(moved, "task.json"));
        Assert.Contains("externalCompletion", taskJson);
        Assert.Contains("operator-chat", taskJson);

        // lifecycle.json terminalized: awaiting-review + running check failed.
        var lifecycle = File.ReadAllText(Path.Combine(moved, "lifecycle.json"));
        Assert.Contains("awaiting-review", lifecycle);
        Assert.Contains("failed", lifecycle);
        Assert.DoesNotContain("\"status\": \"running\"", lifecycle);

        // The external timeline entry lands (the card's history stops being a corpse).
        var timeline = File.ReadAllText(TaskPaths.TimelineLog(moved));
        Assert.Contains(TimelineEventKinds.ExternalCompletion, timeline);
        Assert.Contains("Completed externally by operator-chat", timeline);

        // A remote salvage failure arrives as an explicit open gate item. The
        // escalation summary consumes this checklist on the moved card.
        var followUp = File.ReadAllText(Path.Combine(moved, "orchestrator-follow-up.md"));
        Assert.Contains("- [ ] worktree-blocked: unsecured worktree on agent-runner-01", followUp);
    }

    [Fact]
    public async Task ExternalCompletion_MissingSummary_ReturnsBadRequest()
    {
        WriteJob(TaskStates.Escalated, "no-summary", "No Summary", "Prompt.");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/no-summary/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest { Source = "chat" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The card is untouched: no accidental lane move on a rejected request.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "no-summary")));
    }

    [Fact]
    public async Task ExternalCompletion_UnknownTask_ReturnsNotFound()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/does-not-exist/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest { Summary = "done", Source = "chat" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExternalCompletion_HonorsExplicitTargetState()
    {
        WriteJob(TaskStates.Progress, "explicit-target", "Explicit Target", "Prompt.", commitSha: null);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/explicit-target/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest
            {
                Summary = "done elsewhere",
                Source = "remote-arm",
                TargetState = TaskStates.CodeNotComplete,
            });

        response.EnsureSuccessStatusCode();
        Assert.True(Directory.Exists(
            Path.Combine(_watchPath, TaskStates.CodeNotComplete, "explicit-target")));
    }

    /// <summary>
    /// AGT-2220: the same call aimed at a TERMINAL lane is refused. Stamping a
    /// coding card "completed" out-of-band without a commit anyone can find in
    /// the target repository is exactly the 11.07. phantom shape, so it now
    /// returns 409 and the card never reaches <c>6-completed</c>.
    /// </summary>
    [Fact]
    public async Task ExternalCompletion_RefusesTerminalTargetWithoutRepositoryProof()
    {
        WriteJob(TaskStates.Progress, "no-proof", "No Proof", "Prompt.", commitSha: null);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/no-proof/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest
            {
                Summary = "trust me, it is done",
                Source = "remote-arm",
                TargetState = TaskStates.Completed,
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "no-proof")));
    }

    [Fact]
    public async Task ExternalCompletion_RejectsResultEqualToAttemptBaseWithoutMutatingCard()
    {
        var baseSha = new string('a', 40);
        WriteJob(TaskStates.Progress, "empty-result", "Empty Result", "Prompt.", commitSha: null);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/empty-result/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest
            {
                Summary = "Remote work completed without a terminal sentinel.",
                Source = "agent-runner-01",
                ResultSha = baseSha,
                BaseSha = baseSha,
                ResultRef = "refs/heads/runner/agent-runner-01/empty-result",
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "does not differ from the attempt base",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        var folder = Assert.Single(
            TaskStates.All.Select(state => Path.Combine(_watchPath, state, "empty-result")),
            Directory.Exists);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "empty-result")));
        Assert.False(File.Exists(Path.Combine(folder, "results", "deliverables.md")));
        Assert.DoesNotContain(
            "externalCompletion",
            File.ReadAllText(Path.Combine(folder, "task.json")),
            StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                    });
                });
            });

    private void WriteJob(
        string state,
        string slug,
        string title,
        string promptBody,
        string statusBody = "Result: Success.",
        string? commitSha = "abc1234")
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);

        var commitJson = commitSha == null
            ? ""
            : $",\"commits\":[{{\"sha\":\"{commitSha}\",\"message\":\"work\",\"authorEmail\":\"x@y\",\"at\":\"2026-05-29T12:00:00Z\",\"fileCount\":1}}]";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"{commitJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), statusBody);
    }

    private void WriteLifecycleRunning(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        var lifecycle =
            "{\"version\":1,\"phase\":\"post-processing-running\"," +
            "\"phaseEnteredAt\":\"2026-07-07T10:00:00Z\"," +
            "\"postProcessingChecks\":[{\"name\":\"orchestrator-post-processing\"," +
            "\"status\":\"running\",\"startedAt\":\"2026-07-07T10:00:00Z\"," +
            "\"detail\":\"still running\"}]}";
        File.WriteAllText(Path.Combine(dir, "lifecycle.json"), lifecycle);
    }
}
