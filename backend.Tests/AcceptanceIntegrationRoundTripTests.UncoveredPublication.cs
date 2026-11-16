using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3753 - when a later first-parent publication turns a verified delivery
/// into unverified at acceptance, the refusal names that commit (short SHA and
/// subject) and what each gate source answered for it, and the Human Review
/// card detail projection shows the same reason.
/// </summary>
public sealed partial class AcceptanceIntegrationRoundTripTests
{
    [Fact]
    public async Task Acceptance_EarlierCard_AfterGateNotApplicableLaterCard_Succeeds()
    {
        var earlier = PublishDelivery("earlier-no-gate.txt", "earlier\n");
        RunGit(_repo, "push", "-q", "origin", $"{earlier}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(earlier);
        var earlierCard = deps.Scanner.FindJob(Slug, _watchPath)!;
        IntegrationVerificationStore.Write(earlierCard.FolderPath, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = earlier,
            IntegrationBranch = "develop",
            DeliveryShas = [earlier],
            Evidence = IntegrationVerificationEvidence.GateNotApplicable,
            Reason = "No gate applies to the earlier library merge.",
        });
        var later = PublishLaterCommit("later-no-gate.txt");
        RunGit(_repo, "push", "-q", "origin", $"{later}:refs/heads/runner/agent-runner-01/AGT-LATER");
        var laterDeps = Build(later, taskKey: "AGT-LATER", slug: "later-no-gate");
        var laterCard = laterDeps.Scanner.FindJob("later-no-gate", _watchPath)!;
        IntegrationVerificationStore.Write(laterCard.FolderPath, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = later,
            IntegrationBranch = "develop",
            DeliveryShas = [later],
            Evidence = IntegrationVerificationEvidence.GateNotApplicable,
            Reason = "No gate applies to this library merge.",
        });
        var acceptedLater = await laterDeps.Transitions.MoveAsync("later-no-gate", TaskStates.Completed, _watchPath);
        Assert.True(acceptedLater.Status == MoveJobStatus.Success, acceptedLater.Message);
        deps.Scanner.InvalidateCache();
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob("later-no-gate", _watchPath)?.State);
        var service = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "no-gate-batches")), earlier);

        Assert.Equal(IntegrationVerificationStates.Verified,
            service.BuildForAcceptance(deps.Scanner.FindJob(Slug, _watchPath)!).Verification?.State);
        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);
        Assert.True(accepted.Status == MoveJobStatus.Success, accepted.Message);
    }

    [Theory]
    [InlineData("other", true)]
    [InlineData("develop", false)]
    public void Acceptance_GateNotApplicableRecord_MustMatchBranchAndVerifiedState(
        string branch, bool verified)
    {
        var earlier = PublishDelivery("untrusted-no-gate.txt", "earlier\n");
        RunGit(_repo, "push", "-q", "origin", $"{earlier}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(earlier);
        var later = PublishLaterCommit("untrusted-later.txt");
        var sourceFolder = Path.Combine(_watchPath, TaskStates.Completed, "untrusted-no-gate");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = "untrusted-no-gate", key = "AGT-UNTRUSTED", title = "Untrusted record",
            state = TaskStates.Completed, projectName = Project,
        }));
        IntegrationVerificationStore.Write(sourceFolder, new IntegrationVerificationRecord
        {
            State = verified ? IntegrationVerificationStates.Verified : IntegrationVerificationStates.Unverified,
            Sha = later, IntegrationBranch = branch,
            Evidence = IntegrationVerificationEvidence.GateNotApplicable,
        });
        deps.Scanner.InvalidateCache();

        var result = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "untrusted-batches")), earlier)
            .BuildForAcceptance(deps.Scanner.FindJob(Slug, _watchPath)!).Verification;

        Assert.Equal(IntegrationVerificationStates.Unverified, result?.State);
        Assert.Contains(later[..7], result?.UncoveredPublication);
    }

    [Fact]
    public void Projection_OldNetChangeRecordMatchesItsSourceTrailer()
    {
        var deliverySha = PublishDelivery("old-net-source.txt", "source\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var netChange = PublishLaterCommit("old-net-publication.txt", deliverySha);
        var later = PublishLaterCommit("after-old-net.txt");
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = netChange,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.MergeGate,
            GateVerdict = nameof(BuildTestGateVerdict.Ok),
            DeliveryShas = [netChange],
        };

        var matching = IntegrationVerificationProjection.Resolve(record, null, [], later, "develop",
            deliverySha, sha => sha == netChange, sha => git.GetNetChangeSource(_repo, sha));
        var other = IntegrationVerificationProjection.Resolve(record, null, [], later, "develop",
            new string('e', 40), sha => sha == netChange, sha => git.GetNetChangeSource(_repo, sha));

        Assert.Equal(IntegrationVerificationStates.Verified, matching.State);
        Assert.Equal(IntegrationVerificationStates.Unverified, other.State);
    }

    [Fact]
    public void Acceptance_UncoveredLaterPublication_NamesTheCommitAndEverySourceAnswer()
    {
        var deliverySha = PublishDelivery("uncovered-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        deps.Settings.SetBatchGate(Project, new BatchGateFormationOptions(
            Enabled: true, DocumentationOnly: false, Scope: "code"));
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var uncovered = PublishLaterCommit("uncovered-first.txt");
        var covered = PublishLaterCommit("uncovered-second.txt");
        Assert.True(PublishedGateEvidenceStore.Append(_watchPath, PassedPublishedGate(covered, "AGT-LATER")));
        var batches = new BatchGateStore(Path.Combine(_tempDir, "uncovered-base-gates"));
        batches.RecordBaseVerdict(new BatchGateBaseVerdict(Project, uncovered,
            BatchGatePilotService.GateProfile(deps.Settings.Get(Project).BuildProfile, _repo), "pass", null,
            "verify gate passed", Path.Combine(_tempDir, "no-such-evidence.json"),
            DateTimeOffset.UnixEpoch.AddDays(1), BatchGateBaseCheckSources.FullSuite));
        var service = Service(deps, batches, deliverySha);

        var verification = service.BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.Equal($"{uncovered[..7]} \"later published delivery\"", verification?.UncoveredPublication);
        Assert.Contains($"First uncovered commit after verified tree {deliverySha[..7]} up to {covered[..7]}: "
            + $"{uncovered[..7]} \"later published delivery\"", verification?.Reason);
        Assert.Contains("no published gate evidence for that tree", verification?.Reason);
        Assert.Contains("a batch base check passed on it but is not counted: its evidence file is missing",
            verification?.Reason);
        Assert.Contains("no batch publication covering it", verification?.Reason);
        Assert.Contains("Commit author: test", verification?.Reason);
        Assert.DoesNotContain(covered[..7] + " \"", verification?.Reason);
    }

    [Fact]
    public void Acceptance_LaterExactFullSuiteGateCoversEarlierUngatedPublication()
    {
        var deliverySha = PublishDelivery("full-cover-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        PublishLaterCommit("full-cover-hole.txt");
        var covering = PublishLaterCommit("full-cover-gate.txt");
        Assert.True(PublishedGateEvidenceStore.Append(_watchPath,
            PassedPublishedGate(covering, "AGT-LATER") with { TestLevel = TestExecutionLevels.Full }));

        var verification = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "full-cover-gates")),
            deliverySha).BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Verified, verification?.State);
        Assert.Contains($"publication {covering}", verification?.Reason);
    }

    [Fact]
    public void Acceptance_OldGateEntryUsesItsExactExecutedFullSuiteReceipt()
    {
        var deliverySha = PublishDelivery("receipt-cover-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        PublishLaterCommit("receipt-cover-hole.txt");
        var covering = PublishLaterCommit("receipt-cover-gate.txt");
        Assert.True(PublishedGateEvidenceStore.Append(_watchPath, PassedPublishedGate(covering, Slug)));
        var steps = Path.Combine(card.FolderPath, "post-steps");
        Directory.CreateDirectory(steps);
        File.WriteAllText(Path.Combine(steps, "pre-develop-build-gate-1.log"),
            $"verdict=Ok exit=0 failureKind=None\nexpectedSha={covering} testedSha={covering}\n"
            + "reason=passed; test-level=full; full-suite=required-and-run\nverdictSource=Executed\n");

        var verification = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "receipt-cover-gates")),
            deliverySha).BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Verified, verification?.State);
        Assert.Contains($"publication {covering}", verification?.Reason);
    }

    [Theory]
    [InlineData(nameof(BuildTestGateVerdict.Fail), TestExecutionLevels.Full)]
    [InlineData(nameof(BuildTestGateVerdict.Ok), TestExecutionLevels.WorkPackage)]
    public void Acceptance_RedOrPartialLaterGateDoesNotCoverHole(string verdict, string level)
    {
        var deliverySha = PublishDelivery("not-full-cover-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var hole = PublishLaterCommit("not-full-cover-hole.txt");
        var later = PublishLaterCommit("not-full-cover-gate.txt");
        var gate = PassedPublishedGate(later, "AGT-LATER") with { GateVerdict = verdict, TestLevel = level };
        if (verdict == nameof(BuildTestGateVerdict.Ok))
            Assert.True(PublishedGateEvidenceStore.Append(_watchPath, gate));
        else
            File.WriteAllText(Path.Combine(_watchPath, PublishedGateEvidenceStore.FileName),
                System.Text.Json.JsonSerializer.Serialize(new[] { gate },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var verification = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "not-full-cover-gates")),
            deliverySha).BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.Contains((verdict == nameof(BuildTestGateVerdict.Ok) ? hole : later)[..7],
            verification?.UncoveredPublication);
    }

    [Fact]
    public void Acceptance_RedGateOnLaterPublication_SaysWhyItIsNotCounted()
    {
        var deliverySha = PublishDelivery("red-later-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var later = PublishLaterCommit("red-later.txt");
        // The store refuses a red gate, so write the file as an older writer could have.
        File.WriteAllText(Path.Combine(_watchPath, PublishedGateEvidenceStore.FileName),
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                PassedPublishedGate(later, "AGT-RED") with { GateVerdict = nameof(BuildTestGateVerdict.Fail) },
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var batches = new BatchGateStore(Path.Combine(_tempDir, "red-later-gates"));
        batches.RecordBaseVerdict(new BatchGateBaseVerdict(Project, later,
            BatchGatePilotService.GateProfile(deps.Settings.Get(Project).BuildProfile, _repo),
            "pass", null, "stale batch check", Path.Combine(_tempDir, "missing-red-base-evidence.json"),
            DateTimeOffset.UnixEpoch.AddDays(1), BatchGateBaseCheckSources.FullSuite));
        var service = Service(deps, batches, deliverySha);

        var verification = service.BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.Contains("published gate evidence is not counted: gate "
            + $"{IntegrationGateJournal.PreDevelopBuildGateStep} of AGT-RED returned Fail", verification?.Reason);
        Assert.DoesNotContain("batch", verification?.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Commit author: test", verification?.Reason);
    }

    [Fact]
    public void Projection_HumanReviewCard_ShowsTheAcceptanceReason()
    {
        var deliverySha = PublishDelivery("projection-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, card.State);
        var later = PublishLaterCommit("projection-later.txt");
        var service = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "projection-gates")), deliverySha);

        var projected = service.BuildProjectionLookup([card])[card.TaskKey].Verification;
        var acceptance = service.BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Unverified, projected?.State);
        Assert.Equal(later, projected?.Sha);
        Assert.Equal(acceptance?.Reason, projected?.Reason);
        Assert.Equal($"{later[..7]} \"later published delivery\"", projected?.UncoveredPublication);
        // Lanes other than Human Review keep the completion projection.
        Assert.Equal(IntegrationVerificationStates.Verified, service.BuildProjectionLookup(
            [card with { State = TaskStates.Completed }])[card.TaskKey].Verification?.State);
    }

    private TaskIntegrationStatusService Service(Deps deps, BatchGateStore batches, string deliverySha)
        => new(new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration), deps.Settings,
            deps.Pipeline, NullLogger<TaskIntegrationStatusService>.Instance, batchGates: batches,
            scanner: deps.Scanner)
        {
            TestStatusOverride = _ => VerifiedStatus(deliverySha, deliverySha),
        };
}
