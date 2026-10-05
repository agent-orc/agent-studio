using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2831 reproduction: four review workers on one host, all reviewing the
/// same subject, which is the shape that deadlocked
/// <c>agent-runner-01-review</c> at <c>RUNNER_MAX_PARALLELISM=4</c>.
///
/// The attempt workspace already fences file paths per attempt, but the .NET
/// build servers do not live under <c>TMPDIR</c>: Roslyn's VBCSCompiler listens
/// on <c>/tmp/&lt;pipename&gt;</c> and reusable MSBuild nodes on
/// <c>/tmp/MSBuild&lt;pid&gt;</c>, both host-global for one user and one SDK. The
/// first test proves the build namespace is now fenced per attempt; the second
/// proves that a tree which blocks on a host-shared handle anyway is reaped on a
/// bounded budget and classified as infrastructure instead of sitting on its
/// review slot.
/// </summary>
public sealed class ReviewConcurrentWorkspaceIsolationTests : IDisposable
{
    private const int Workers = 4;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "review-parallel-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _reviewRoot;
    private readonly string _collector;

    public ReviewConcurrentWorkspaceIsolationTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _reviewRoot = Path.Combine(_root, "review");
        _collector = Path.Combine(_root, "collector");
    }

    [SkippableFact]
    public async Task Four_concurrent_attempts_of_one_subject_get_four_private_dotnet_build_namespaces()
    {
        Skip.IfNot(PosixShell.IsAvailable, "The probe command is a POSIX shell script.");
        var sha = await SeedOriginAsync();
        Directory.CreateDirectory(_collector);

        var evidence = await Task.WhenAll(Enumerable.Range(0, Workers).Select(async index =>
        {
            var attemptId = $"attempt-parallel-{index}";
            var report = Path.Combine(_collector, attemptId + ".env");
            var workspace = Workspace(
                attemptId,
                sha,
                [Probe(report)],
                portBase: 27000 + (index * 8),
                noCpuProgressSeconds: 0);
            await workspace.PrepareAsync(null!, default);
            return await workspace.ExecutePlanAsync(default);
        }));

        Assert.All(evidence, item => Assert.Equal("Pass", item.Outcome));
        var observed = Enumerable.Range(0, Workers)
            .Select(index => ReadReport(Path.Combine(_collector, $"attempt-parallel-{index}.env")))
            .ToArray();

        // Every attempt must run without any host-shared .NET build server.
        Assert.All(observed, item =>
        {
            Assert.Equal("1", item["nodeReuse"]);
            Assert.Equal("0", item["msbuildServer"]);
            Assert.Equal("false", item["sharedCompilation"]);
        });

        // And the writable namespaces stay pairwise disjoint under concurrency.
        foreach (var key in new[] { "debugPath", "tmpdir", "home", "port" })
            Assert.Equal(
                Workers,
                observed.Select(item => item[key]).Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task A_verify_command_blocked_on_a_host_shared_handle_is_reaped_as_review_infrastructure()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The hang watchdog reads process CPU time from /proc.");
        Skip.IfNot(PosixShell.IsAvailable, "The probe command is a POSIX shell script.");
        var sha = await SeedOriginAsync();
        // One host-global handle with no writer, standing in for the shared
        // VBCSCompiler socket: every reader blocks in the kernel at zero CPU.
        var rendezvous = Path.Combine(_root, "shared-handle");
        Assert.True((await ProcessRunner.RunAsync("mkfifo", [rendezvous], _root)).Success);

        var started = DateTime.UtcNow;
        var failures = await Task.WhenAll(Enumerable.Range(0, Workers).Select(async index =>
        {
            var workspace = Workspace(
                $"attempt-stall-{index}",
                sha,
                // AGT-2851: the effective no-CPU-progress window is never smaller
                // than half the command's own budget, so a 2s floor against a
                // 20s budget still ends on the watchdog (~10s), not the clock.
                [Shell("verify-stall", $"read line < {Quote(rendezvous)}", timeoutSeconds: 20)],
                portBase: 27100 + (index * 8),
                noCpuProgressSeconds: 2);
            await workspace.PrepareAsync(null!, default);
            return await Assert.ThrowsAsync<ReviewInfrastructureException>(
                () => workspace.ExecutePlanAsync(default));
        }));
        var elapsed = DateTime.UtcNow - started;

        Assert.All(failures, failure =>
        {
            Assert.Equal("NoCpuProgress", failure.Classification);
            Assert.Contains("detector=no-cpu-progress", failure.Message, StringComparison.Ordinal);
            Assert.Contains("verify-stall", failure.Message, StringComparison.Ordinal);
        });
        // The incident held four slots for the full budget. The bound is now the
        // watchdog window, not the command timeout.
        Assert.True(
            elapsed < TimeSpan.FromMinutes(2),
            $"four stalled attempts should end on the watchdog window, took {elapsed}");
    }

    [SkippableFact]
    public async Task A_command_that_keeps_working_is_never_reaped_by_the_hang_watchdog()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The hang watchdog reads process CPU time from /proc.");
        Skip.IfNot(PosixShell.IsAvailable, "The probe command is a POSIX shell script.");
        var sha = await SeedOriginAsync();
        var workspace = Workspace(
            "attempt-busy",
            sha,
            // Busy for longer than the watchdog window, so only consumed CPU can
            // tell it apart from the stalled case above.
            [Shell(
                "verify-busy",
                "end=$(( $(date +%s) + 5 )); while [ $(date +%s) -lt $end ]; do :; done",
                timeoutSeconds: 600)],
            portBase: 27200,
            noCpuProgressSeconds: 2);
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        var command = Assert.Single(evidence.Commands, item => item.StepId == "verify-busy");
        Assert.Null(command.Signal);
        Assert.Equal(0, command.ExitCode);
    }

    private static ReviewCommandDto Probe(string reportPath)
        => Shell(
            "verify-isolation",
            "printf 'nodeReuse=%s\\nmsbuildServer=%s\\nsharedCompilation=%s\\n" +
            "debugPath=%s\\ntmpdir=%s\\nhome=%s\\nport=%s\\n' " +
            "\"$MSBUILDDISABLENODEREUSE\" \"$DOTNET_CLI_USE_MSBUILD_SERVER\" " +
            "\"$UseSharedCompilation\" \"$MSBUILDDEBUGPATH\" \"$TMPDIR\" \"$HOME\" \"$PORT\" " +
            $"> {Quote(reportPath)}",
            timeoutSeconds: 60);

    private static ReviewCommandDto Shell(string stepId, string script, int timeoutSeconds)
        => new(
            stepId,
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", script],
            TimeoutSeconds: timeoutSeconds);

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static Dictionary<string, string> ReadReport(string path)
    {
        Assert.True(File.Exists(path), $"the review command wrote no report at {path}");
        return File.ReadAllLines(path)
            .Where(line => line.Contains('=', StringComparison.Ordinal))
            .ToDictionary(
                line => line[..line.IndexOf('=', StringComparison.Ordinal)],
                line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
                StringComparer.Ordinal);
    }

    private RemoteReviewWorkspace Workspace(
        string attemptId,
        string sha,
        IReadOnlyList<ReviewCommandDto> commands,
        int portBase,
        int noCpuProgressSeconds)
    {
        var repositoryId = TaskServerClient.RepositoryIdentity(_origin)!;
        // Every worker reviews the same immutable subject; only the fenced
        // attempt identity and the port window differ.
        var subject = new ReviewSubjectDto(
            "subject-shared",
            "task-shared",
            "run-" + attemptId,
            repositoryId,
            _origin,
            sha,
            "refs/heads/main",
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(commands, commands.Select(command => command.Aspect).ToArray()),
            DateTime.UtcNow);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            1,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(10),
            "active",
            $"review-{attemptId}-f1",
            portBase);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "review-executor",
            RunnerName = "review-executor",
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = _reviewRoot,
            BaseBranch = "main",
            ClaudeCliBin = "unused",
            TtlSeconds = 600,
            HeartbeatSeconds = 30,
            ReviewNoCpuProgressSeconds = noCpuProgressSeconds,
        };
        return new RemoteReviewWorkspace(options, subject, lease, _ => { });
    }

    private async Task<string> SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "immutable product");
        await GitAsync(seed, "add", "product.txt");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "result");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    public void Dispose() => ResilientDirectory.TryDelete(_root);
}
