using AgentStudio.Personas;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Workspace persona activation (persona team PT-1, decision PT-D1 option B):
/// activation needs the Owner and a fresh, single-use step-up and records the
/// descriptor revision and digest in shadow state for one report period;
/// pause, resume and deactivation need no step-up; every change lands in the
/// IAM audit; and a fork keeps its own revision chain, untouched by later
/// catalogue revisions until the Owner adopts one.
/// </summary>
public sealed class PersonaActivationStoreTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private const string Workspace = "main";
    private const string Warden = "operations-warden";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "persona-activation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    private readonly AccessSecurityStore _security;
    private readonly string _ownerId;
    private readonly string _ownerSession;
    private readonly string _totpSecret;

    public PersonaActivationStoreTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Secrets:KeyFile"] = _root + ".kek",
            ["Security:Profile"] = "networked",
        }).Build();
        _security = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var bootstrap = _security.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        (_ownerId, _ownerSession) = (bootstrap.User.Id, bootstrap.SessionToken);
        var owner = _security.AuthenticateSession(_ownerSession)!;
        _totpSecret = _security.BeginTotpEnrollment(owner);
        _security.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_totpSecret, Now));
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void Activation_without_a_step_up_answers_step_up_required_and_records_nothing()
    {
        var store = Store();
        var auditStart = _security.ControlPlaneAudit.HeadSequence;

        var refused = Refused(() => store.Activate(Workspace, Warden, new PersonaActivateRequest(), Owner()));

        Assert.Equal((403, "step-up-required"), (refused.Status, refused.Code));
        Assert.Null(Entry(store, Warden).Activation);
        Assert.Empty(PersonaAudit(auditStart));
        Assert.Equal("owner-required", Refused(() => store.Activate(Workspace, Warden, new PersonaActivateRequest(), Admin())).Code);
    }

    [Fact]
    public void Activation_with_a_step_up_records_revision_digest_and_warden_shadow_for_two_weeks()
    {
        var store = Store();
        var catalogue = new PersonaCatalogue().Find(Warden)!;
        var auditStart = _security.ControlPlaneAudit.HeadSequence;
        var owner = SteppedUpOwner();

        var activation = store.Activate(Workspace, Warden, new PersonaActivateRequest(
            new PersonaAttributesOverrideV1(Thoroughness: 5, SignalThreshold: 20),
            new PersonaAllowanceOverrideV1(TokensPerWeek: 100_000),
            new PersonaScopeNarrowingV1(Projects: ["alpha"], Areas: ["operations.backups", "audit.chain"], ExcludedPaths: ["docs/"]),
            "Start the warden in shadow"), owner);

        Assert.Equal((PersonaStates.Shadow, PersonaSources.Catalogue, 1), (activation.State, activation.Source, activation.ActivationRevision));
        Assert.Equal(("2026.11.1", catalogue.Digest), (activation.DescriptorRevision, activation.DescriptorDigest));
        Assert.Equal(catalogue.Digest, PersonaDescriptorDigest.Compute(activation.Descriptor));
        Assert.Equal((Now, Now.AddDays(14), _ownerId), (activation.ActivatedAt, activation.ShadowUntil, activation.ActivatedBy));
        Assert.Equal((5, 20, "A1"), (activation.Attributes.Thoroughness, activation.Attributes.SignalThreshold, activation.Attributes.Autonomy));
        Assert.Equal((100_000, 10), (activation.Allowance.TokensPerWeek, activation.Allowance.SessionsPerWeek));
        Assert.Equal(new PersonaScopeV1(["alpha"], ["operations.backups", "audit.chain"], ["docs/"]), activation.Scope,
            new ScopeComparer());
        Assert.Equal(["observe", "persona.item.publish", "persona.report.publish", "persona.proposal.draft", "persona.decision.request"],
            activation.Operations);
        Assert.Equal(Warden, activation.RoleId);
        var principal = _security.FindPersonaPrincipal(Workspace, Warden)!;
        Assert.Equal(Warden, principal.PersonaRoleId);
        Assert.Equal(["alpha"], Assert.Single(_security.ListStandingGrants(principal.Id)).Scope.Projects);
        // The step-up is single use.
        Assert.Equal("step-up-required", Refused(() =>
            store.Activate(Workspace, "cost-controller", new PersonaActivateRequest(), owner)).Code);

        var audit = Assert.Single(PersonaAudit(auditStart));
        Assert.Equal(("persona.activate", $"persona:{Workspace}/{Warden}", $"human:{_ownerId}"), (audit.Operation, audit.Target, audit.Actor));
        Assert.True(_security.ControlPlaneAudit.VerifyChain().Ok);

        // The record survives a restart, and the warden shadow period ends after two weeks.
        var restarted = Store();
        _clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1));
        Assert.Equal(PersonaStates.Shadow, Entry(restarted, Warden).Activation!.State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PersonaStates.Active, Entry(restarted, Warden).Activation!.State);
        Assert.Equal("persona-active", Refused(() =>
            restarted.Activate(Workspace, Warden, new PersonaActivateRequest(), SteppedUpOwner())).Code);
    }

    [Fact]
    public void Pause_resume_and_deactivation_need_no_step_up_and_are_audited()
    {
        var store = Store();
        store.Activate(Workspace, Warden, new PersonaActivateRequest(), SteppedUpOwner());
        var auditStart = _security.ControlPlaneAudit.HeadSequence;
        var owner = Owner();

        Assert.Equal(PersonaStates.Paused, store.Pause(Workspace, Warden, new PersonaChangeRequest("Noisy week"), owner).State);
        Assert.Equal(PersonaStates.Shadow, store.Resume(Workspace, Warden, new PersonaChangeRequest(), owner).State);
        store.Pause(Workspace, Warden, new PersonaChangeRequest(), owner);
        var deactivated = store.Deactivate(Workspace, Warden, new PersonaChangeRequest("Retune the charter"), owner);

        Assert.Equal((PersonaStates.Inactive, Now), (deactivated.State, deactivated.DeactivatedAt!.Value));
        Assert.Equal(["activate", "pause", "resume", "pause", "deactivate"], deactivated.History.Select(item => item.Action));
        // Repeating a change that already holds is not a change and writes nothing.
        store.Deactivate(Workspace, Warden, new PersonaChangeRequest(), owner);
        Assert.Equal(
            ["persona.pause", "persona.resume", "persona.pause", "persona.deactivate"],
            PersonaAudit(auditStart).Select(entry => entry.Operation));
        Assert.All(PersonaAudit(auditStart), entry => Assert.Equal($"human:{_ownerId}", entry.Actor));
        Assert.Equal("persona-not-active", Refused(() => store.Pause(Workspace, Warden, new PersonaChangeRequest(), owner)).Code);
        Assert.Equal("persona-not-active", Refused(() =>
            store.Deactivate(Workspace, "cost-controller", new PersonaChangeRequest(), owner)).Code);
        Assert.Equal("owner-required", Refused(() => store.Deactivate(Workspace, Warden, new PersonaChangeRequest(), Admin())).Code);

        // Activating again needs a fresh step-up, starts a new shadow period and keeps the history.
        Assert.Equal("step-up-required", Refused(() => store.Activate(Workspace, Warden, new PersonaActivateRequest(), owner)).Code);
        var again = store.Activate(Workspace, Warden, new PersonaActivateRequest(), SteppedUpOwner());
        Assert.Equal((PersonaStates.Shadow, 2, 6), (again.State, again.ActivationRevision, again.History.Count));
    }

    [Fact]
    public void A_fork_is_independent_of_later_catalogue_revisions_until_the_owner_adopts_one()
    {
        var store = Store();
        var fork = store.Fork(Workspace, Warden, new PersonaForkRequest("night-warden", "Night warden",
            "Watch backups and credentials overnight. Report gaps with evidence. Never change a setting yourself.",
            Scope: new PersonaScopeNarrowingV1(Areas: ["operations.backups", "access.credentials"])), Owner());
        Assert.Equal(("2026.11.1", "2026.11.1", Warden), (fork.Revisions[0].Revision, fork.Revisions[0].BasedOnRevision, fork.ForkOf));
        store.Activate(Workspace, "night-warden", new PersonaActivateRequest(), SteppedUpOwner());
        store.Activate(Workspace, Warden, new PersonaActivateRequest(), SteppedUpOwner());

        // The catalogue ships a new warden revision; the workspace state is read back after the restart.
        var current = new PersonaCatalogue();
        var next = current.Find(Warden)!.Descriptor with
        {
            Revision = "2026.11.2", Floors = [.. current.Find(Warden)!.Descriptor.Floors, "backup.destination-unset:95"],
        };
        var upgraded = Store(new PersonaCatalogue(current.Entries.Select(entry => entry.Descriptor.Id == Warden ? next : entry.Descriptor)));

        var forked = Entry(upgraded, "night-warden");
        Assert.Equal((PersonaSources.Fork, "2026.11.1", "2026.11.2"), (forked.Source, forked.Revision, forked.AdoptableRevision));
        Assert.Equal(fork.Revisions[0].Digest, forked.Digest);
        Assert.Equal(fork.Revisions[0].Digest, forked.Activation!.DescriptorDigest);
        Assert.DoesNotContain("backup.destination-unset:95", forked.Activation.Descriptor.Floors);
        var catalogueActivation = Entry(upgraded, Warden);
        Assert.Equal(("2026.11.2", "2026.11.1", "2026.11.2"),
            (catalogueActivation.Revision, catalogueActivation.Activation!.DescriptorRevision, catalogueActivation.AdoptableRevision));

        // Adopting into a live activation changes what runs, so it needs a fresh step-up.
        var auditStart = _security.ControlPlaneAudit.HeadSequence;
        Assert.Equal("step-up-required", Refused(() => upgraded.Adopt(Workspace, "night-warden", new PersonaChangeRequest(), Owner())).Code);
        var adopted = upgraded.Adopt(Workspace, "night-warden", new PersonaChangeRequest("Take the new floor"), SteppedUpOwner());

        Assert.Equal(("2026.11.2", null, "Night warden"), (adopted.Revision, adopted.AdoptableRevision, adopted.Title));
        Assert.Contains("backup.destination-unset:95", adopted.Descriptor.Floors);
        Assert.Equal(["operations.backups", "access.credentials"], adopted.Descriptor.Scope.Areas);
        Assert.StartsWith("Watch backups and credentials overnight.", adopted.Descriptor.Charter);
        Assert.Equal((2, "2026.11.2", adopted.Digest),
            (adopted.Activation!.ActivationRevision, adopted.Activation.DescriptorRevision, adopted.Activation.DescriptorDigest));
        Assert.Equal("persona-current", Refused(() =>
            upgraded.Adopt(Workspace, "night-warden", new PersonaChangeRequest(), SteppedUpOwner())).Code);
        // The catalogue activation still runs 2026.11.1 until the Owner adopts it as well.
        Assert.Equal("2026.11.1", Entry(upgraded, Warden).Activation!.DescriptorRevision);
        Assert.Equal("2026.11.2", upgraded.Adopt(Workspace, Warden, new PersonaChangeRequest(), SteppedUpOwner()).Activation!.DescriptorRevision);
        Assert.Equal(["persona.adopt", "persona.adopt"], PersonaAudit(auditStart).Select(entry => entry.Operation));
    }

    [Fact]
    public void A_fork_needs_the_owner_a_new_id_and_a_valid_descriptor()
    {
        var store = Store();
        var auditStart = _security.ControlPlaneAudit.HeadSequence;

        Assert.Equal("owner-required", Refused(() => store.Fork(Workspace, Warden, new PersonaForkRequest("mine"), Admin())).Code);
        Assert.Equal("persona-exists", Refused(() => store.Fork(Workspace, Warden, new PersonaForkRequest("cost-controller"), Owner())).Code);
        Assert.Equal("invalid-fork", Refused(() => store.Fork(Workspace, Warden, new PersonaForkRequest("Bad Id"), Owner())).Code);
        Assert.Equal("invalid-fork", Refused(() =>
            store.Fork(Workspace, Warden, new PersonaForkRequest("long", Charter: new string('x', 601)), Owner())).Code);
        Assert.Equal("persona-not-found", Refused(() => store.Fork(Workspace, "nobody", new PersonaForkRequest("x"), Owner())).Code);

        store.Fork(Workspace, Warden, new PersonaForkRequest("mine"), Owner());
        Assert.Equal("persona-exists", Refused(() => store.Fork(Workspace, Warden, new PersonaForkRequest("mine"), Owner())).Code);
        Assert.Equal("fork-source-not-catalogue", Refused(() =>
            store.Fork(Workspace, "mine", new PersonaForkRequest("mine-too"), Owner())).Code);
        Assert.Equal("persona.fork", Assert.Single(PersonaAudit(auditStart)).Operation);
        Assert.Equal(12, store.List(Workspace).Personas.Count);
    }

    [Fact]
    public void Activation_settings_only_narrow_the_descriptor_and_keep_its_rules()
    {
        var store = Store();
        var owner = SteppedUpOwner();
        PersonaActivateRequest[] invalid =
        [
            new(new PersonaAttributesOverrideV1(Autonomy: "A2")),
            new(new PersonaAttributesOverrideV1(Focus: new Dictionary<string, int> { ["weather"] = 100 })),
            new(new PersonaAttributesOverrideV1(Focus: new Dictionary<string, int> { ["backups"] = 50, ["credentials"] = 40 })),
            new(new PersonaAttributesOverrideV1(Thoroughness: 6)),
            new(Allowance: new PersonaAllowanceOverrideV1(TokensPerWeek: 2_000_000)),
            new(Allowance: new PersonaAllowanceOverrideV1(MinutesPerSession: 30)),
            new(Scope: new PersonaScopeNarrowingV1(Areas: ["frontend.visual"])),
            new(Scope: new PersonaScopeNarrowingV1(Areas: [])),
        ];
        foreach (var request in invalid)
            Assert.Equal("invalid-activation", Refused(() => store.Activate(Workspace, Warden, request, owner)).Code);
        Assert.Equal("persona-not-found", Refused(() => store.Activate(Workspace, "nobody", new PersonaActivateRequest(), owner)).Code);
        Assert.Equal("workspace-not-found", Refused(() => store.List("elsewhere")).Code);

        // A refused request consumes no step-up. Autonomy A0 grants observe and publish only.
        var observing = store.Activate(Workspace, Warden, new PersonaActivateRequest(new PersonaAttributesOverrideV1(Autonomy: "A0")), owner);
        Assert.Equal(["observe", "persona.item.publish", "persona.report.publish"], observing.Operations);
        var manager = store.Activate(Workspace, "chief-of-staff", new PersonaActivateRequest(), SteppedUpOwner());
        Assert.Contains("card.requeue", manager.Operations);
    }

    [Fact]
    public void Attribute_edit_validates_focus_and_autonomy_and_only_changes_activation_revision()
    {
        var store = Store();
        var original = store.Activate(Workspace, Warden, new PersonaActivateRequest(), SteppedUpOwner());
        var focus = original.Attributes.Focus!;
        var invalidFocus = focus.ToDictionary(pair => pair.Key, pair => pair.Value);
        invalidFocus[focus.Keys.First()]++;
        Assert.Equal("invalid-persona-attributes", Refused(() => store.ChangeAttributes(Workspace, Warden,
            new PersonaAttributeChangeRequest(new PersonaAttributesOverrideV1(Focus: invalidFocus)), Owner())).Code);
        Assert.Equal("invalid-persona-attributes", Refused(() => store.ChangeAttributes(Workspace, Warden,
            new PersonaAttributeChangeRequest(new PersonaAttributesOverrideV1(Autonomy: "A2")), Owner())).Code);
        var changed = store.ChangeAttributes(Workspace, Warden,
            new PersonaAttributeChangeRequest(new PersonaAttributesOverrideV1(Focus: focus,
                Autonomy: "A0", RiskAppetite: "low", Thoroughness: 4, SignalThreshold: 55)), SteppedUpOwner());
        Assert.Equal(original.ActivationRevision + 1, changed.ActivationRevision);
        Assert.Equal(original.Allowance, changed.Allowance);
        Assert.Equal(original.Scope, changed.Scope);
        Assert.Equal("A0", changed.Attributes.Autonomy);
        Assert.Equal("attributes", changed.History[^1].Action);
    }

    private PersonaActivationStore Store(PersonaCatalogue? catalogue = null)
        => new(Path.Combine(_root, ".metadata", "personas"), catalogue ?? new PersonaCatalogue(), _security,
            id => string.Equals(id, Workspace, StringComparison.OrdinalIgnoreCase) ? Workspace : null, _clock);

    private static PersonaEntryV1 Entry(PersonaActivationStore store, string id)
        => store.List(Workspace).Personas.Single(entry => entry.Id == id);

    private List<ControlPlaneAuditEntry> PersonaAudit(long after)
        => _security.ControlPlaneAudit.Read(after + 1, 100)
            .Where(entry => entry.Stream == ControlPlaneAuditStreams.Iam && entry.Target?.StartsWith("persona:") == true).ToList();

    private HumanPrincipal Owner() => _security.AuthenticateSession(_ownerSession)!;

    /// <summary>A fresh Owner session right after a second-factor step-up; the fake clock may have outrun older sessions.</summary>
    private HumanPrincipal SteppedUpOwner()
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        var session = _security.Login("first.owner", Password, "persona-tests", SecondFactorCodes.CurrentTotp(_totpSecret, Now)).SessionToken;
        var owner = _security.AuthenticateSession(session)!;
        _clock.Advance(TimeSpan.FromSeconds(30));
        _security.VerifyStepUp(owner, SecondFactorCodes.CurrentTotp(_totpSecret, Now));
        return owner;
    }

    private HumanPrincipal Admin()
    {
        var admin = _security.InviteUser(new InviteUserRequest("workspace.admin." + Guid.NewGuid().ToString("N")[..6], "Admin", "", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), _ownerId).User;
        return new HumanPrincipal(admin, new StudioSession
        {
            Id = "ses_admin", UserId = admin.Id, TokenHash = "", CsrfHash = "", CreatedAt = Now, LastSeenAt = Now,
            ExpiresAt = Now.AddHours(1), AbsoluteExpiresAt = Now.AddHours(1),
        });
    }

    private static SecurityOperationException Refused<T>(Func<T> action) => Assert.Throws<SecurityOperationException>(() => action());

    private sealed class ScopeComparer : IEqualityComparer<PersonaScopeV1>
    {
        public bool Equals(PersonaScopeV1? x, PersonaScopeV1? y)
            => x is not null && y is not null && x.Projects.SequenceEqual(y.Projects) && x.Areas.SequenceEqual(y.Areas)
               && (x.ExcludedPaths ?? []).SequenceEqual(y.ExcludedPaths ?? []);

        public int GetHashCode(PersonaScopeV1 obj) => obj.Projects.Count;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        File.Delete(_root + ".kek");
    }
}
