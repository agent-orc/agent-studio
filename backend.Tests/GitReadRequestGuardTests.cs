using System.Diagnostics;
using AgentStudio.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Behaviour of the Git process boundary and the read-request guard
/// (AGT-3321, Git facts S1). The test process runs the guard in strict mode
/// (<see cref="GitReadGuardBootstrap"/>), so these tests read the mode and do
/// not change it: other tests run in parallel against the same static guard.
/// </summary>
public sealed class GitReadRequestGuardTests
{
    private const string UnlistedRoute = "/api/git-read-guard-tests/unlisted";

    [Fact]
    public void Test_process_runs_the_guard_in_strict_mode()
        => Assert.Equal(GitReadGuardMode.Strict, GitReadRequestGuard.Mode);

    [Fact]
    public async Task Boundary_registers_the_child_while_it_runs_and_records_telemetry()
    {
        var cwd = Path.GetTempPath();
        IReadOnlyList<string>? liveDuringStart = null;
        using var telemetry = GitProcessTelemetry.BeginRequest("git-read-guard-tests/boundary", NullLogger.Instance);

        var result = await GitProcessBoundary.RunAsync(GitVersion(cwd), stdin: null, TimeSpan.FromSeconds(30),
            CancellationToken.None, start =>
            {
                liveDuringStart = GitChildProcessRegistry.WorkingDirectories();
                return Process.Start(start);
            });

        Assert.True(result.Success, result.StandardError);
        Assert.Contains(cwd, liveDuringStart!);
        Assert.Equal(1, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
    }

    [Fact]
    public async Task Strict_guard_rejects_an_unlisted_read_before_git_starts()
    {
        var started = false;
        using var scope = GitReadRequestGuard.BeginRead("GET", UnlistedRoute);

        var error = await Assert.ThrowsAsync<GitProcessOnReadRequestException>(() => GitProcessBoundary.RunAsync(
            GitVersion(Path.GetTempPath()), stdin: null, TimeSpan.FromSeconds(30), CancellationToken.None, start =>
            {
                started = true;
                return Process.Start(start);
            }));

        Assert.False(started);
        Assert.Equal(UnlistedRoute, error.RouteTemplate);
        Assert.Equal("version", error.Command);
        Assert.Equal(GitReadSpawnVerdict.Unlisted, scope.Verdict);
        Assert.True(scope.ThrownAtSpawn);
    }

    [Fact]
    public void Scope_allows_a_frozen_route_up_to_its_ceiling_and_flags_the_next_spawn()
    {
        var exception = new GitReadSpawnException("GET", "/api/example/{id}", 2, "owner", "S3", "R4");
        using var scope = new GitReadRequestScope("GET", exception.RouteTemplate, exception, null, NullLogger.Instance);

        Assert.Equal(GitReadSpawnVerdict.Allowed, scope.RecordSpawn("rev-parse"));
        Assert.Equal(GitReadSpawnVerdict.Allowed, scope.RecordSpawn("merge-base"));
        Assert.Equal(GitReadSpawnVerdict.CeilingBreached, scope.RecordSpawn("merge-base"));
        Assert.Equal(["rev-parse", "merge-base", "merge-base"], scope.Commands);
        Assert.True(scope.Violated);
    }

    [Fact]
    public void Spawn_after_the_request_ended_counts_as_detached_not_as_a_violation()
    {
        var route = "/api/git-read-guard-tests/detached/" + Guid.NewGuid().ToString("N");
        var scope = new GitReadRequestScope("GET", route, null, null, NullLogger.Instance);
        scope.Dispose();

        Assert.Equal(GitReadSpawnVerdict.Detached, scope.RecordSpawn("fetch"));
        Assert.Empty(scope.Commands);
        var debt = Assert.Single(GitReadRequestGuard.Ledger.Snapshot(), entry => entry.RouteTemplate == route);
        Assert.Equal(1, debt.DetachedSpawns);
        Assert.Equal(0, debt.UnlistedRequests);
    }

    [Fact]
    public void Detach_leaves_the_read_scope_for_background_work_and_restores_it()
    {
        using var scope = GitReadRequestGuard.BeginRead("GET", UnlistedRoute);
        using (GitReadRequestGuard.Detach())
        {
            Assert.Null(GitReadRequestGuard.Current);
            GitReadRequestGuard.OnGitProcessStarting("fetch");
        }

        Assert.Same(scope, GitReadRequestGuard.Current);
        Assert.Empty(scope.Commands);
    }

    [Fact]
    public void Ledger_counts_unlisted_and_breached_requests_per_route()
    {
        var route = "/api/git-read-guard-tests/ledger/" + Guid.NewGuid().ToString("N");
        using (var scope = new GitReadRequestScope("GET", route, null, null, NullLogger.Instance))
        {
            scope.RecordSpawn("log");
            scope.RecordSpawn("show");
        }
        using (new GitReadRequestScope("GET", route, null, null, NullLogger.Instance))
        {
        }

        var debt = Assert.Single(GitReadRequestGuard.Ledger.Snapshot(), entry => entry.RouteTemplate == route);
        Assert.Equal(1, debt.RequestsWithSpawns);
        Assert.Equal(2, debt.Spawns);
        Assert.Equal(2, debt.MaxSpawnsPerRequest);
        Assert.Equal(1, debt.UnlistedRequests);
        Assert.Null(debt.SpawnCeiling);
    }

    [Theory]
    [InlineData(new[] { "-C", "/repo", "log", "--oneline" }, "log")]
    [InlineData(new[] { "-c", "core.quotepath=off", "diff", "--name-only" }, "diff")]
    [InlineData(new[] { "--git-dir=/repo.git", "cat-file", "--batch" }, "cat-file")]
    [InlineData(new string[0], "git")]
    public void Command_label_is_the_subcommand_without_paths_or_options(string[] args, string expected)
    {
        var start = new ProcessStartInfo("git") { CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        Assert.Equal(expected, GitProcessBoundary.CommandLabel(start));
    }

    private static ProcessStartInfo GitVersion(string cwd)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("version");
        return start;
    }
}
