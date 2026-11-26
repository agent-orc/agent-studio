using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class OperatorSignInTests
{
    private static OperatorDevicePublicKey Jwk(ECDsa key)
    {
        var point = key.ExportParameters(false).Q;
        return new("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!), OperatorDevicePublicKey.Base64Url(point.Y!));
    }

    private (ServiceAccount Agent, PollOperatorSignInResponse Issued) BoundSession(ECDsa sessionKey, ECDsa? deviceKey = null, int hours = 24)
    {
        var agent = NewAgent();
        var started = Start(Jwk(sessionKey), ["project.testing"]);
        if (deviceKey is not null) _store.PollOperatorSignIn(started.Poll, certification: Certification(deviceKey, started.SignIn));
        Confirm(started.Code, agent.Id, hours: hours);
        return (agent, Poll(started.Poll));
    }

    private OperatorSessionResponse Rotate(PollOperatorSignInResponse issued, ECDsa current, ECDsa device, ECDsa next, string address = Terminal, AccessSecurityStore? target = null)
    {
        var now = _clock.GetUtcNow();
        var statement = AccessSecurityStore.OperatorRenewalStatement(issued.SessionId!, Jwk(next).Thumbprint(), now.ToUnixTimeSeconds());
        var request = new OperatorSessionRenewalRequest(issued.SessionId!, Jwk(next), now.ToUnixTimeSeconds(),
            OperatorDevicePublicKey.Base64Url(device.SignData(Encoding.UTF8.GetBytes(statement), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var proof = new DefaultHttpContext().Request;
        proof.Scheme = "https"; proof.Host = new HostString("studio.test"); proof.Method = "POST";
        proof.Path = OperatorSessionEndpoints.RenewPath;
        proof.Headers["DPoP"] = OperatorDpopTests.Proof(current, issued.Token!, "POST", "https://studio.test" + proof.Path, now);
        return (target ?? _store).RenewBoundOperatorSession(issued.Token, request, proof, address);
    }

    [Fact]
    public void Lifetime_choices_are_clamped_by_binding_agent_and_default_and_terminal_can_only_shorten_the_preselection()
    {
        var agent = NewAgent();
        using var hardware = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var (requested, expected) in new[] { (720, 168), (24, 24), (8, 8) })
        {
            var started = _store.StartOperatorSignIn(new(NewKey(), "workstation", agent.Id, [], requested), Terminal, Terminal);
            _store.PollOperatorSignIn(started.PollHandle, certification: Certification(hardware, started.SignIn));
            var lookup = _store.LookupOperatorSignIn(started.UserCode, Owner(), Terminal);
            Assert.Equal([8, 24, 168, 720, 0], lookup.LifetimeHours);
            Assert.Equal(expected, lookup.DefaultLifetimeHours);
        }
        var memory = Start();
        Assert.Equal([8, 24], _store.LookupOperatorSignIn(memory.Code, Owner(), Terminal).LifetimeHours);
        Assert.Equal(24, _store.LookupOperatorSignIn(memory.Code, Owner(), Terminal).DefaultLifetimeHours);
        Assert.Equal("invalid-lifetime", Assert.Throws<SecurityOperationException>(() => Confirm(memory.Code, agent.Id, hours: 168)).Code);
        _store.SetOperatorAgentLifetimeLimit(agent.Id, 8, SteppedUp());
        Assert.Equal([8], _store.LookupOperatorSignIn(memory.Code, Owner(), Terminal).LifetimeHours);
        Assert.Equal(8, _store.LookupOperatorSignIn(memory.Code, Owner(), Terminal).DefaultLifetimeHours);
    }

    [Fact]
    public void Until_revoked_has_a_null_chosen_end_and_stores_confirmation_limits_while_settings_stays_at_eight_hours()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bound = BoundSession(key, device, 0);
        var stored = _store.ListOperatorAgents().Single().Sessions!.Single();
        Assert.Null(stored.ChosenEnd);
        Assert.Equal(0, stored.ChosenLifetimeHours);
        Assert.Equal(new OperatorSessionLimits(), stored.LimitsAtConfirmation);
        Assert.Equal(stored.CreatedAt.AddMinutes(60), stored.KeyExpiresAt);
        Assert.Equal("invalid-lifetime", Assert.Throws<SecurityOperationException>(() =>
            _store.StartOperatorSession(bound.Agent.Id, new("settings", 24), _ownerId)).Code);
    }

    [Fact]
    public void Device_signed_rotation_needs_no_step_up_rejects_old_key_and_marks_new_address_without_resetting_idle()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (_, issued) = BoundSession(key);
        var created = _store.ListOperatorAgents().Single().Sessions!.Single().CreatedAt;
        _clock.Advance(TimeSpan.FromMinutes(50));
        var rotated = Rotate(issued, key, key, next, "203.0.113.44");
        Assert.Null(rotated.LastUsedAt);
        Assert.Equal(created.AddHours(24), rotated.IdleEnd);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, rotated.NewAddressAt);
        var principal = _store.AuthenticateOperatorSession(issued.Token, false)!;
        Assert.Equal(Jwk(next).Thumbprint(), principal.BoundKeyThumbprint);
        var now = _clock.GetUtcNow();
        Assert.Equal("dpop-key-mismatch", _store.DpopVerifier.Validate(OperatorDpopTests.Proof(key, issued.Token!, "GET", "https://studio.test/api/tasks", now),
            "GET", "https://studio.test/api/tasks", issued.Token!, principal.BoundKeyThumbprint!, now));
        var audit = Assert.Single(_store.ControlPlaneAudit.Read(0, 1000), entry => entry.Operation == "operator-session.renew");
        Assert.Equal($"operator-agent:{issued.OperatorAgentId}/session:{issued.SessionId}", audit.Actor);
        Assert.Equal("human:" + _ownerId, audit.DelegatedBy);
        _store.RecordOperatorSessionUse(issued.Token!, "203.0.113.44");
        Assert.Equal(now.UtcDateTime.AddHours(24), _store.ToOperatorSessionResponse(issued.OperatorAgentId!,
            _store.ListOperatorAgents().Single().Sessions!.Single()).IdleEnd);
    }

    [Theory]
    [InlineData("copy", "renewal-device-key-missing")]
    [InlineData("forgotten", "session-ended")]
    [InlineData("kill", "session-ended")]
    [InlineData("disabled", "session-ended")]
    [InlineData("key-expired", "session-ended")]
    public void Renewal_refuses_ending_events_and_a_signature_from_another_machine(string ending, string reason)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongDevice = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (agent, issued) = BoundSession(key);
        if (ending == "forgotten") _store.ForgetOperatorDevice(agent.Id, _store.ListOperatorAgents().Single().Devices!.Single().Id, _ownerId);
        if (ending == "kill") _store.EndOperatorAccess(null, OperatorKillActor.Owner(_ownerId));
        if (ending == "disabled")
        {
            var secondOwner = Person("other.owner", IamRoles.Owner);
            _store.UpdateUser(_ownerId, new UpdateUserRequest(null, null, null, true), secondOwner.Id);
        }
        if (ending == "key-expired") _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(reason, Assert.Throws<SecurityOperationException>(() => Rotate(issued, key,
            ending == "copy" ? wrongDevice : key, next)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Renewals_alone_do_not_prevent_idle_end_and_requests_reset_idle(bool use)
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var (_, issued) = BoundSession(current, device, 168);
            for (var hour = 0; hour < 24; hour++)
            {
                _clock.Advance(TimeSpan.FromMinutes(50));
                var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Rotate(issued, current, device, next);
                current.Dispose(); current = next;
            }
            if (use) _store.RecordOperatorSessionUse(issued.Token!, Terminal);
            for (var hour = 0; hour < 5; hour++)
            {
                _clock.Advance(TimeSpan.FromMinutes(50));
                using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                if (!use && hour == 4)
                {
                    Assert.Equal("session-ended", Assert.Throws<SecurityOperationException>(() => Rotate(issued, current, device, next)).Code);
                    Assert.Null(_store.AuthenticateOperatorSession(issued.Token, false));
                }
                else
                {
                    Rotate(issued, current, device, next);
                    current.Dispose(); current = ECDsa.Create(next.ExportParameters(true));
                }
            }
            if (use) Assert.NotNull(_store.AuthenticateOperatorSession(issued.Token, false));
        }
        finally { current.Dispose(); }
    }

    [Fact]
    public void Lowered_limit_refuses_next_rotation_and_raising_limits_does_not_extend_the_chosen_end()
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var (_, issued) = BoundSession(current, device, 168);
            var chosenEnd = issued.ChosenEnd;
            // Keep the browser active while the device rotates through eight hours.
            for (var index = 0; index < 10; index++)
            {
                _clock.Advance(TimeSpan.FromMinutes(50));
                Owner();
                var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Rotate(issued, current, device, next); current.Dispose(); current = next;
            }
            _store.SetOperatorSessionLimits(new(168, 8), SteppedUp());
            using var last = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Equal("limit-lowered", Assert.Throws<SecurityOperationException>(() => Rotate(issued, current, device, last)).Code);
            Assert.Equal(chosenEnd, _store.ListOperatorAgents().Single().Sessions!.Single().ChosenEnd);
        }
        finally { current.Dispose(); }
    }

    [Fact]
    public void Session_limits_need_owner_step_up_validate_idle_range_and_are_audited()
    {
        Assert.Equal("step-up-required", Assert.Throws<SecurityOperationException>(() => _store.SetOperatorSessionLimits(new(), Owner())).Code);
        Assert.Equal("invalid-idle-limit", Assert.Throws<SecurityOperationException>(() => _store.SetOperatorSessionLimits(new(IdleHours: 169), SteppedUp())).Code);
        Assert.Equal(new OperatorSessionLimits(24, 168, 24, 1), _store.SetOperatorSessionLimits(new(24, 168, 24, 1), SteppedUp()));
        Assert.Contains("operator-session-limits.change", AuditOperations());
    }
    [Fact]
    public void Chosen_end_is_enforced_even_with_a_fresh_certificate_and_ordinary_activity()
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var (_, issued) = BoundSession(current, device, 8);
            for (var index = 0; index < 9; index++)
            {
                _clock.Advance(TimeSpan.FromMinutes(50));
                var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Rotate(issued, current, device, next); current.Dispose(); current = next;
                _store.RecordOperatorSessionUse(issued.Token!, Terminal);
            }
            _clock.Advance(TimeSpan.FromMinutes(31));
            using var last = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Equal("session-ended", Assert.Throws<SecurityOperationException>(() => Rotate(issued, current, device, last)).Code);
            Assert.Null(_store.AuthenticateOperatorSession(issued.Token, false));
        }
        finally { current.Dispose(); }
    }

    [Fact]
    public void End_elevation_keeps_the_session_and_ending_all_revokes_long_or_class_carrying_sessions()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (agent, issued) = BoundSession(key);
        _store.ActOnElevatedSession(agent.Id, issued.SessionId!, "end-elevation", _ownerId);
        Assert.Empty(_store.ListOperatorAgents().Single().Sessions!.Single().Classes!);
        Assert.NotNull(_store.AuthenticateOperatorSession(issued.Token, false));
        _store.ActOnElevatedSession(agent.Id, issued.SessionId!, "keep", _ownerId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, _store.ListOperatorAgents().Single().Sessions!.Single().ReviewedAt);
        _store.EndAllElevatedSessions(_ownerId);
        Assert.Null(_store.AuthenticateOperatorSession(issued.Token, false));
    }

    [Fact]
    public void Forgetting_a_memory_device_after_rotation_ends_its_session()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (agent, issued) = BoundSession(key);
        Rotate(issued, key, key, next);
        _store.ForgetOperatorDevice(agent.Id, _store.ListOperatorAgents().Single().Devices!.Single().Id, _ownerId);
        Assert.Null(_store.AuthenticateOperatorSession(issued.Token, false));
    }

    [Fact]
    public void A_confirming_person_without_a_second_factor_cannot_use_or_renew_the_session()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (_, issued) = BoundSession(key);
        var file = Path.Combine(_store.SecurityFolder, "access-state.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var user in state["Users"]!.AsArray())
            if (user!["Id"]!.GetValue<string>() == _ownerId) user.AsObject().Remove("TotpSecret");
        File.WriteAllText(file, state.ToJsonString());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root, ["Secrets:KeyFile"] = _root + ".kek", ["Security:Profile"] = "networked" }).Build();
        var reloaded = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        Assert.Null(reloaded.AuthenticateOperatorSession(issued.Token, false));
        Assert.Equal("session-ended", Assert.Throws<SecurityOperationException>(() => Rotate(issued, key, key, next, target: reloaded)).Code);
    }

    [Fact]
    public void Kill_switch_denies_a_pending_sign_in_even_if_the_owner_later_creates_another_agent()
    {
        NewAgent();
        var pending = Start();
        _store.EndOperatorAccess(null, OperatorKillActor.Owner(_ownerId));
        var replacement = NewAgent("replacement console");
        Assert.Equal("signin-code-denied", Assert.Throws<SecurityOperationException>(() => Confirm(pending.Code, replacement.Id)).Code);
    }

    [Fact]
    public void An_explicit_until_revoked_role_limit_survives_reloading_the_identity_store()
    {
        _store.SetOperatorSessionLimits(new(AdminLifetimeHours: null), SteppedUp());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root, ["Secrets:KeyFile"] = _root + ".kek", ["Security:Profile"] = "networked" }).Build();
        var reloaded = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        Assert.Null(reloaded.GetOperatorSessionLimits().AdminLifetimeHours);
    }

}
