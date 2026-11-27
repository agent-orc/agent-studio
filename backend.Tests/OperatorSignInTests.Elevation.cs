using System.Security.Cryptography;
using AgentStudio.Security;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Tests;

public sealed partial class OperatorSignInTests
{
    [Fact]
    public void Direct_class_lapses_after_24_hours_and_only_owner_step_up_rearms_without_resetting_rate()
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var agent = NewAgent();
            var started = Start(Jwk(current), ["project.testing", "cards.accept-override", "cards.archive-override"]);
            _store.PollOperatorSignIn(started.Poll, certification: Certification(device, started.SignIn));
            var confirmed = Confirm(started.Code, agent.Id, hours: 168);
            var issued = Poll(started.Poll);
            var operation = OperatorOperationCatalog.Find(OperatorOperationIds.ProjectTestingSet)!;
            var initial = _store.ListOperatorAgents().Single().Sessions!.Single();
            var armed = initial.Elevation!.Classes.Single(entry => entry.Id == "project.testing");
            Assert.Equal(initial.CreatedAt.AddHours(24), armed.ArmedUntil);
            Assert.Equal(_ownerId, armed.ConfirmedBy);
            Assert.True(_store.AdmitOperatorElevation(agent.Id, confirmed.SessionId, operation, "alpha", "owner requested strict")!.Admitted);
            // Renew the device proof throughout the day and record ordinary use separately.
            while (_clock.GetUtcNow().UtcDateTime < armed.ArmedUntil)
            {
                _clock.Advance(TimeSpan.FromMinutes(50));
                var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Rotate(issued, current, device, next);
                current.Dispose(); current = next;
                _store.RecordOperatorSessionUse(issued.Token!, Terminal);
                Assert.NotNull(Owner());
            }
            Assert.Null(_store.AdmitOperatorElevation(agent.Id, confirmed.SessionId, operation, "alpha", "owner requested strict"));
            var session = _store.ListOperatorAgents().Single().Sessions!.Single();
            Assert.Equal(3, session.Elevation!.Classes.Count);
            Assert.All(session.Elevation.Classes.Where(entry => entry.Id.StartsWith("cards.", StringComparison.Ordinal)), entry =>
            { Assert.Null(entry.ArmedUntil); Assert.Null(entry.ArmingId); });
            Assert.Equal("step-up-required", Assert.Throws<SecurityOperationException>(() =>
                _store.RearmOperatorClass(agent.Id, confirmed.SessionId, "project.testing", Owner())).Code);
            _store.RearmOperatorClass(agent.Id, confirmed.SessionId, "project.testing", SteppedUp());
            var rearmed = _store.ListOperatorAgents().Single().Sessions!.Single().Elevation!.Classes.Single(entry => entry.Id == "project.testing");
            Assert.NotEqual(armed.ArmingId, rearmed.ArmingId);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddHours(24), rearmed.ArmedUntil);
            Assert.True(_store.AdmitOperatorElevation(agent.Id, confirmed.SessionId, operation, "alpha", "owner requested strict")!.Admitted);
            Assert.Equal("class-not-rearmable", Assert.Throws<SecurityOperationException>(() =>
                _store.RearmOperatorClass(agent.Id, confirmed.SessionId, "cards.accept-override", SteppedUp())).Code);
        }
        finally { current.Dispose(); }
    }

    [Fact]
    public void Six_uses_per_project_and_three_refusals_end_elevation_with_persisted_attention()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (agent, issued) = BoundSession(key);
        var operation = OperatorOperationCatalog.Find(OperatorOperationIds.ProjectTestingSet)!;
        Assert.Equal("elevation-reason-required", _store.AdmitOperatorElevation(agent.Id, issued.SessionId!, operation, "alpha", " ")!.Reason);
        for (var i = 0; i < 6; i++)
            Assert.True(_store.AdmitOperatorElevation(agent.Id, issued.SessionId!, operation, "alpha", "Use strict")!.Admitted);
        Assert.True(_store.AdmitOperatorElevation(agent.Id, issued.SessionId!, operation, "beta", "Use strict")!.Admitted);
        var restarted = new AccessSecurityStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root, ["Secrets:KeyFile"] = _root + ".kek", ["Security:Profile"] = "networked" }).Build(),
            NullLogger<AccessSecurityStore>.Instance, _clock);
        Assert.Equal(7, restarted.ListOperatorAgents().Single().Sessions!.Single().Elevation!.Classes.Single().Uses.Count);
        _store.RearmOperatorClass(agent.Id, issued.SessionId!, "project.testing", SteppedUp());
        for (var i = 0; i < 3; i++)
            Assert.Equal(AccessSecurityStore.ElevationRateExceeded,
                _store.AdmitOperatorElevation(agent.Id, issued.SessionId!, operation, "alpha", "Use strict")!.Reason);
        var session = _store.ListOperatorAgents().Single().Sessions!.Single();
        Assert.Null(session.Elevation); Assert.Empty(session.Classes!);
        Assert.NotNull(_store.AuthenticateOperatorSession(issued.Token));
        Assert.Single(new OperatorElevationFactSource(_store).Collect(_clock.GetUtcNow().UtcDateTime));
        _store.ActOnElevatedSession(agent.Id, issued.SessionId!, "end-elevation", _ownerId);
        Assert.Empty(new OperatorElevationFactSource(_store).Collect(_clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Direct_arming_is_capped_at_session_end_and_batch_never_admits_single_request()
    {
        var agent = NewAgent();
        var started = Start(classes: ["project.testing", "cards.accept-override"]);
        var confirmed = Confirm(started.Code, agent.Id, hours: 8);
        var session = _store.ListOperatorAgents().Single().Sessions!.Single();
        Assert.Equal(session.ExpiresAt, session.Elevation!.Classes.Single(entry => entry.Id == "project.testing").ArmedUntil);
        Assert.Null(_store.AdmitOperatorElevation(agent.Id, confirmed.SessionId,
            OperatorOperationCatalog.Find(OperatorOperationIds.CardAcceptOverride)!, "alpha", "owner request"));
        _store.EndOperatorAccess(null, OperatorKillActor.Owner(_ownerId));
        Assert.Null(_store.ListOperatorAgents().Single().Sessions!.Single().Elevation);
    }
}
