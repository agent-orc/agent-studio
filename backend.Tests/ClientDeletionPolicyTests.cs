using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix coverage for <see cref="ClientDeletionPolicy"/> - the pure
/// decision the permanent-delete endpoint and the retired-purge sweep both
/// evaluate before touching disk.
/// </summary>
public sealed class ClientDeletionPolicyTests
{
    [Fact]
    public void Refuses_WhenTheClientIsNotRetired()
    {
        var identity = Identity(ClientIdentityKind.Service);

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: false, hasUnresolvedAttempt: false);

        Assert.False(decision.Allowed);
        Assert.Equal(ClientDeletionRefusal.NotRetired, decision.Refusal);
        Assert.Equal("client-must-be-retired-before-delete", decision.RefusalCode);
    }

    [Fact]
    public void Refuses_WhenRetiredButStillReportingActiveSlots()
    {
        var identity = Identity(ClientIdentityKind.Retired) with { RunnerActiveSlots = 1 };

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: false, hasUnresolvedAttempt: false);

        Assert.False(decision.Allowed);
        Assert.Equal(ClientDeletionRefusal.Online, decision.Refusal);
        Assert.Equal("client-online", decision.RefusalCode);
    }

    [Fact]
    public void Refuses_WhenRetiredButDaemonStillReportsRunning()
    {
        var identity = Identity(ClientIdentityKind.Retired) with { RunnerActiveSlots = 0, RunnerDaemonState = "running" };

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: false, hasUnresolvedAttempt: false);

        Assert.False(decision.Allowed);
        Assert.Equal(ClientDeletionRefusal.Online, decision.Refusal);
    }

    [Fact]
    public void Refuses_WhenAnAttemptExpiredWithoutAConfirmedOutcome()
    {
        var identity = Identity(ClientIdentityKind.Retired) with { RunnerActiveSlots = 0 };

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: false, hasUnresolvedAttempt: true);

        Assert.False(decision.Allowed);
        Assert.Equal(ClientDeletionRefusal.ProcessUnknown, decision.Refusal);
        Assert.Equal("client-attempt-process-unknown", decision.RefusalCode);
    }

    [Fact]
    public void Refuses_WhenTheClientStillHoldsAnActiveLease()
    {
        var identity = Identity(ClientIdentityKind.Retired) with { RunnerActiveSlots = 0 };

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: true, hasUnresolvedAttempt: false);

        Assert.False(decision.Allowed);
        Assert.Equal(ClientDeletionRefusal.ActiveLease, decision.Refusal);
        Assert.Equal("client-has-active-lease", decision.RefusalCode);
    }

    [Fact]
    public void ProcessUnknownTakesPriorityOverAnOrdinaryActiveLease()
    {
        var identity = Identity(ClientIdentityKind.Retired) with { RunnerActiveSlots = 0 };

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: true, hasUnresolvedAttempt: true);

        Assert.Equal(ClientDeletionRefusal.ProcessUnknown, decision.Refusal);
    }

    [Fact]
    public void Allows_WhenRetiredIdleAndFree()
    {
        var identity = Identity(ClientIdentityKind.Retired) with { RunnerActiveSlots = 0, RunnerDaemonState = "stopped" };

        var decision = ClientDeletionPolicy.Evaluate(identity, hasActiveLease: false, hasUnresolvedAttempt: false);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Refusal);
        Assert.Null(decision.Message);
    }

    private static ClientIdentity Identity(ClientIdentityKind kind) => new()
    {
        Id = "runner-under-test",
        DisplayName = "Runner under test",
        Kind = kind,
        RegisteredAt = DateTime.UtcNow,
    };
}
