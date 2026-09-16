using Microsoft.AspNetCore.Http;

using Xunit;

namespace AgentStudio.Tests;

public class TaskQueryEngineTests : IDisposable
{
    private readonly string _root;

    public TaskQueryEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-task-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Execute_FiltersSortsAndPagesWithTotal()
    {
        var jobs = new[]
        {
            Job("a", TaskStates.Ready, "alpha", commits: 0, lastActivity: Utc(10), createdAt: Utc(1), cliType: "claude"),
            Job("b", TaskStates.AutoReview, "beta", commits: 2, lastActivity: Utc(20), createdAt: Utc(2), cliType: "codex"),
            Job("c", TaskStates.AutoReview, "gamma", commits: 4, lastActivity: Utc(30), createdAt: Utc(3), cliType: "codex"),
        };

        var query = Query("state=4-auto-review&cliType=codex&minCommits=2&sortBy=commits&order=desc&limit=1&offset=0&fields=id,commits,state");
        var response = TaskQueryEngine.Execute(jobs, query);

        Assert.Null(response.Error);
        Assert.Equal(2, response.Total);
        var only = Assert.Single(response.Items);
        var item = Assert.IsType<Dictionary<string, object?>>(only);
        Assert.Equal("c", item["id"]);
        Assert.Equal(4, item["commits"]);
        Assert.Equal(TaskStates.AutoReview, item["state"]);
    }

    [Fact]
    public void Execute_GrepFindsLiteralAndRegexMatchesWithEvidence()
    {
        var literal = Job("literal", TaskStates.HumanReview, "literal title", folder: MakeFolder("literal"));
        Write(Path.Combine(literal.FolderPath, "logs", "cli-output.log"),
            "[taskboard] failed\nEACCES: permission denied\n");
        var regex = Job("regex", TaskStates.HumanReview, "regex title", folder: MakeFolder("regex"));
        Write(Path.Combine(regex.FolderPath, "prompt.md"), "Please fix error ATP-123 now.\n");

        var literalResponse = TaskQueryEngine.Execute([literal, regex], Query("q=eacces&in=output"));
        Assert.Equal(1, literalResponse.Total);
        var literalItem = Assert.IsType<TaskSearchResult>(literalResponse.Items[0]);
        Assert.Equal("literal", literalItem.Id);
        Assert.Equal("logs/cli-output.log", literalItem.Match.File);
        Assert.Equal(2, literalItem.Match.Line);
        Assert.Contains("EACCES", literalItem.Match.Snippet);

        var regexResponse = TaskQueryEngine.Execute(
            [literal, regex],
            Query($"q={Uri.EscapeDataString("ATP-[0-9]+")}&regex=true&in=prompt"));
        Assert.Equal(1, regexResponse.Total);
        var regexItem = Assert.IsType<TaskSearchResult>(regexResponse.Items[0]);
        Assert.Equal("regex", regexItem.Id);
        Assert.Equal("prompt.md", regexItem.Match.File);
    }

    [Fact]
    public void Execute_InvalidRegexReturnsBadRequestPayload()
    {
        var job = Job("a", TaskStates.Ready, "alpha");

        var response = TaskQueryEngine.Execute([job], Query("q=%5B&regex=true"));

        Assert.StartsWith("Invalid regex:", response.Error);
        Assert.Empty(response.Items);
    }

    [Fact]
    public void Execute_AggregatesFilteredRows()
    {
        var jobs = new[]
        {
            Job("a", TaskStates.Ready, "alpha", cliType: "claude"),
            Job("b", TaskStates.AutoReview, "beta", cliType: "codex", verdict: "reissue", issue: "classifier-unknown"),
            Job("c", TaskStates.AutoReview, "gamma", cliType: "codex", verdict: "accept"),
        };

        var response = TaskQueryEngine.Execute(jobs, Query("state=4-auto-review&aggregate=state,verdict,issueKind,cliType"));

        Assert.Null(response.Error);
        Assert.Equal(2, response.Total);
        Assert.NotNull(response.Aggregates);
        Assert.Equal(2, response.Aggregates!["state"][TaskStates.AutoReview]);
        Assert.Equal(1, response.Aggregates["verdict"]["reissue"]);
        Assert.Equal(1, response.Aggregates["verdict"]["accept"]);
        Assert.Equal(1, response.Aggregates["issueKind"]["classifier-unknown"]);
        Assert.Equal(1, response.Aggregates["issueKind"]["(none)"]);
        Assert.Equal(2, response.Aggregates["cliType"]["codex"]);
    }

    [Fact]
    public void Execute_DurationFilterUsesLatestCliOutputDuration()
    {
        var fast = Job("fast", TaskStates.AutoReview, "fast", folder: MakeFolder("fast"));
        Write(Path.Combine(fast.FolderPath, "logs", "cli-output.log"),
            "[taskboard] codex CLI exited: status=completed, exitCode=0, duration=4.2s\n");
        var slow = Job("slow", TaskStates.AutoReview, "slow", folder: MakeFolder("slow"));
        Write(Path.Combine(slow.FolderPath, "logs", "cli-output.log"),
            "[taskboard] codex CLI exited: status=completed, exitCode=0, duration=90s\n");

        var response = TaskQueryEngine.Execute([fast, slow], Query("durationMin=30&sortBy=duration&fields=id,duration"));

        Assert.Equal(1, response.Total);
        var item = Assert.IsType<Dictionary<string, object?>>(response.Items[0]);
        Assert.Equal("slow", item["id"]);
        Assert.Equal(90d, item["duration"]);
    }

    [Fact]
    public void Execute_FiltersByAreaAndTagAsAConjunction()
    {
        var delivery = Job("delivery", TaskStates.Ready, "merge bounce", tags: ["delivery-chain", "incident"]);
        var observation = Job("observation", TaskStates.Ready, "watcher probe", tags: ["observation", "incident"]);
        var untagged = Job("untagged", TaskStates.Ready, "nothing");
        TaskInfo[] jobs = [delivery, observation, untagged];

        // area alone
        Assert.Equal(["delivery"], Ids(TaskQueryEngine.Execute(jobs, Query("area=delivery-chain&fields=id"))));
        // areas inside one parameter are alternatives
        Assert.Equal(["delivery", "observation"],
            Ids(TaskQueryEngine.Execute(jobs, Query("area=delivery-chain,observation&sortBy=key&order=asc&fields=id"))));
        // area and tag narrow each other
        Assert.Equal(["delivery"],
            Ids(TaskQueryEngine.Execute(jobs, Query("area=delivery-chain&tag=incident&fields=id"))));
        Assert.Empty(Ids(TaskQueryEngine.Execute(jobs, Query("area=delivery-chain&tag=evidence&fields=id"))));
    }

    [Fact]
    public void FromQuery_TreatsAreaAsAnAnalysisParameter() =>
        Assert.True(Query("area=observation").IsActive);

    private static List<object?> Ids(TaskQueryResponse response) =>
        [.. response.Items
            .Cast<Dictionary<string, object?>>()
            .Select(item => item["id"])];

    private static TaskQueryRequest Query(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + queryString);
        return TaskQueryRequest.FromQuery(context.Request.Query);
    }

    private TaskInfo Job(
        string id,
        string state,
        string title,
        int commits = 0,
        DateTime? lastActivity = null,
        DateTime? createdAt = null,
        string? cliType = null,
        string? verdict = null,
        string? issue = null,
        string? folder = null,
        List<string>? tags = null)
    {
        folder ??= MakeFolder(id);
        return new TaskInfo
        {
            Tags = tags ?? [],
            Id = id,
            Key = "ATP-" + id,
            TaskKey = _root + "::" + id,
            Title = title,
            State = state,
            ProjectName = "project-a",
            WatchPath = _root,
            FolderPath = folder,
            LastActivity = lastActivity ?? Utc(1),
            CreatedAt = createdAt ?? Utc(1),
            CliType = cliType,
            OrchestratorVerdict = verdict,
            OutcomeIssue = issue == null ? null : new TaskOutcomeIssue { Kind = issue, Label = issue },
            Commits = Enumerable.Range(0, commits)
                .Select(i => new TaskCommitInfo { Sha = $"sha-{id}-{i}", Message = "commit" })
                .ToList()
        };
    }

    private string MakeFolder(string id)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        return dir;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static DateTime Utc(int minute)
        => new(2026, 6, 8, 12, minute, 0, DateTimeKind.Utc);
}
