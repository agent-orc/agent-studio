using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class AcceptanceIntegrationRoundTripTests
{
    [Fact]
    public async Task FifteenIntegratedDeliveries_AcceptOnOneContainingGreenAfterTenConceptDecisions()
    {
        int[] cards = [3820, 4024, 4029, 4031, 4042, 4069, 4127, 4128, 4129, 3178, 3991, 3971, 3879, 3989, 3481];
        RunGit(_repo, "checkout", "-q", "-B", "integration", "origin/develop");
        for (var i = 0; i < 10; i++)
        {
            var folder = Path.Combine(_repo, "docs", $"concept-{i}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "workbench.json"), """{"status":"decision-pending"}""");
        }
        RunGit(_repo, "add", ".");
        RunGit(_repo, "commit", "-q", "-m", "concepts awaiting decisions");
        var deliveries = new List<(string Key, string Sha)>();
        foreach (var number in cards)
        {
            var key = $"AGT-{number}";
            RunGit(_repo, "checkout", "-q", "-b", key);
            File.WriteAllText(Path.Combine(_repo, key + ".cs"), $"// {key}\n");
            RunGit(_repo, "add", ".");
            RunGit(_repo, "commit", "-q", "-m", key);
            deliveries.Add((key, Git(_repo, "rev-parse", "HEAD").Out.Trim()));
            RunGit(_repo, "checkout", "-q", "integration");
            RunGit(_repo, "merge", "-q", "--no-ff", key, "-m", $"Integrate {key}");
        }
        var tested = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        for (var i = 0; i < 10; i++)
        {
            var folder = Path.Combine(_repo, "docs", $"concept-{i}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "workbench.json"), """{"status":"decided"}""");
            RunGit(_repo, "add", ".");
            RunGit(_repo, "-c", "user.email=agent-studio@users.noreply.github.com", "commit", "-q", "-m",
                $"chore(dossier): accept concept FIX-{i}");
        }
        RunGit(_repo, "push", "-q", "origin", "HEAD:refs/heads/develop");
        var tip = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        var runs = new PostMergeRunStore(Path.Combine(_tempDir, "post-merge"));
        var fixtures = deliveries.Select(d => (d.Key, Deps: Build(d.Sha, taskKey: d.Key,
            slug: d.Key, postMergeRuns: runs))).ToList();
        var first = fixtures[0];
        var registry = new ProjectRegistry(first.Deps.Configuration, NullLogger<ProjectRegistry>.Instance);
        var id = registry.EnsureProjectForStorage(_watchPath, Project, DefaultWorkspace.Id).Id;
        var now = new DateTimeOffset(VerificationFixtureAtUtc);
        runs.ObserveWindow(id, new(tip, now.AddHours(-4), now.AddHours(-3), 30, "no available slot"));
        runs.ObserveWindow(id, new(tip, now.AddHours(-4), now, 30, "no available slot"));
        var card = first.Deps.Scanner.FindJob(first.Key, _watchPath)!;
        var refused = await first.Deps.Transitions.MoveAsync(first.Key, TaskStates.Completed, _watchPath);
        var projected = first.Deps.Integration.BuildProjectionLookup([card])[card.TaskKey];
        Assert.Equal(MoveJobStatus.IntegrationFailed, refused.Status);
        Assert.Contains(tip, refused.Message);
        Assert.Contains("waiting for admission since", refused.Message);
        Assert.Contains("180 minutes ago", refused.Message);
        Assert.Contains("240 minutes ago", refused.Message);
        Assert.Equal(projected.Verification?.Reason, refused.Message);
        var readiness = AcceptanceReadinessPolicy.Classify(new(first.Deps.Transitions.PreviewCompletion(card), card.Mode, null));
        Assert.Equal(refused.Message, readiness.Reason);

        var run = new PostMergeRunRecord("gate-1", Project, "window", tested, null, null, null, cards.Length,
            ["full suite"], [], "running", now, null);
        Assert.NotNull(runs.Record(id, run));
        Assert.NotNull(runs.Record(id, run with { Status = "green", FinishedAtUtc = now,
            Verdicts = [new("full suite", 0, false)] }));
        foreach (var (key, deps) in fixtures)
        {
            var useOverride = key == first.Key;
            var accepted = await deps.Transitions.MoveAsync(key, TaskStates.Completed, _watchPath,
                operatorOverride: useOverride, cause: useOverride ? TimelineActors.Human("operator") : null,
                reason: useOverride ? "Reviewed the containing post-merge receipt." : null);
            Assert.True(accepted.Status == MoveJobStatus.Success, $"{key}: {accepted.Message}");
            var completed = deps.Scanner.FindJob(key, _watchPath)!;
            if (useOverride)
                Assert.Contains(completed.IntegrationRecords, record => record.IntegrationSha == tested);
            else
                Assert.Contains(tested, completed.CompletionClaim?.Evidence);
        }
    }
}
