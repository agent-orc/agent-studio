using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Git;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2220 - the invariant "out-of-band / external completion stamps only with
/// commits that provably exist in the target repository", proven per stamping
/// path with the three cases that actually occurred in production history:
/// the SHA exists, the SHA does not exist, and the ref exists but resolves to a
/// different SHA (the shape that stamped AGT-2220 itself on 28.07.).
/// </summary>
public sealed class DeliveryVerificationTests
{
    [Theory]
    [InlineData(true, true, true, true, DeliveryFenceRefusalAction.ContinueFromResult)]
    [InlineData(false, true, true, true, DeliveryFenceRefusalAction.Escalate)]
    [InlineData(true, false, true, true, DeliveryFenceRefusalAction.Escalate)]
    [InlineData(true, true, false, true, DeliveryFenceRefusalAction.Escalate)]
    [InlineData(true, true, true, false, DeliveryFenceRefusalAction.Escalate)]
    public void Fence_refusal_only_continues_a_published_result_with_both_commits(
        bool baseNotAncestor, bool hasResultRef, bool hasResultSha, bool hasBaseSha,
        DeliveryFenceRefusalAction expected)
    {
        Assert.Equal(expected, DeliveryFenceRefusalPolicy.Decide(
            baseNotAncestor, hasResultRef, hasResultSha, hasBaseSha));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void Fence_repair_is_not_spent_by_the_coding_round_budget(
        bool isFenceRefusal, bool exhausted, bool expected)
    {
        Assert.Equal(expected, DeliveryFenceRefusalPolicy.AllowsContinuation(isFenceRefusal, exhausted));
    }

    // ---------------------------------------------------------------
    // Path 1 + 2: GitService.VerifyDeliveredCommit - the primitive every
    // stamping path funnels through.
    // ---------------------------------------------------------------

    [Fact]
    public void VerifyDeliveredCommit_ShaIsRefTip_Verified()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var result = git.VerifyDeliveredCommit(repo, DeliveryBranch, fixture.Tip);

            Assert.Equal(DeliveryVerificationStatus.Verified, result.Status);
            Assert.True(result.IsVerified);
            Assert.False(result.IsDisproved);
            Assert.Equal(fixture.Tip, result.ResolvedRefSha);
            Assert.Contains("Repository-Verifikation:", result.Note, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void VerifyDeliveredCommit_ShaDoesNotExist_CommitMissing()
    {
        WithRemoteFixture((git, repo, _) =>
        {
            var result = git.VerifyDeliveredCommit(repo, gitRef: null, FabricatedSha);

            Assert.Equal(DeliveryVerificationStatus.CommitMissing, result.Status);
            Assert.False(result.IsVerified);
            Assert.True(result.IsDisproved);
        });
    }

    /// <summary>
    /// The exact AGT-2220 shape: the recorded ref exists, but the repository
    /// holds a different commit and the claimed one is nowhere in that history.
    /// On 28.07. this produced a log warning and a "Done" stamp anyway.
    /// </summary>
    [Fact]
    public void VerifyDeliveredCommit_RefExistsButShaDiffers_ShaMismatch()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var result = git.VerifyDeliveredCommit(repo, DeliveryBranch, DivergentSha(repo, fixture));

            Assert.Equal(DeliveryVerificationStatus.ShaMismatch, result.Status);
            Assert.False(result.IsVerified);
            Assert.True(result.IsDisproved);
            Assert.Equal(fixture.Tip, result.ResolvedRefSha);
            Assert.Contains("nicht enthalten", result.Note, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A commit that is not the tip but IS in the ref's history is a real
    /// delivery - the branch simply moved on. Refusing it would make the
    /// invariant unusable in normal operation, so it verifies (distinctly).
    /// </summary>
    [Fact]
    public void VerifyDeliveredCommit_ShaContainedInRefHistory_VerifiedContained()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var result = git.VerifyDeliveredCommit(repo, DeliveryBranch, fixture.First);

            Assert.Equal(DeliveryVerificationStatus.VerifiedContained, result.Status);
            Assert.True(result.IsVerified);
            Assert.False(result.IsDisproved);
            Assert.Equal(fixture.Tip, result.ResolvedRefSha);
        });
    }

    [Fact]
    public void VerifyDeliveredCommit_RefNotOnRemote_RefMissing()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var result = git.VerifyDeliveredCommit(repo, "runner/agent-runner-01/AGT-9999", fixture.Tip);

            Assert.Equal(DeliveryVerificationStatus.RefMissing, result.Status);
            Assert.True(result.IsDisproved);
        });
    }

    [Fact]
    public void VerifyDeliveredCommit_ShortOrEmptySha_NotVerifiableNeverProof()
    {
        WithRemoteFixture((git, repo, _) =>
        {
            foreach (var claim in new[] { null, "", "  ", "abc123" })
            {
                var result = git.VerifyDeliveredCommit(repo, DeliveryBranch, claim);
                Assert.Equal(DeliveryVerificationStatus.NotVerifiable, result.Status);
                Assert.False(result.IsVerified);
            }
        });
    }

    /// <summary>
    /// Fails closed: a repository we cannot inspect never yields proof. This is
    /// the difference between "disproved" (never stamp) and "could not look"
    /// (record honestly, still no stamp for a proof-requiring card).
    /// </summary>
    [Fact]
    public void VerifyDeliveredCommit_NoOriginRemote_NotVerifiable()
    {
        var root = NewTempRoot("delivery-verify-no-origin-");
        var repo = Path.Combine(root, "repo");
        try
        {
            Directory.CreateDirectory(repo);
            RunGit(root, $"init -q \"{repo}\"");
            RunGit(repo, "config user.email test@example.com");
            RunGit(repo, "config user.name Test");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"chore: base\"");
            var sha = RunGit(repo, "rev-parse HEAD");

            var result = NewGitService(repo).VerifyDeliveredCommit(repo, "main", sha);

            Assert.Equal(DeliveryVerificationStatus.NotVerifiable, result.Status);
            Assert.False(result.IsVerified);
            Assert.False(result.IsDisproved);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------------------------------------------------------------
    // Path 3: crash-recovery attribution - the target repository is the
    // local checkout, so commits[] is gated on local existence.
    // ---------------------------------------------------------------

    [Fact]
    public void CommitExistsInRepo_DistinguishesRealCommitFromFabricatedSha()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            Assert.True(git.CommitExistsInRepo(repo, fixture.Tip));
            Assert.False(git.CommitExistsInRepo(repo, FabricatedSha));
            Assert.False(git.CommitExistsInRepo(repo, ""));
            Assert.False(git.CommitExistsInRepo(repoRoot: null, fixture.Tip));
        });
    }

    // ---------------------------------------------------------------
    // Path 4: the runner completion path's range inspection now classifies
    // its failure, so a disproved delivery can be routed away from
    // 4-auto-review instead of only logging a warning.
    // ---------------------------------------------------------------

    [Fact]
    public void InspectRemoteDeliveryCommitRange_FencedMismatch_IsClassifiedDisproved()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var range = git.InspectRemoteDeliveryCommitRange(
                repo, DeliveryBranch, DivergentSha(repo, fixture), "refs/heads/main");

            Assert.False(range.Success);
            Assert.Equal(DeliveryVerificationStatus.ShaMismatch, range.Verification);
            Assert.True(range.IsDisproved);
            Assert.Contains("Fenced delivery mismatch", range.Warning!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InspectRemoteDeliveryCommitRange_MatchingTip_IsClassifiedVerified()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var range = git.InspectRemoteDeliveryCommitRange(
                repo, DeliveryBranch, fixture.Tip, "refs/heads/main");

            Assert.True(range.Success, range.Warning);
            Assert.Equal(DeliveryVerificationStatus.Verified, range.Verification);
            Assert.False(range.IsDisproved);
        });
    }

    [Fact]
    public void Agt_3245_result_from_salvage_passes_the_fenced_ancestry_check()
    {
        WithRemoteFixture((git, repo, fixture) =>
        {
            var salvageSha = DivergentSha(repo, fixture);
            var refused = git.InspectRemoteDeliveryCommitRange(
                repo, DeliveryBranch, fixture.Tip, "refs/heads/develop", salvageSha);
            Assert.Equal(DeliveryVerificationStatus.BaseNotAncestor, refused.Verification);
            Assert.True(refused.IsDisproved);

            var continuationBranch = "agent-studio/results/agt-3245/continued";
            RunGit(repo, $"checkout -q -b agt-3245-continued {salvageSha}");
            File.WriteAllText(Path.Combine(repo, "continued.txt"), "new attempt work");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"fix(AGT-3245): continue salvage\"");
            var resultSha = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, $"push -q origin HEAD:refs/heads/{continuationBranch}");

            var accepted = git.InspectRemoteDeliveryCommitRange(
                repo, continuationBranch, resultSha, "refs/heads/develop", salvageSha);
            Assert.True(accepted.Success, accepted.Warning);
            Assert.Equal(salvageSha, accepted.MergeBaseSha);
            Assert.Equal(resultSha, Assert.Single(accepted.Commits).Sha);
        });
    }

    [Fact]
    public void InspectRemoteDeliveryCommitRange_MissingRepository_IsNotDisproved()
    {
        var git = NewGitService(Path.GetTempPath());
        var range = git.InspectRemoteDeliveryCommitRange(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")),
            DeliveryBranch,
            FabricatedSha,
            "refs/heads/main");

        Assert.False(range.Success);
        // "Could not look" must never be laundered into disproof - or into proof.
        Assert.Equal(DeliveryVerificationStatus.NotVerifiable, range.Verification);
        Assert.False(range.IsDisproved);
    }

    // ---------------------------------------------------------------
    // The stamping policy itself.
    // ---------------------------------------------------------------

    [Fact]
    public void Policy_VerifiedDelivery_Stamps()
    {
        var verified = new DeliveryVerificationResult(
            DeliveryVerificationStatus.Verified, null, FabricatedSha, "runner/x/AGT-1", FabricatedSha);

        var (decision, reason) = OutOfBandStampPolicy.Decide(
            TaskModes.Coding, TaskStates.Completed, verified);

        Assert.Equal(OutOfBandStampDecision.Stamp, decision);
        Assert.Contains("Repository-Verifikation:", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_ContainedDelivery_Stamps()
    {
        var contained = new DeliveryVerificationResult(
            DeliveryVerificationStatus.VerifiedContained, null, FabricatedSha, "runner/x/AGT-1", "deadbeef");

        var (decision, _) = OutOfBandStampPolicy.Decide(
            TaskModes.Coding, TaskStates.Completed, contained);

        Assert.Equal(OutOfBandStampDecision.Stamp, decision);
    }

    /// <summary>
    /// A claim the repository contradicts is refused in EVERY lane - a false
    /// delivery claim is worse than no claim at all.
    /// </summary>
    [Theory]
    [InlineData(DeliveryVerificationStatus.ShaMismatch, TaskStates.Completed)]
    [InlineData(DeliveryVerificationStatus.ShaMismatch, TaskStates.HumanReview)]
    [InlineData(DeliveryVerificationStatus.RefMissing, TaskStates.HumanReview)]
    [InlineData(DeliveryVerificationStatus.CommitMissing, TaskStates.HumanReview)]
    public void Policy_DisprovedDelivery_IsRefusedInEveryLane(
        DeliveryVerificationStatus status, string targetState)
    {
        var disproved = new DeliveryVerificationResult(
            status, "no", FabricatedSha, "runner/x/AGT-1", null);

        var (decision, _) = OutOfBandStampPolicy.Decide(TaskModes.Coding, targetState, disproved);

        Assert.Equal(OutOfBandStampDecision.RefuseUnverified, decision);
    }

    /// <summary>
    /// REGRESSION - the 11.07. phantom wave. A whole remote wave was stamped
    /// "completed" while nothing had been pushed: the completions carried no
    /// commit claim at all, and the system terminalized the cards anyway.
    /// A terminal lane without proof is now refused.
    /// </summary>
    [Theory]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void Policy_Regression_TerminalLaneWithoutProof_IsRefused(string terminalLane)
    {
        var (decision, reason) = OutOfBandStampPolicy.Decide(
            TaskModes.Coding, terminalLane, verification: null);

        Assert.Equal(OutOfBandStampDecision.RefuseUnverified, decision);
        Assert.Contains("Phantom-Muster", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counter-case that keeps the invariant usable: an operator rescuing a
    /// stuck card into a non-terminal lane rarely has a SHA, and the
    /// worktree-blocked escalation by definition has none. Those still get
    /// reconciled - but as an unproven delivery, without commits[].
    /// </summary>
    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    [InlineData(null)]
    public void Policy_NoClaimIntoNonTerminalLane_ReconcilesAsUnproven(string? targetState)
    {
        var (decision, reason) = OutOfBandStampPolicy.Decide(
            TaskModes.Coding, targetState, verification: null);

        Assert.Equal(OutOfBandStampDecision.StampUnproven, decision);
        Assert.Contains("unbestaetigte Lieferung", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_UnverifiableClaimIntoTerminalLane_IsRefused()
    {
        var unverifiable = DeliveryVerificationResult.NotVerifiable("no origin", FabricatedSha);

        var (decision, _) = OutOfBandStampPolicy.Decide(
            TaskModes.Coding, TaskStates.Completed, unverifiable);

        Assert.Equal(OutOfBandStampDecision.RefuseUnverified, decision);
    }

    /// <summary>
    /// REGRESSION - the historical phantom, end to end against real git: a card
    /// claims a delivery, the target repository holds nothing of the sort, and
    /// the claim must not become a completion stamp.
    /// </summary>
    [Fact]
    public void Regression_CardClaimsDelivery_TargetRepositoryEmpty_NoStamp()
    {
        var root = NewTempRoot("delivery-phantom-");
        var remote = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "repo");
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, $"init -q --bare \"{remote}\"");
            RunGit(root, $"clone -q \"{remote}\" \"{repo}\"");
            RunGit(repo, "config user.email test@example.com");
            RunGit(repo, "config user.name Test");

            // The target repository is empty - nothing was ever pushed.
            var verification = NewGitService(repo)
                .VerifyDeliveredCommit(repo, DeliveryBranch, FabricatedSha);
            var (decision, _) = OutOfBandStampPolicy.Decide(
                TaskModes.Coding, TaskStates.HumanReview, verification);

            Assert.True(verification.IsDisproved);
            // Even the lenient non-terminal lane refuses a contradicted claim.
            Assert.Equal(OutOfBandStampDecision.RefuseUnverified, decision);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Report-only cards deliver a document into the task folder, not commits,
    /// so they carry no repository claim and stay stampable. Without this the
    /// invariant would strand every planning/research card.
    /// </summary>
    [Theory]
    [InlineData(TaskModes.Planning)]
    [InlineData(TaskModes.Research)]
    public void Policy_ReportOnlyModes_NeedNoRepositoryProof(string mode)
    {
        Assert.False(OutOfBandStampPolicy.RequiresRepositoryProof(mode));

        var (decision, _) = OutOfBandStampPolicy.Decide(
            mode, TaskStates.Completed, verification: null);

        Assert.Equal(OutOfBandStampDecision.Stamp, decision);
    }

    [Theory]
    [InlineData(TaskModes.Coding)]
    [InlineData(TaskModes.Concept)]
    [InlineData(null)]
    public void Policy_CommitProducingModes_RequireRepositoryProof(string? mode)
    {
        Assert.True(OutOfBandStampPolicy.RequiresRepositoryProof(mode));
    }

    // ---------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------

    private const string DeliveryBranch = "runner/agent-runner-01/AGT-2220";
    private const string FabricatedSha = "0123456789abcdef0123456789abcdef01234567";

    private sealed record Fixture(string BaseSha, string First, string Tip);

    /// <summary>
    /// A repo whose origin carries <c>main</c>, <c>develop</c> and a delivery
    /// branch with two commits, mirroring the real runner branch layout.
    /// </summary>
    private static void WithRemoteFixture(Action<GitService, string, Fixture> assert)
    {
        var root = NewTempRoot("delivery-verify-");
        var remote = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "repo");
        try
        {
            Directory.CreateDirectory(root);
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

            RunGit(repo, $"checkout -q -b {DeliveryBranch} main");
            File.WriteAllText(Path.Combine(repo, "first.txt"), "first");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"feat: first\"");
            var first = RunGit(repo, "rev-parse HEAD");
            File.WriteAllText(Path.Combine(repo, "second.txt"), "second");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"test(AGT-2220): second\"");
            var tip = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, $"push -q origin {DeliveryBranch}");

            assert(NewGitService(repo), repo, new Fixture(baseSha, first, tip));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A real commit that exists locally but is NOT contained in the pushed
    /// delivery branch - the collision-branch situation AGT-2220 hit.
    /// </summary>
    private static string DivergentSha(string repo, Fixture fixture)
    {
        RunGit(repo, $"checkout -q -b divergent-{Guid.NewGuid():N} {fixture.BaseSha}");
        File.WriteAllText(Path.Combine(repo, "divergent.txt"), "divergent");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m \"feat: divergent work never pushed to the delivery ref\"");
        var sha = RunGit(repo, "rev-parse HEAD");
        RunGit(repo, $"checkout -q {DeliveryBranch}");
        return sha;
    }

    private static GitService NewGitService(string repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
                ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
            }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static string NewTempRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException ex) { System.Diagnostics.Trace.WriteLine(ex); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

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
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed: {error}");
        return output.Trim();
    }
}
