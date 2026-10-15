using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3061: the integration role publishes an accepted, fenced result to the
/// integration ref with a real Git remote, never with force.
/// </summary>
public sealed class IntegrationPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agent-runner-integration-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    [Fact]
    public async Task Fast_forward_publishes_the_result_and_a_repeat_reports_it_contained()
    {
        var (origin, work) = await RepositoryAsync();
        var result = await CommitAsync(work, "feature.txt", "feature", "Feature");
        await Git(work, "push", "-q", origin, "HEAD:refs/heads/results/one");

        var published = await IntegrationPublisher.PublishAsync(
            Command(origin, "refs/heads/develop", "refs/heads/results/one", result), 1, Path.Combine(_root, "state"), _log.Add, default);

        Assert.Equal(IntegrationOutcomeKinds.Integrated, published.Outcome);
        Assert.Equal(result, published.IntegratedSha);
        Assert.Equal(result, await RevParseAsync(origin, "refs/heads/develop"));
        var repeat = await IntegrationPublisher.PublishAsync(
            Command(origin, "refs/heads/develop", "refs/heads/results/one", result), 2, Path.Combine(_root, "state"), _log.Add, default);
        Assert.Equal(IntegrationOutcomeKinds.AlreadyIntegrated, repeat.Outcome);
        Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "state")).Any());
    }

    [Fact]
    public async Task A_moved_integration_ref_is_merged_and_a_conflict_is_reported_without_a_push()
    {
        var (origin, work) = await RepositoryAsync();
        var baseSha = await RevParseAsync(work, "HEAD");
        var result = await CommitAsync(work, "feature.txt", "feature", "Feature");
        await Git(work, "push", "-q", origin, "HEAD:refs/heads/results/two");
        await Git(work, "checkout", "-q", "--detach", baseSha);
        await CommitAsync(work, "other.txt", "other", "Other work");
        await Git(work, "push", "-q", origin, "HEAD:refs/heads/develop");
        var moved = await RevParseAsync(origin, "refs/heads/develop");

        var merged = await IntegrationPublisher.PublishAsync(
            Command(origin, "develop", "refs/heads/results/two", result), 1, Path.Combine(_root, "state"), _log.Add, default);

        Assert.Equal(IntegrationOutcomeKinds.Integrated, merged.Outcome);
        var tip = await RevParseAsync(origin, "refs/heads/develop");
        Assert.Equal(tip, merged.IntegratedSha);
        Assert.True((await Run(origin, "merge-base", "--is-ancestor", result, tip)).Success);
        Assert.True((await Run(origin, "merge-base", "--is-ancestor", moved, tip)).Success);

        await Git(work, "checkout", "-q", "--detach", baseSha);
        var conflicting = await CommitAsync(work, "feature.txt", "conflict", "Conflicting");
        await Git(work, "push", "-q", origin, "HEAD:refs/heads/results/three");
        var conflict = await IntegrationPublisher.PublishAsync(
            Command(origin, "develop", "refs/heads/results/three", conflicting), 1, Path.Combine(_root, "state"), _log.Add, default);

        Assert.Equal(IntegrationOutcomeKinds.Conflict, conflict.Outcome);
        Assert.Null(conflict.IntegratedSha);
        Assert.Equal(tip, await RevParseAsync(origin, "refs/heads/develop"));
    }

    [Fact]
    public async Task A_result_ref_that_names_another_sha_is_refused()
    {
        var (origin, work) = await RepositoryAsync();
        await CommitAsync(work, "feature.txt", "feature", "Feature");
        await Git(work, "push", "-q", origin, "HEAD:refs/heads/results/four");

        var refused = await IntegrationPublisher.PublishAsync(
            Command(origin, "develop", "refs/heads/results/four", new string('9', 40)),
            1, Path.Combine(_root, "state"), _log.Add, default);

        Assert.Equal(IntegrationOutcomeKinds.Failed, refused.Outcome);
        Assert.NotEqual(new string('9', 40), await RevParseAsync(origin, "refs/heads/develop"));
    }

    private static IntegrationCommandDto Command(
        string origin, string integrationRef, string resultRef, string resultSha)
        => new("int_test", "prj", "tsk", "orch", "run", "review", "repo", origin, integrationRef,
            resultRef, resultSha, IntegrationCommandStates.Leased, 1, DateTime.UnixEpoch, DateTime.UnixEpoch);

    private async Task<(string Origin, string Work)> RepositoryAsync()
    {
        var origin = Path.Combine(_root, "origin.git");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(origin);
        Directory.CreateDirectory(work);
        await Git(origin, "init", "-q", "--bare");
        await Git(work, "init", "-q");
        await CommitAsync(work, "README.md", "base", "Base");
        await Git(work, "push", "-q", origin, "HEAD:refs/heads/develop");
        return (origin, work);
    }

    private static async Task<string> CommitAsync(string work, string file, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(work, file), content + "\n");
        await Git(work, "add", file);
        await Git(work, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "-m", message);
        return await RevParseAsync(work, "HEAD");
    }

    private static async Task<string> RevParseAsync(string directory, string revision)
        => (await Run(directory, "rev-parse", revision)).StdOut.Trim();

    private static async Task Git(string directory, params string[] args)
    {
        var result = await Run(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
    }

    private static Task<ProcessResult> Run(string directory, params string[] args)
        => ProcessRunner.RunAsync("git", args, directory, ct: CancellationToken.None);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
