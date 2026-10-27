using AgentRunner.SourceFacts;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3318: the runner-host producer computes the merge-base diff object,
/// path history and blame of a delivered result and publishes them under its
/// producer epoch; on demand it answers reachability for card provenance.
/// </summary>
public sealed class SourceFactsProducerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "source-facts-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Handoff_publishes_the_diff_object_history_and_blame_of_the_touched_files()
    {
        var (repository, baseSha, headSha) = await SeedAsync();
        var facts = new FakeSourceFactsClient();
        var logs = new List<string>();
        var producer = new SourceFactsProducer(facts, "host-a:1", logs.Add, utcNow: () => Now);

        var result = await producer.PublishHandoffAsync("prj_a", repository, headSha, baseSha, "refs/heads/main", null, default);

        Assert.True(result.Published, string.Join('\n', logs));
        Assert.Equal(baseSha, result.MergeBaseSha);
        var diff = Assert.Single(facts.Diffs.Values);
        Assert.Equal(["src/app.txt", "src/new.txt"], diff.ChangedFiles);
        Assert.Contains(diff.DiffLines, line => line == "+second");
        Assert.Contains(diff.DiffLines, line => line.StartsWith("index ", StringComparison.Ordinal) && line.Length > 80);
        Assert.Equal(SourceDiffObjectOptions.DefaultReview, diff.Options);
        Assert.Equal(
            SourceDiffMaterialDigest.Compute(diff.ChangedFiles, diff.DiffLines, diff.TotalFiles, diff.TotalDiffLines,
                diff.MaximumFiles, diff.MaximumLines),
            diff.ContentSha256);

        var history = facts.Histories.Single(item => item.Request.Path == "src/app.txt").Request;
        Assert.Equal([headSha, baseSha], history.Entries.Select(entry => entry.Sha));
        Assert.Equal("delivery", history.Entries[0].Subject);
        Assert.Contains("Run-Index: 3", history.Entries[0].Body, StringComparison.Ordinal);
        var blame = facts.Blames.Single(item => item.Request.Path == "src/app.txt").Request;
        Assert.Equal([new SourceBlameRange(1, 1, baseSha), new SourceBlameRange(2, 1, headSha)], blame.Ranges);
        Assert.Equal(2, blame.Commits.Count);
        Assert.Equal(2, result.PathFacts);
    }

    [Fact]
    public async Task A_producer_without_the_lease_records_a_demand_and_a_fenced_producer_stops()
    {
        var (repository, baseSha, headSha) = await SeedAsync();
        var facts = new FakeSourceFactsClient { LeaseHolder = "host-b:1" };
        var producer = new SourceFactsProducer(facts, "host-a:1", _ => { }, utcNow: () => Now);

        var skipped = await producer.PublishHandoffAsync(
            "prj_a", repository, headSha, baseSha, "main", "refs/agent-results/a", default);

        Assert.False(skipped.Published);
        Assert.Equal("lease-held-elsewhere", skipped.SkipReason);
        var demand = Assert.Single(facts.Demands).Request;
        Assert.Equal((SourceFactKinds.Diff, baseSha, headSha, "refs/agent-results/a"),
            (demand.Kind, demand.BaseSha, demand.HeadSha, demand.FetchRef));
        Assert.Empty(facts.Diffs);

        // The holder publishes, then loses the repository to a newer epoch:
        // its next publish is refused and nothing is written.
        facts.LeaseHolder = null;
        var holder = new SourceFactsProducer(facts, "host-a:1", _ => { }, utcNow: () => Now);
        await holder.DrainDemandsAsync("prj_a", repository, default);
        facts.TakeOver("host-b:1");
        var fenced = await holder.PublishHandoffAsync("prj_a", repository, headSha, baseSha, "main", null, default);
        Assert.False(fenced.Published);
        Assert.Empty(facts.Diffs);
    }

    [Fact]
    public async Task Demand_drain_answers_reachability_and_diff_demands()
    {
        var (repository, baseSha, headSha) = await SeedAsync();
        var facts = new FakeSourceFactsClient();
        facts.PendingDemands.Add(new SourceFactDemandDto(
            7, "prj_a", SourceFactKinds.Reachability, "card-open-provenance", null, null, null,
            [baseSha, headSha], ["main"], null, 0, null, Now));
        facts.PendingDemands.Add(new SourceFactDemandDto(
            8, "prj_a", SourceFactKinds.Diff, "review-material-miss", baseSha, headSha, null,
            [], [], null, 0, null, Now));
        facts.PendingDemands.Add(new SourceFactDemandDto(
            9, "prj_a", SourceFactKinds.PathHistory, "card-open-file-history", null, headSha, "src/absent.txt",
            [], [], null, 0, null, Now));
        var producer = new SourceFactsProducer(facts, "host-a:1", _ => { }, utcNow: () => Now);

        var answered = await producer.DrainDemandsAsync("prj_a", repository, default);

        Assert.Equal(3, answered);
        var published = Assert.Single(facts.Reachability).Request;
        var mainHead = Assert.Single(published.Heads);
        Assert.Equal(("main", baseSha), (mainHead.Ref, mainHead.Sha));
        Assert.Contains(published.Facts, fact => fact.Sha == baseSha && fact.Contained);
        Assert.Contains(published.Facts, fact => fact.Sha == headSha && !fact.Contained);
        Assert.Single(facts.Diffs);
        Assert.All(facts.Completions, completion => Assert.True(completion.Request.Succeeded));
        Assert.Empty(facts.PendingDemands);
    }

    [Fact]
    public void Blame_and_history_parsers_read_porcelain_and_record_separators()
    {
        var sha = new string('a', 40);
        var other = new string('b', 40);
        var porcelain = string.Join('\n',
            $"{sha} 1 1 2",
            "author Dev",
            "author-mail <dev@example.invalid>",
            "author-time 1790000000",
            "summary first",
            "filename src/app.txt",
            "\tline one",
            $"{sha} 2 2",
            "\tline two",
            $"{other} 3 3 1",
            "author Other",
            "summary second",
            "filename src/app.txt",
            "\tline three");

        var (ranges, commits) = SourcePathFacts.ParseBlame(porcelain)!.Value;

        Assert.Equal([new SourceBlameRange(1, 2, sha), new SourceBlameRange(3, 1, other)], ranges);
        Assert.Equal("Dev <dev@example.invalid>", commits.Single(commit => commit.Sha == sha).Author);
        Assert.Equal("second", commits.Single(commit => commit.Sha == other).Summary);

        var history = SourcePathFacts.ParseHistory(
            $"{sha}\u001f2026-10-27T09:00:00+00:00\u001fDev <d@x>\u001fsubject\u001fsubject\n\nVerdict: pass\u001e\n");
        var entry = Assert.Single(history);
        Assert.Equal((sha, "subject", Now), (entry.Sha, entry.Subject, entry.AuthoredAt!.Value));
        Assert.Contains("Verdict: pass", entry.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Demand_sweep_discovers_repositories_by_their_marker()
    {
        var workDir = Path.Combine(_root, "work");
        var project = Path.Combine(workDir, "prj-a");
        Directory.CreateDirectory(Path.Combine(project, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(workDir, "unmarked", "repo", ".git"));

        Assert.Empty(SourceFactsDemandSweep.DiscoverRepositories(workDir));
        SourceFactsDemandSweep.WriteMarker(project, "prj:a");
        Assert.Equal([("prj:a", Path.Combine(project, "repo"))], SourceFactsDemandSweep.DiscoverRepositories(workDir));
    }

    private async Task<(string Repository, string BaseSha, string HeadSha)> SeedAsync()
    {
        var origin = Path.Combine(_root, "origin.git");
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        await GitAsync(_root, "init", "--bare", "-b", "main", origin);
        await GitAsync(_root, "init", "-b", "main", repository);
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        await File.WriteAllTextAsync(Path.Combine(repository, "src", "app.txt"), "first\n");
        await GitAsync(repository, "add", ".");
        await CommitAsync(repository, "base");
        await GitAsync(repository, "remote", "add", "origin", origin);
        await GitAsync(repository, "push", "origin", "main");
        await GitAsync(repository, "fetch", "origin");
        var baseSha = (await GitAsync(repository, "rev-parse", "HEAD")).StdOut.Trim();
        await GitAsync(repository, "checkout", "-b", "task/a");
        await File.WriteAllTextAsync(Path.Combine(repository, "src", "app.txt"), "first\nsecond\n");
        await File.WriteAllTextAsync(Path.Combine(repository, "src", "new.txt"), "new\n");
        await GitAsync(repository, "add", ".");
        await CommitAsync(repository, "delivery\n\nRun-Index: 3");
        var headSha = (await GitAsync(repository, "rev-parse", "HEAD")).StdOut.Trim();
        return (repository, baseSha, headSha);
    }

    private static Task<ProcessResult> CommitAsync(string repository, string message)
        => GitAsync(repository, "-c", "user.name=Dev", "-c", "user.email=dev@example.invalid", "commit", "-m", message);

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
