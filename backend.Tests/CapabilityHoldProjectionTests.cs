using AgentStudio.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3840: Execution Hosts showed a review role Online while claims withheld
/// provider-auth:codex from it. The held keys come from the same rule set as
/// claim admission, and refused review claims add the keys the role never
/// registered.
/// </summary>
public sealed class CapabilityHoldProjectionTests
{
    private const string Runner = "kori-review-01-runner";
    private const string Instance = "kori-review-01:1";
    private static readonly string Codex = Contract.CapabilityProtocol.ProviderAuthentication("codex");
    private static readonly string Claude = Contract.CapabilityProtocol.ProviderAuthentication("claude");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IndeterminateProbe_IsHeld_AndWithheldFromClaims_WhileTheRoleStaysOnline()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Codex, Claude]);
        Advertise(registry,
            new Contract.AdvertisedCapabilityDto(Codex, "provider-auth", "ready",
                Detail: "codex login status timed out after 30s", Signal: "indeterminate"),
            new Contract.AdvertisedCapabilityDto(Claude, "provider-auth", "ready", Signal: "ok"));

        var held = Assert.Single(registry.HeldCapabilities(Runner));
        Assert.Equal(Codex, held.Key);
        Assert.Equal(Contract.HeldCapabilityStates.Indeterminate, held.State);
        Assert.Equal(Contract.HeldCapabilityCauses.ProbeIndeterminate, held.Cause);
        Assert.Equal("codex login status timed out after 30s", held.Detail);
        var admitted = registry.AdmittedReviewCapabilities(Runner, Instance);
        Assert.DoesNotContain(Codex, admitted);
        Assert.Contains(Claude, admitted);
    }

    [Fact]
    public void StaleAndUnavailableAdvertisements_AreHeldWithTheProbeDetail()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Codex, Claude]);
        Advertise(registry,
            new Contract.AdvertisedCapabilityDto(Codex, "provider-auth", "unavailable", Detail: "Not logged in"),
            new Contract.AdvertisedCapabilityDto(Claude, "provider-auth", "ready"));
        Assert.Equal(
            [(Codex, Contract.HeldCapabilityStates.Held, Contract.HeldCapabilityCauses.NotReady, "Not logged in")],
            registry.HeldCapabilities(Runner).Select(hold => (hold.Key, hold.State, hold.Cause, hold.Detail)));

        _clock.Advance(TimeSpan.FromMinutes(4));
        var stale = registry.HeldCapabilities(Runner);
        Assert.Equal([Claude, Codex], stale.Select(hold => hold.Key).Order(StringComparer.Ordinal));
        Assert.All(stale, hold => Assert.Equal(Contract.HeldCapabilityCauses.Stale, hold.Cause));
        Assert.All(stale, hold => Assert.Equal(Contract.HeldCapabilityStates.Indeterminate, hold.State));
        Assert.Empty(registry.AdmittedReviewCapabilities(Runner, Instance).Where(key => key.StartsWith("provider-auth:")));
    }

    [Fact]
    public void RefusedReviewClaim_AddsTheUnregisteredKey_AndNamesTheWaitingCard()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Claude]);
        Advertise(registry, new Contract.AdvertisedCapabilityDto(Claude, "provider-auth", "ready"));
        var log = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance, _clock);
        log.Record(Runner, [new Contract.ReviewUnclaimableAttemptDto(
            "review_codex", "AGT-3649", _clock.GetUtcNow().UtcDateTime.AddMinutes(-20), [Codex])]);
        // Another executor's refusal and a quota deferral name no key for this role.
        log.Record("kori-review-02-runner", [new Contract.ReviewUnclaimableAttemptDto(
            "review_other", "AGT-1", _clock.GetUtcNow().UtcDateTime, ["git:read:other"])]);
        log.Record(Runner, [new Contract.ReviewUnclaimableAttemptDto(
            "review_quota", "AGT-2", _clock.GetUtcNow().UtcDateTime, ["Codex quota is exhausted."])],
            Contract.ReviewClaimEmptyReasons.QuotaDeferred);

        var held = Assert.Single(HeldCapabilityProjection.Merge(
            registry.HeldCapabilities(Runner), log.ForExecutor(Runner),
            registry.RegisteredReviewCapabilities(Runner, Instance), Set("review_codex")));

        Assert.Equal(Codex, held.Key);
        Assert.Equal(Contract.HeldCapabilityStates.Held, held.State);
        Assert.Equal(Contract.HeldCapabilityCauses.NotRegistered, held.Cause);
        Assert.Equal(["AGT-3649"], held.WaitingTaskKeys);
    }

    [Fact]
    public void RefusedReviewClaim_OnAnAdvertisedHold_KeepsTheProbeCause()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Codex]);
        Advertise(registry, new Contract.AdvertisedCapabilityDto(Codex, "provider-auth", "ready",
            Detail: "probe timed out", Signal: "indeterminate"));
        var log = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance, _clock);
        log.Record(Runner, [new Contract.ReviewUnclaimableAttemptDto(
            "review_codex", "AGT-3649", _clock.GetUtcNow().UtcDateTime, [Codex])]);

        var held = Assert.Single(HeldCapabilityProjection.Merge(
            registry.HeldCapabilities(Runner), log.ForExecutor(Runner),
            registry.RegisteredReviewCapabilities(Runner, Instance), Set("review_codex")));

        Assert.Equal(Contract.HeldCapabilityCauses.ProbeIndeterminate, held.Cause);
        Assert.Equal("probe timed out", held.Detail);
        Assert.Equal(["AGT-3649"], held.WaitingTaskKeys);
    }

    [Fact]
    public void RefusalRecordedBeforeRecovery_DoesNotCreateANotRegisteredHold()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Codex]);
        Advertise(registry, new Contract.AdvertisedCapabilityDto(Codex, "provider-auth", "unavailable"));
        var log = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance, _clock);
        log.Record(Runner, [new Contract.ReviewUnclaimableAttemptDto(
            "review_codex", "AGT-3649", _clock.GetUtcNow().UtcDateTime, [Codex])]);

        Advertise(registry, 3, new Contract.AdvertisedCapabilityDto(Codex, "provider-auth", "ready", Signal: "ok"));

        Assert.Contains(Codex, registry.AdmittedReviewCapabilities(Runner, Instance));
        Assert.Empty(registry.HeldCapabilities(Runner));
        Assert.Empty(HeldCapabilityProjection.Merge(registry.HeldCapabilities(Runner), log.ForExecutor(Runner),
            registry.RegisteredReviewCapabilities(Runner, Instance), Set("review_codex")));
    }

    [Fact]
    public void RefusalForAttemptNoLongerPending_DoesNotAddHoldOrWaitingWork()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Claude]);
        Advertise(registry, new Contract.AdvertisedCapabilityDto(Claude, "provider-auth", "ready"));
        var log = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance, _clock);
        log.Record(Runner, [new Contract.ReviewUnclaimableAttemptDto(
            "review_old", "AGT-3649", _clock.GetUtcNow().UtcDateTime, [Codex])]);

        Assert.Empty(HeldCapabilityProjection.Merge(registry.HeldCapabilities(Runner), log.ForExecutor(Runner),
            registry.RegisteredReviewCapabilities(Runner, Instance), Set("review_current")));
    }

    [Fact]
    public void OldRefusalDoesNotAddWaitingWorkToARegisteredHold()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Codex]);
        Advertise(registry, new Contract.AdvertisedCapabilityDto(Codex, "provider-auth", "unavailable"));
        var log = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance, _clock);
        log.Record(Runner, [new Contract.ReviewUnclaimableAttemptDto(
            "review_old", "AGT-3649", _clock.GetUtcNow().UtcDateTime, [Codex])]);

        var hold = Assert.Single(HeldCapabilityProjection.Merge(registry.HeldCapabilities(Runner),
            log.ForExecutor(Runner), registry.RegisteredReviewCapabilities(Runner, Instance), Set("review_current")));
        Assert.Empty(hold.WaitingTaskKeys ?? []);
    }

    [Fact]
    public void ReprobeRequest_IsHandedOutOnce_AndOnlyForARegisteredRole()
    {
        var registry = Registry([Contract.ReviewCapabilities.ReviewExecutor, Codex]);

        Assert.False(registry.RequestReprobe("unknown-runner", Codex));
        Assert.Null(registry.TakeReprobeRequests(Runner));
        Assert.True(registry.RequestReprobe(Runner, Codex));
        Assert.True(registry.RequestReprobe(Runner, Codex));

        Assert.Equal([Codex], registry.TakeReprobeRequests(Runner));
        Assert.Null(registry.TakeReprobeRequests(Runner));
        Assert.Equal(["git:fetch", Codex],
            CapabilityReprobeDelivery.Merge(["git:fetch"], [Codex, "git:fetch"]).Order(StringComparer.Ordinal));
    }

    private V1ReviewExecutorRegistry Registry(IReadOnlyList<string> capabilities)
    {
        var registry = new V1ReviewExecutorRegistry(_clock);
        registry.Register(Runner, new Contract.RegisterRunnerRequest(
            Runner, "kori-review-01", Instance, "1.0.0", Contract.TaskServerProtocol.Current, capabilities));
        return registry;
    }

    private static IReadOnlySet<string> Set(params string[] values)
        => values.ToHashSet(StringComparer.Ordinal);

    private void Advertise(V1ReviewExecutorRegistry registry, params Contract.AdvertisedCapabilityDto[] capabilities)
        => Advertise(registry, 1, capabilities);

    private void Advertise(V1ReviewExecutorRegistry registry, long generation,
        params Contract.AdvertisedCapabilityDto[] capabilities)
        => registry.AdvertiseCapabilities(Runner, new Contract.CapabilityAdvertisementRequest(
            Runner, Instance, Contract.CapabilityProtocol.CurrentSchemaVersion,
            _clock.GetUtcNow().UtcDateTime, 180, generation,
            [new Contract.AdvertisedCapabilityDto(Contract.ReviewCapabilities.ReviewExecutor, "executor", "ready"),
             .. capabilities]));
}
