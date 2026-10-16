using AgentStudio.Orchestrator;
using AgentStudio.Publishing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorContextDigestServiceTests
{
    [Fact]
    public void ScopeTasks_GlobalProjectAndTaskRespectContextBoundary()
    {
        var alpha = Task("alpha", "ALPHA-1", "alpha-one");
        var alphaOther = Task("alpha", "ALPHA-2", "alpha-two");
        var beta = Task("beta", "BETA-1", "beta-one");
        var all = new[] { alpha, alphaOther, beta };

        Assert.True(OrchestratorContextKey.TryParse("global", out var global));
        var globalTasks = OrchestratorContextDigestService.ScopeTasks(global, all, out var globalFocus);
        Assert.Equal(3, globalTasks.Count);
        Assert.Null(globalFocus);

        Assert.True(OrchestratorContextKey.TryParse("project:alpha", out var project));
        var projectTasks = OrchestratorContextDigestService.ScopeTasks(project, all, out var projectFocus);
        Assert.Equal(2, projectTasks.Count);
        Assert.All(projectTasks, task => Assert.Equal("alpha", task.ProjectName));
        Assert.Null(projectFocus);

        Assert.True(OrchestratorContextKey.TryParse("task:alpha/ALPHA-1", out var taskContext));
        var taskTasks = OrchestratorContextDigestService.ScopeTasks(taskContext, all, out var focus);
        Assert.Equal(2, taskTasks.Count);
        Assert.Same(alpha, focus);
    }

    [Fact]
    public void ScopeTasks_UnknownTaskFailsInsteadOfBorrowingAnotherContext()
    {
        Assert.True(OrchestratorContextKey.TryParse("task:alpha/MISSING-1", out var context));

        var error = Assert.Throws<KeyNotFoundException>(() =>
            OrchestratorContextDigestService.ScopeTasks(
                context,
                [Task("alpha", "ALPHA-1", "alpha-one")],
                out _));

        Assert.Contains("MISSING-1", error.Message);
    }

    [Fact]
    public void ScopeTasks_MatchesLegacyTaskKeyCaseInsensitively()
    {
        Assert.True(OrchestratorContextKey.TryParse("task:alpha/LEGACY-1", out var context));
        var legacy = new TaskInfo
        {
            ProjectName = "alpha",
            Id = "legacy-folder",
            Key = null,
            TaskKey = "legacy-1",
        };

        OrchestratorContextDigestService.ScopeTasks(context, [legacy], out var focus);

        Assert.Same(legacy, focus);
    }

    [Fact]
    public void RenderDigest_CarriesAllReadSectionsAndCapsNoisyTails()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:alpha", out var context));
        var now = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc);
        var transitions = Enumerable.Range(0, 10)
            .Select(i => new DigestTransition(now.AddMinutes(-i), "alpha", $"ALPHA-{i}", "2-ready", "3-progress", "system"))
            .ToList();
        var runs = Enumerable.Range(0, 10)
            .Select(i => new DigestProgressRun("alpha", $"RUN-{i}", $"Run {i}", "active", LifecyclePhases.ExecutionRunning, now, now, "codex", "gpt-test"))
            .ToList();
        var decisions = Enumerable.Range(0, 10)
            .Select(i => new DigestDecision(now.AddMinutes(-i), "alpha", $"DEC-{i}", "Reissue", "bounded reason"))
            .ToList();

        var data = new OrchestratorContextDigestData(
            context,
            now,
            [new DigestProjectLanes("alpha", 2, new Dictionary<string, int> { ["2-ready"] = 1, ["3-progress"] = 1 }, "auto-continuous", 1, 2)],
            new DigestTaskFocus("alpha", "ALPHA-1", "Focused task", "3-progress", LifecyclePhases.ExecutionRunning, now),
            transitions,
            runs,
            new QuotaReport
            {
                TtlSeconds = 600,
                Snapshots =
                [
                    new QuotaSnapshot
                    {
                        CliType = "codex",
                        FetchedAt = now,
                        Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 42, ResetAt = now.AddDays(1) }]
                    }
                ]
            },
            [new DigestPublishProject("alpha", true, null, [new PublishTarget { Label = "npm", PendingCount = 2 }])],
            new TaskWatcherHealthSnapshot(now.AddHours(-1), 1, 1, now.AddMinutes(-1), null, true),
            decisions);

        var digest = OrchestratorContextDigestService.RenderDigest(data);

        Assert.Contains("lanes:", digest);
        Assert.Contains("board pulse", digest);
        Assert.Contains("progress runs", digest);
        Assert.Contains("quota", digest);
        Assert.Contains("publish targets", digest);
        Assert.Contains("healthz=ok; watcher=healthy", digest);
        Assert.Contains("decision journal", digest);
        Assert.Contains("task focus", digest);
        Assert.Contains("ALPHA-7", digest);
        Assert.DoesNotContain("ALPHA-8:", digest);
        Assert.Contains("RUN-7", digest);
        Assert.DoesNotContain("RUN-8", digest);
        Assert.Contains("DEC-7", digest);
        Assert.DoesNotContain("DEC-8", digest);
    }

    [Fact]
    public void SourceStatuses_RunWithoutTimestampDoesNotThrow()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:alpha", out var context));
        var now = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc);
        var data = new OrchestratorContextDigestData(
            context,
            now,
            [],
            null,
            [],
            [new DigestProgressRun("alpha", "ALPHA-1", "Run", "progress-idle", LifecyclePhases.ExecutionRunning, null, null, null, null)],
            new QuotaReport(),
            [],
            new TaskWatcherHealthSnapshot(null, 0, 0, null, null, false),
            []);

        var statuses = OrchestratorContextDigestService.BuildSourceStatuses(data);

        var runs = Assert.Single(statuses, source => source.Name == "runs");
        Assert.Equal("ok", runs.Status);
        Assert.Null(runs.CapturedAt);
        Assert.Equal(
            ["lanes", "transitions", "runs", "quota", "publishTargets", "health", "decisionJournal"],
            statuses.Select(source => source.Name).ToArray());
    }

    [Fact]
    public void RenderDigest_preserves_project_named_secret_and_redacts_credential_values()
    {
        Assert.True(OrchestratorContextKey.TryParse("global", out var context));
        var credential = "api_key=" + new string('r', 24);
        var data = new OrchestratorContextDigestData(context, DateTime.UnixEpoch,
            [new DigestProjectLanes("secret", 1, new Dictionary<string, int>(), "manual", 0, 1),
             new DigestProjectLanes(credential, 1, new Dictionary<string, int>(), "manual", 0, 1)],
            null, [], [], new QuotaReport(),
            [new DigestPublishProject("secret", true, null, [])],
            new TaskWatcherHealthSnapshot(null, 0, 0, null, null, false),
            [new DigestDecision(DateTime.UnixEpoch, "secret", "AGT-1", "decision", credential)]);

        var digest = OrchestratorContextDigestService.RenderDigest(data);

        Assert.Contains("- secret: total=1", digest);
        Assert.Contains("- secret: none derived", digest);
        Assert.DoesNotContain(credential, digest);
        Assert.Contains("Evidence omissions: secret", digest);
    }

    [Fact]
    public void RenderDigest_omits_secrets_and_caps_multichat_context_bytes()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:alpha", out var context));
        var now = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        var secret = "api_key=" + new string('r', 24);
        var data = new OrchestratorContextDigestData(context, now, [], null, [], [],
            new QuotaReport(), [], new TaskWatcherHealthSnapshot(null, 0, 0, null, null, false),
            Enumerable.Range(0, 8).Select(i => new DigestDecision(now, "alpha", $"AGT-{i}",
                "decision", new string('x', 8_000) + " " + secret, $"/api/receipts/{i}")).ToList());

        var digest = OrchestratorContextDigestService.RenderDigest(data);

        Assert.DoesNotContain(secret, digest);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(digest) <= OrchestratorContextDigestService.MaxDigestBytes);
        Assert.Contains("[omitted: oversize context digest]", digest);
    }

    private static TaskInfo Task(string project, string key, string id) => new()
    {
        ProjectName = project,
        Key = key,
        Id = id,
        TaskKey = $"C:/tasks/{project}::{id}",
        State = TaskStates.Ready,
        Title = id,
    };
}
