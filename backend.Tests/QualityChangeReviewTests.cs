using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.QualityStudio;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class QualityChangeReviewTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-change-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(root, "repo");
    private string Card => Path.Combine(root, "card");

    [Fact]
    public async Task Two_rounds_keep_their_reviewed_merge_base_and_equal_no_ff_landed_patch()
    {
        Init();
        File.WriteAllText(Path.Combine(Repo, "base.txt"), "base\n");
        Git("add", "."); Git("commit", "-m", "base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(Repo, "feature.txt"), "one\n");
        Git("add", "."); Git("commit", "-m", "round one");
        var headOne = Git("rev-parse", "HEAD");
        File.AppendAllText(Path.Combine(Repo, "feature.txt"), "two\n");
        Git("add", "."); Git("commit", "-m", "round two");
        var headTwo = Git("rev-parse", "HEAD");
        Git("checkout", "main");
        File.WriteAllText(Path.Combine(Repo, "other.txt"), "another change\n");
        Git("add", "."); Git("commit", "-m", "other work");
        var integrationTip = Git("rev-parse", "HEAD");
        Assert.Equal(baseSha, Git("merge-base", "main", headOne));
        Assert.Equal(baseSha, Git("merge-base", "main", headTwo));

        var card = new TaskInfo { FolderPath = Card, TaskKey = "AGT-1", WatchPath = Repo,
            Commits = [
                new TaskCommitInfo { Sha = headOne, ResultSha = headOne, DeliveryGeneration = 1,
                    DeliveryAttemptId = "run-1" },
                new TaskCommitInfo { Sha = headTwo, ResultSha = headTwo, DeliveryGeneration = 2,
                    DeliveryAttemptId = "run-2" },
                new TaskCommitInfo { Sha = new string('f', 40), DeliveryGeneration = 1,
                    SupersededBySha = headTwo },
            ] };
        var first = QualityChangeReviewStore.Project(card, Review("review-1", "run-1", headOne),
            Report(baseSha, headOne, "pass"), "repo", Now);
        var second = QualityChangeReviewStore.Project(card, Review("review-2", "run-2", headTwo),
            Report(baseSha, headTwo, "concerns"), "repo", Now.AddMinutes(1));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first.Round);
        Assert.Equal(2, second.Round);
        Assert.Equal(2, second.CommitCount);
        Assert.Equal([headOne, headTwo], QualityChangeReviewStore.Read(Card).Select(item => item.HeadSha));

        Git("merge", "--no-ff", "feature", "-m", "Integrate AGT-1");
        var landed = Git("rev-parse", "HEAD");
        var view = await QualityChangeReviewProjection.BuildAsync(card, "repo", landed,
            (_, sha) => Git("rev-parse", sha + "^1"));
        Assert.Equal(integrationTip, view.LandedBaseSha);
        Assert.True(view.LandedPatchEqualsReviewedPatch);
    }

    [Fact]
    public async Task Conflict_resolution_has_a_distinct_landed_patch()
    {
        Init();
        File.WriteAllText(Path.Combine(Repo, "same.txt"), "base\n");
        Git("add", "."); Git("commit", "-m", "base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(Repo, "same.txt"), "feature\n");
        Git("add", "."); Git("commit", "-m", "feature");
        var head = Git("rev-parse", "HEAD");
        Git("checkout", "main");
        File.WriteAllText(Path.Combine(Repo, "same.txt"), "main\n");
        Git("add", "."); Git("commit", "-m", "main");
        var integrationTip = Git("rev-parse", "HEAD");
        GitAllowFailure("merge", "--no-ff", "feature", "-m", "Integrate AGT-2");
        File.WriteAllText(Path.Combine(Repo, "same.txt"), "resolved\n");
        Git("add", "."); Git("commit", "-m", "Resolve AGT-2");
        var landed = Git("rev-parse", "HEAD");
        var card = new TaskInfo { FolderPath = Card, TaskKey = "AGT-2", WatchPath = Repo };
        QualityChangeReviewStore.Project(card, Review("review-2", "run-2", head),
            Report(baseSha, head, "pass"), "repo", Now);
        var view = await QualityChangeReviewProjection.BuildAsync(card, "repo", landed,
            (_, sha) => Git("rev-parse", sha + "^1"));
        Assert.Equal(integrationTip, view.LandedBaseSha);
        Assert.False(view.LandedPatchEqualsReviewedPatch);
    }

    [Fact]
    public void Infrastructure_and_outdated_ranges_never_present_as_pass()
    {
        var round = new QualityChangeReviewRound(2, "attempt", "run", "repo",
            new string('a', 40), new string('b', 40), 1, "decide", Now,
            JsonSerializer.SerializeToElement(new { verdict = "timeout" }));
        Assert.Equal("unavailable", QualityChangeReviewProjection.State("repo", round.HeadSha, round, "4-review"));
        Assert.Equal("outdated", QualityChangeReviewProjection.State("repo", new string('c', 40), round, "4-review"));
        Assert.Equal("not-registered", QualityChangeReviewProjection.State(null, round.HeadSha, round, "4-review"));
    }

    private void Init()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Card);
        Git("init", "-b", "main");
        Git("config", "user.email", "fixture@example.test");
        Git("config", "user.name", "Fixture");
    }

    private string Git(params string[] args) => RunGit(false, args);
    private string GitAllowFailure(params string[] args) => RunGit(true, args);
    private string RunGit(bool allowFailure, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") {
            WorkingDirectory = Repo, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!allowFailure) Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private static AgentStudio.Shared.ReviewAttemptDto Review(string attempt, string run, string head)
    {
        var now = Now;
        return new AgentStudio.Shared.ReviewAttemptDto(attempt, "AGT-1", "repo", run, null,
            new AgentStudio.Shared.ReviewSubjectDto("subject", "repo", head, run, "requirements", "policy", [], now),
            AttemptLifecycleState.Completed, null, 1, 1, now, now,
            ReviewTerminalOutcome.Pass, null, head, null, []);
    }

    private static ReviewReportRequest Report(string baseSha, string head, string verdict)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            schema = "https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v1.schema.json",
            schemaVersion = 1, repository = "repo", baseSha, headSha = head, verdict,
            policyHash = "sha256:" + new string('a', 64), ruleSetHash = "sha256:" + new string('b', 64),
            catalogueVersion = "1.0", findings = Array.Empty<object>() });
        return new ReviewReportRequest("executor", "instance", "lease", 1, "report", "Pass", null, null,
            new ReviewWorkspaceProofDto("repo", head, head, new string('c', 40), false, false,
                "workspace", "resource", MergeBaseSha: baseSha),
            new ReviewEnvironmentDto("host", "executor", "instance", "linux", "x64", "10",
                new Dictionary<string, string>(), new Dictionary<string, string>()), [],
            [new ReviewArtifactEvidenceDto(QualityChangeReviewStore.ArtifactName, "application/json",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length,
                Convert.ToBase64String(bytes))], [], QualityStudioCodeQualityMode: "decide");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
    }
}
