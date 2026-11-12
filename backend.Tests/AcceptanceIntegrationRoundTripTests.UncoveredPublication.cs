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
    public void Acceptance_UncoveredLaterPublication_NamesTheCommitAndEverySourceAnswer()
    {
        var deliverySha = PublishDelivery("uncovered-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
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
        Assert.DoesNotContain(covered[..7] + " \"", verification?.Reason);
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
        var service = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "red-later-gates")), deliverySha);

        var verification = service.BuildForAcceptance(card).Verification;

        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.Contains("published gate evidence is not counted: gate "
            + $"{IntegrationGateJournal.PreDevelopBuildGateStep} of AGT-RED returned Fail", verification?.Reason);
        Assert.Contains("no batch base verdict on it", verification?.Reason);
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
            deps.Pipeline, NullLogger<TaskIntegrationStatusService>.Instance, batchGates: batches)
        {
            TestStatusOverride = _ => VerifiedStatus(deliverySha, deliverySha),
        };
}
