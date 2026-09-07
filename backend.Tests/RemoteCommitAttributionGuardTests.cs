using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteCommitAttributionGuardTests
{
    [Fact]
    public void Attribute_ExactRunnerBranch_PersistsEveryBranchCommitAsAutomatic()
    {
        var commits = new[]
        {
            Commit("1111111111111111111111111111111111111111", "feat: first change"),
            Commit("2222222222222222222222222222222222222222", "test(AGT-2389): cover the change"),
        };

        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2389",
            "runner/agent-runner-01/AGT-2389",
            commits,
            "https://github.com/example/runner.git");

        Assert.True(result.Accepted, result.Warning);
        Assert.Equal(2, result.Commits.Count);
        Assert.All(result.Commits, commit =>
        {
            Assert.Equal(CommitAttributionKinds.Automatic, commit.Attribution);
            Assert.Equal(1.0, commit.Confidence);
            Assert.Equal("https://github.com/example/runner.git", commit.Repository);
            Assert.Equal("runner/agent-runner-01/AGT-2389", commit.Branch);
        });
        Assert.Equal(commits.Select(commit => commit.Sha), result.Commits.Select(commit => commit.Sha));
    }

    [Fact]
    public void Attribute_ImmutableResultEnvelopeRef_IsAcceptedAsTaskNeutral()
    {
        // The envelope ref carries the run id + result SHA, never the task key.
        // Its identity is the fenced result SHA the caller verified, so the
        // task-key suffix rule must not reject it (AGT-2434/AGT-2445: every
        // canonical remote card lost its attributed commits).
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2445",
            "refs/heads/agent-studio/results/run_26f460aab3b64600837bd8a54745bd37/fence-42/3cd7da14403e934f1fc00a4782a24bb799a398a1",
            [Commit("3cd7da14403e934f1fc00a4782a24bb799a398a1", "docs: link research convention")]);

        Assert.True(result.Accepted, result.Warning);
        Assert.Single(result.Commits);
        Assert.Equal(CommitAttributionKinds.Automatic, result.Commits[0].Attribution);
    }

    [Fact]
    public void Attribute_ImmutableResultEnvelopeRef_StillRejectsForeignTaskSubjects()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2445",
            "agent-studio/results/run_26f460aab3b64600837bd8a54745bd37/3cd7da14403e934f1fc00a4782a24bb799a398a1",
            [Commit("1111111111111111111111111111111111111111", "fix(AGT-9999): foreign change")]);

        Assert.False(result.Accepted);
        Assert.Contains("AGT-9999", result.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// AGT-2494 - a divergent salvage parks the run's result on
    /// <c>&lt;card-branch&gt;-collision-&lt;local&gt;-&lt;remote&gt;</c>. Rejecting
    /// that branch as "another task's" is why the reviewed AGT-2220 delivery
    /// arrived with an empty <c>commits[]</c>.
    /// </summary>
    [Fact]
    public void Attribute_DivergentCollisionBranchOfTheSameCard_IsAttributed()
    {
        const string collision =
            "runner/agent-runner-01/AGT-2220"
            + "-collision-f538f896f538f896f538f896f538f896f538f896"
            + "-744deb892744deb892744deb892744deb892744d";

        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2220",
            collision,
            [Commit("f538f896f538f896f538f896f538f896f538f896", "feat(AGT-2220): salvaged work")]);

        Assert.True(result.Accepted, result.Warning);
        Assert.Single(result.Commits);
        // The commit is recorded on the branch that actually holds it.
        Assert.Equal(collision, result.Commits[0].Branch);
    }

    [Fact]
    public void Attribute_CollisionBranchOfAnotherCard_IsStillRejected()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2220",
            "runner/agent-runner-01/AGT-2387"
            + "-collision-f538f896f538f896f538f896f538f896f538f896"
            + "-744deb892744deb892744deb892744deb892744d",
            [Commit("1111111111111111111111111111111111111111", "feat: change")]);

        Assert.False(result.Accepted);
        Assert.Empty(result.Commits);
    }

    /// <summary>
    /// Only the reconciliation's own <c>-collision-&lt;sha&gt;-&lt;sha&gt;</c> shape
    /// is stripped. A hand-made branch that merely mentions the card key must not
    /// inherit the card's attribution.
    /// </summary>
    [Theory]
    [InlineData("runner/agent-runner-01/AGT-2220-collision")]
    [InlineData("runner/agent-runner-01/AGT-2220-collision-f538f896")]
    [InlineData("runner/agent-runner-01/AGT-2220-experiment")]
    public void Attribute_BranchThatOnlyLooksLikeACollisionBranch_IsRejected(string branch)
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2220",
            branch,
            [Commit("1111111111111111111111111111111111111111", "feat: change")]);

        Assert.False(result.Accepted);
        Assert.Empty(result.Commits);
    }

    [Fact]
    public void Attribute_ForeignTaskKeyInAnySubject_RejectsTheWholeRange()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2242",
            "runner/agent-runner-01/AGT-2242",
            [
                Commit("1111111111111111111111111111111111111111", "fix(AGT-2242): own change"),
                Commit("2222222222222222222222222222222222222222", "docs(AGT-2240): foreign change"),
            ]);

        Assert.False(result.Accepted);
        Assert.Empty(result.Commits);
        Assert.Contains("AGT-2240", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Attribute_BranchForAnotherTask_RejectsTheWholeRange()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2386",
            "runner/agent-runner-01/AGT-2387",
            [Commit("1111111111111111111111111111111111111111", "feat: change")]);

        Assert.False(result.Accepted);
        Assert.Empty(result.Commits);
        Assert.Contains("AGT-2386", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectRemoteDeliveryCommitRange_FetchesExactMergeBaseToPushedTip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "remote-attribution-range-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, $"init -q --bare \"{remote}\"");
            RunGit(root, $"clone -q \"{remote}\" \"{repo}\"");
            RunGit(repo, "config user.email test@example.com");
            RunGit(repo, "config user.name Test");
            RunGit(repo, "checkout -q -b main");
            File.WriteAllText(Path.Combine(repo, "base.txt"), "base");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"chore: base\"");
            var baseSha = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, "push -q origin main");
            RunGit(repo, "checkout -q -b develop");
            RunGit(repo, "push -q origin develop");
            RunGit(repo, "checkout -q -b runner/agent-runner-01/AGT-2389 main");
            File.WriteAllText(Path.Combine(repo, "first.txt"), "first");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"feat: first\"");
            var first = RunGit(repo, "rev-parse HEAD");
            File.WriteAllText(Path.Combine(repo, "second.txt"), "second");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"test(AGT-2389): second\"");
            var tip = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, "push -q origin runner/agent-runner-01/AGT-2389");

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                    ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
                }).Build();
            var summary = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                config);
            var scanner = new TaskScannerService(
                config,
                NullLogger<TaskScannerService>.Instance,
                summary);
            var git = new GitService(NullLogger<GitService>.Instance, scanner, config);

            var range = git.InspectRemoteDeliveryCommitRange(
                repo,
                "runner/agent-runner-01/AGT-2389",
                tip,
                "refs/heads/main");

            Assert.True(range.Success, range.Warning);
            Assert.Equal("refs/heads/main", range.IntegrationBranch);
            Assert.Equal(baseSha, range.MergeBaseSha);
            Assert.Equal(tip, range.TipSha);
            Assert.Equal([first, tip], range.Commits.Select(commit => commit.Sha));

            // TE/CAC release tasks can fast-forward main themselves before the
            // remote completion reaches Agent Studio. A live merge-base then is
            // the result itself and the legacy range collapses to zero commits.
            RunGit(repo, "push -q origin HEAD:main");
            var collapsed = git.InspectRemoteDeliveryCommitRange(
                repo,
                "runner/agent-runner-01/AGT-2389",
                tip,
                "refs/heads/main");
            Assert.True(collapsed.Success, collapsed.Warning);
            Assert.Empty(collapsed.Commits);

            var exactEnvelopeRange = git.InspectRemoteDeliveryCommitRange(
                repo,
                "runner/agent-runner-01/AGT-2389",
                tip,
                "refs/heads/main",
                baseSha);
            Assert.True(exactEnvelopeRange.Success, exactEnvelopeRange.Warning);
            Assert.Equal(baseSha, exactEnvelopeRange.MergeBaseSha);
            Assert.Equal([first, tip], exactEnvelopeRange.Commits.Select(commit => commit.Sha));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Windows MAX_PATH regression behind the AGT-2494 fixture failures, in both
    /// manifestations. First, git disambiguates every revision argument against
    /// the working tree, and the <c>lstat</c> of the literal
    /// <c>&lt;sha&gt;..&lt;ref&gt;</c> fails with "Filename too long" instead of
    /// "not found" once checkout path plus argument exceed 260 characters -
    /// <c>rev-list --count</c> then dies with exit 128 on a perfectly valid
    /// range. Second, fetching the ref writes
    /// <c>.git/refs/remotes/origin/&lt;branch&gt;.lock</c>, and once that path
    /// crosses the same limit the fetch dies with "cannot lock ref" unless the
    /// spawn runs with <c>core.longpaths=true</c>. The delivery ref below is
    /// sized so that the range argument AND the remote-tracking loose ref cross
    /// the limit from this fixture's checkout while the refs the fixture itself
    /// writes stay below it; the delivery must still verify and attribute. On
    /// other platforms the test simply exercises a long ref.
    /// </summary>
    [Fact]
    public void InspectRemoteDeliveryCommitRange_LongDeliveryRef_IsNotDisambiguatedAgainstTheWorkingTree()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "remote-attribution-long-ref-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(root);
        try
        {
            // "<sha>..refs/remotes/origin/<branch>" is the argument git stats.
            const int rangeArgumentOverhead = 40 + 2 + 20;
            // "\.git\refs\remotes\origin\<branch>.lock" is written by the product's fetch.
            const int remoteTrackingRefOverhead = 26 + 5;
            // "\.git\refs\heads\<branch>.lock" is written by the fixture's own git.
            const int localHeadsRefOverhead = 17 + 5;
            const string prefix = "runner/agent-runner-01/AGT-2494-collision-";
            var branchLength = Math.Max(prefix.Length + 8, 233 - repo.Length);
            var branch = prefix + new string('f', branchLength - prefix.Length);
            Assert.True(
                repo.Length + 1 + rangeArgumentOverhead + branch.Length > 260,
                "the fixture must push the range argument past MAX_PATH");
            Assert.True(
                repo.Length + remoteTrackingRefOverhead + branch.Length > 260,
                "the fixture must push the fetched remote-tracking ref past MAX_PATH");
            Assert.True(
                repo.Length + localHeadsRefOverhead + branch.Length <= 259,
                "the fixture must keep the refs its own git writes below MAX_PATH");

            RunGit(root, $"init -q --bare \"{remote}\"");
            RunGit(root, $"clone -q \"{remote}\" \"{repo}\"");
            RunGit(repo, "config user.email test@example.com");
            RunGit(repo, "config user.name Test");
            RunGit(repo, "checkout -q -b main");
            File.WriteAllText(Path.Combine(repo, "base.txt"), "base");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"chore: base\"");
            var baseSha = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, "push -q origin main");
            RunGit(repo, $"checkout -q -b {branch} main");
            File.WriteAllText(Path.Combine(repo, "result.txt"), "result");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"feat(AGT-2494): the delivered result\"");
            var tip = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, $"push -q origin {branch}");
            RunGit(repo, "checkout -q main");

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                    ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
                }).Build();
            var summary = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                config);
            var scanner = new TaskScannerService(
                config,
                NullLogger<TaskScannerService>.Instance,
                summary);
            var git = new GitService(NullLogger<GitService>.Instance, scanner, config);

            var range = git.InspectRemoteDeliveryCommitRange(repo, branch, tip, "refs/heads/main");

            Assert.True(range.Success, range.Warning);
            Assert.Equal(DeliveryVerificationStatus.Verified, range.Verification);
            Assert.Equal(baseSha, range.MergeBaseSha);
            Assert.Equal(tip, range.TipSha);
            Assert.Equal([tip], range.Commits.Select(commit => commit.Sha));

            // The same range through the batch helper the board uses.
            Assert.Equal(
                [tip],
                git.GetReachableShaSet(repo, baseSha, $"refs/remotes/origin/{branch}"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static GitCommitInfo Commit(string sha, string subject) =>
        new(
            sha,
            sha[..8],
            DateTime.SpecifyKind(new DateTime(2026, 7, 28), DateTimeKind.Utc),
            "Agent Studio Runner",
            subject,
            1,
            1,
            0);

    private static string RunGit(string cwd, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed: {error}");
        return output.Trim();
    }
}
