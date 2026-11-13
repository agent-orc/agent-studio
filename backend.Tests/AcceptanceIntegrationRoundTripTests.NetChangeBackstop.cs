using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3758 - a Completed card whose delivery develop carries as a verified
/// net-change publication is integrated: the backstop starts no recovery
/// merge after a tip change, and a failed recovery keeps the card Completed.
/// A delivery that develop truly lacks still returns to Human Review.
/// </summary>
public sealed partial class AcceptanceIntegrationRoundTripTests
{
    private (Deps Deps, TaskInfo Card, CountingBuildTestGateRunner Gate) CompletedNetChangeCard(string name)
    {
        var deliverySha = PublishDelivery($"{name}-source.txt", "source\n");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, gateRunner: gate, initialState: TaskStates.Completed);
        var netChange = PublishLaterCommit($"{name}-net.txt", deliverySha);
        PublishLaterCommit($"{name}-later.txt");
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        IntegrationVerificationStore.Write(card.FolderPath, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = netChange,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.MergeGate,
            GateVerdict = nameof(BuildTestGateVerdict.Ok),
            DeliveryShas = [netChange],
        });
        deps.Scanner.InvalidateCache();
        return (deps, deps.Scanner.FindJob(Slug, _watchPath)!, gate);
    }

    [Fact]
    public void Backstop_CompletedNetChangeCardAfterTipChange_StartsNoRecoveryMerge()
    {
        var (deps, card, gate) = CompletedNetChangeCard("net-backstop");
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        Assert.True(AcceptedIntegrationPublishedDeliveryGuard.CarriesNetChangePublication(card, "develop", git));

        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner, deps.Settings, VerifyingRunner(deps, gate), deps.Integration,
            deps.Mutations, deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions, deps.Timeline, deps.Pipeline, git: git);

        Assert.Equal(0, backstop.RunOnce());
        Assert.Equal(0, gate.Invocations);
        var kept = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, kept.State);
        Assert.DoesNotContain(deps.Timeline.ReadAll(kept.FolderPath), item => item.Kind == "lane_changed");
    }

    [Fact]
    public void Guard_FailedRecoveryForNetChangeCard_KeepsCompleted()
    {
        var (deps, card, _) = CompletedNetChangeCard("net-guard");
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);

        Assert.True(AcceptedIntegrationPublishedDeliveryGuard.KeepCompleted(
            card, "develop", AcceptedIntegrationFailureCodes.StaleAttempt, git, null, NullLogger.Instance,
            recoveryMerge: true));
    }

    [Fact]
    public void Guard_CompletedCardWithTrulyMissingDelivery_DoesNotKeepCompleted()
    {
        var deliverySha = PublishDelivery("missing-delivery.txt", "never published\n");
        var deps = Build(deliverySha, initialState: TaskStates.Completed);
        PublishLaterCommit("missing-later.txt");
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);

        Assert.False(AcceptedIntegrationPublishedDeliveryGuard.CarriesNetChangePublication(card, "develop", git));
        Assert.False(AcceptedIntegrationPublishedDeliveryGuard.KeepCompleted(
            card, "develop", AcceptedIntegrationFailureCodes.StaleAttempt, git, null, NullLogger.Instance,
            recoveryMerge: true));
    }
}
