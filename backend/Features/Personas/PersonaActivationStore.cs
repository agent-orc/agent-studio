using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Security;
using AgentStudio.Quota;

namespace AgentStudio.Personas;

public static class PersonaStates
{
    /// <summary>The first report period after activation: the persona publishes to its own feed only.</summary>
    public const string Shadow = "shadow";
    public const string Active = "active";
    /// <summary>Scheduling stops; the grants stay.</summary>
    public const string Paused = "paused";
    /// <summary>Deactivated: grants revoked, history kept.</summary>
    public const string Inactive = "inactive";
}

public static class PersonaSources
{
    public const string Catalogue = "catalogue";
    public const string Fork = "fork";
}

public sealed record PersonaAttributesOverrideV1(
    IReadOnlyDictionary<string, int>? Focus = null,
    string? Autonomy = null,
    string? RiskAppetite = null,
    int? Thoroughness = null,
    int? SignalThreshold = null);

public sealed record PersonaAllowanceOverrideV1(
    int? TokensPerWeek = null,
    int? SessionsPerWeek = null,
    int? ProposalsPerWeek = null,
    int? DraftCardsPerWeek = null,
    int? MinutesPerSession = null,
    int? InvestigationsPerWeek = null);

/// <summary>Narrows the descriptor scope: projects and areas pick a subset, excluded paths add to the descriptor's.</summary>
public sealed record PersonaScopeNarrowingV1(
    IReadOnlyList<string>? Projects = null,
    IReadOnlyList<string>? Areas = null,
    IReadOnlyList<string>? ExcludedPaths = null);

public sealed record PersonaActivateRequest(
    PersonaAttributesOverrideV1? Attributes = null,
    PersonaAllowanceOverrideV1? Allowance = null,
    PersonaScopeNarrowingV1? Scope = null,
    string? Reason = null);

public sealed record PersonaChangeRequest(string? Reason = null);
public sealed record PersonaAttributeChangeRequest(PersonaAttributesOverrideV1 Attributes, string? Reason = null);

public sealed record PersonaForkRequest(
    string Id,
    string? Title = null,
    string? Charter = null,
    PersonaAttributesOverrideV1? Attributes = null,
    PersonaAllowanceOverrideV1? Budget = null,
    PersonaScopeNarrowingV1? Scope = null,
    string? Reason = null);

/// <summary>What the Owner set at activation; re-applied when the activation adopts a new descriptor revision.</summary>
public sealed record PersonaActivationSettingsV1(
    PersonaAttributesOverrideV1? Attributes,
    PersonaAllowanceOverrideV1? Allowance,
    PersonaScopeNarrowingV1? Scope);

public sealed record PersonaActivationEventV1(
    DateTime At, string Actor, string Action, string State, int ActivationRevision,
    string DescriptorRevision, string DescriptorDigest, string? Reason);

/// <summary>
/// One persona's activation in one workspace. <see cref="Descriptor"/> is the
/// snapshot the activation runs, so a later catalogue revision never changes a
/// running persona until the Owner adopts it.
/// </summary>
public sealed record PersonaActivationV1(
    string PersonaId,
    string Source,
    string State,
    int ActivationRevision,
    string DescriptorRevision,
    string DescriptorDigest,
    PersonaDescriptorV1 Descriptor,
    PersonaActivationSettingsV1 Settings,
    PersonaAttributesV1 Attributes,
    PersonaBudgetV1 Allowance,
    PersonaScopeV1 Scope,
    IReadOnlyList<string> Operations,
    DateTime ActivatedAt,
    string ActivatedBy,
    DateTime ShadowUntil,
    DateTime? PausedAt,
    DateTime? DeactivatedAt,
    IReadOnlyList<PersonaActivationEventV1> History)
{
    /// <summary>The catalogue or fork role used by this persona. PT-14 can create more instances of the same role.</summary>
    public string? RoleId { get; init; }
}

public sealed record PersonaForkOverridesV1(
    string? Title,
    string? Charter,
    PersonaAttributesOverrideV1? Attributes,
    PersonaAllowanceOverrideV1? Budget,
    PersonaScopeNarrowingV1? Scope);

public sealed record PersonaForkRevisionV1(
    string Revision, string Digest, string BasedOnRevision, string BasedOnDigest, PersonaDescriptorV1 Descriptor,
    DateTime CreatedAt, string CreatedBy, string? Reason);

/// <summary>A workspace's custom persona: a catalogue descriptor plus the Owner's changes, with its own revision chain.</summary>
public sealed record PersonaForkV1(
    string Id, string ForkOf, PersonaForkOverridesV1 Overrides, IReadOnlyList<PersonaForkRevisionV1> Revisions,
    DateTime CreatedAt, string CreatedBy);

public sealed record PersonaWorkspaceStateV1(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<PersonaActivationV1> Activations, IReadOnlyList<PersonaForkV1> Forks);

/// <summary>
/// One row of <c>GET /api/workspaces/{id}/personas</c>. <see cref="AdoptableRevision"/>
/// names a newer catalogue revision the Owner may adopt into a fork or a live activation.
/// </summary>
public sealed record PersonaEntryV1(
    string Id, string Source, string Title, string Aspect, string Kind, bool StartsActive, string Revision, string Digest,
    string? ForkOf, string? AdoptableRevision, PersonaDescriptorV1 Descriptor, PersonaActivationV1? Activation);

public sealed record PersonaListResponse(string WorkspaceId, IReadOnlyList<PersonaEntryV1> Personas);

/// <summary>
/// Application-owned persona activations and forks per workspace (persona team
/// PT-1, decision PT-D1 option B), stored under
/// <c>.metadata/personas/&lt;workspace&gt;.json</c> and written only through
/// <c>/api/workspaces/{id}/personas</c>. Activation needs the Owner and a fresh
/// step-up, records the descriptor revision and digest, and starts in
/// <see cref="PersonaStates.Shadow"/> for one report period. Pause, resume,
/// deactivate, fork and adopt need the Owner; adopting into a live activation
/// also needs a step-up, because it changes what runs. Every change goes to
/// the IAM audit. PT-2 creates its principal and grant during activation;
/// scheduling (PT-3) reads these records. No occurrence runs from here.
/// </summary>
public sealed partial class PersonaActivationStore
{
    public const int MaxReasonLength = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string? _root;
    private readonly PersonaCatalogue _catalogue;
    private readonly AccessSecurityStore _security;
    private readonly Func<string, string?> _resolveWorkspace;
    private readonly TimeProvider _time;
    private readonly PersonaSchedulingService? _scheduling;
    private readonly WorkspaceQuotaService? _quota;
    private readonly Dictionary<string, PersonaWorkspaceStateV1> _states = new(StringComparer.Ordinal);

    public PersonaActivationStore(IConfiguration config, PersonaCatalogue catalogue, AccessSecurityStore security,
        AgentStudio.Registry.WorkspaceRegistry workspaces, TimeProvider time,
        PersonaSchedulingService scheduling, WorkspaceQuotaService quota)
        : this(string.IsNullOrWhiteSpace(config["TaskRepository"]) ? null
            : Path.Combine(config["TaskRepository"]!, ".metadata", "personas"),
            catalogue, security, id => workspaces.Find(id)?.Id, time)
    {
        _scheduling = scheduling;
        _quota = quota;
    }

    /// <param name="resolveWorkspace">Returns the canonical workspace id, or null for an unknown workspace.</param>
    public PersonaActivationStore(string? root, PersonaCatalogue catalogue, AccessSecurityStore security,
        Func<string, string?> resolveWorkspace, TimeProvider time)
    {
        _root = root;
        _catalogue = catalogue;
        _security = security;
        _resolveWorkspace = resolveWorkspace;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private void ValidatePool(PersonaWorkspaceStateV1 state, string personaId, PersonaBudgetV1 allowance)
    {
        if (_scheduling is null || _quota is null) return;
        var active = state.Activations.Where(a => (a.State is PersonaStates.Active or PersonaStates.Shadow)
            && a.PersonaId != personaId).ToArray();
        var snapshot = _quota.Snapshot(state.WorkspaceId);
        var pool = _scheduling.Budget(state.WorkspaceId, Now,
            snapshot.Limits?.ModelTokensPerWeek, active).PoolTokens;
        if (active.Sum(a => (long)a.Allowance.TokensPerWeek) + allowance.TokensPerWeek > pool)
            throw new SecurityOperationException(409, "persona-pool-overallocated",
                "Active persona allowances exceed the workspace persona pool.");
    }

    public PersonaListResponse List(string workspaceId)
    {
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            var now = Now;
            var entries = new List<PersonaEntryV1>();
            foreach (var entry in _catalogue.Entries)
            {
                var activation = ActivationOf(state, entry.Descriptor.Id);
                var adoptable = activation is not null && activation.State != PersonaStates.Inactive
                                && activation.DescriptorDigest != entry.Digest ? entry.Descriptor.Revision : null;
                entries.Add(Entry(entry.Descriptor, entry.Digest, PersonaSources.Catalogue, null, adoptable, activation, now));
            }
            foreach (var fork in state.Forks.OrderBy(fork => fork.Id, StringComparer.Ordinal))
            {
                var latest = fork.Revisions[^1];
                var basis = _catalogue.Find(fork.ForkOf);
                var adoptable = basis is not null && basis.Digest != latest.BasedOnDigest ? basis.Descriptor.Revision : null;
                entries.Add(Entry(latest.Descriptor, latest.Digest, PersonaSources.Fork, fork.ForkOf, adoptable,
                    ActivationOf(state, fork.Id), now));
            }
            return new PersonaListResponse(state.WorkspaceId, entries);
        }
    }

    public PersonaActivationV1 Activate(string workspaceId, string personaId, PersonaActivateRequest request, HumanPrincipal owner)
    {
        var reason = Reason(request.Reason);
        _security.RequirePersonaOwner(owner);
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            var (source, descriptor, digest) = Descriptor(state, personaId);
            var existing = ActivationOf(state, personaId);
            if (existing is not null && existing.State != PersonaStates.Inactive)
                throw new SecurityOperationException(409, "persona-active",
                    "This persona is already activated. Deactivate it first, or adopt a newer revision.");
            var settings = new PersonaActivationSettingsV1(request.Attributes, request.Allowance, request.Scope);
            var (attributes, allowance, scope) = Effective(descriptor, settings, "invalid-activation");
            ValidatePool(state, personaId, allowance);
            _security.ConsumePersonaActivationStepUp(owner);
            var now = Now;
            var revision = (existing?.ActivationRevision ?? 0) + 1;
            var operations = PersonaAutonomy.Grantable(descriptor, attributes.Autonomy);
            var activation = new PersonaActivationV1(personaId, source, PersonaStates.Shadow, revision, descriptor.Revision, digest,
                descriptor, settings, attributes, allowance, scope, operations, now, owner.User.Id,
                now + (descriptor.Id == "operations-warden" ? TimeSpan.FromDays(14)
                    : PersonaDescriptorValidator.ReportPeriod(descriptor.Cadence)), null, null,
                [.. (existing?.History ?? []), new PersonaActivationEventV1(now, owner.User.Id, "activate", PersonaStates.Shadow,
                    revision, descriptor.Revision, digest, reason)]) { RoleId = descriptor.Id };
            _security.ActivatePersonaPrincipal(state.WorkspaceId, activation, owner.User.Id);
            SaveLocked(state with { Activations = Replace(state.Activations, activation) });
            _security.AppendPersonaAudit(owner.User.Id, "persona.activate", state.WorkspaceId, personaId, new
            {
                source, state = activation.State, activationRevision = revision, descriptorRevision = descriptor.Revision,
                descriptorDigest = digest, attributes, allowance, scope, operations, activation.ShadowUntil, reason,
            });
            return activation;
        }
    }

    public PersonaActivationV1 ChangeAttributes(string workspaceId, string personaId,
        PersonaAttributeChangeRequest request, HumanPrincipal owner)
    {
        _security.RequirePersonaOwner(owner);
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            var current = ActivationOf(state, personaId) ?? throw NotActive();
            if (current.State == PersonaStates.Inactive) throw NotActive();
            var settings = current.Settings with { Attributes = request.Attributes };
            var (attributes, _, _) = Effective(current.Descriptor, settings, "invalid-persona-attributes");
            _security.ConsumePersonaActivationStepUp(owner);
            var now = Now;
            var revision = current.ActivationRevision + 1;
            var changed = current with
            {
                ActivationRevision = revision, Settings = settings, Attributes = attributes,
                Operations = PersonaAutonomy.Grantable(current.Descriptor, attributes.Autonomy),
                History = [.. current.History, new PersonaActivationEventV1(now, owner.User.Id,
                    "attributes", current.State, revision, current.DescriptorRevision,
                    current.DescriptorDigest, Reason(request.Reason))],
            };
            _security.ActivatePersonaPrincipal(state.WorkspaceId, changed, owner.User.Id);
            SaveLocked(state with { Activations = Replace(state.Activations, changed) });
            _security.AppendPersonaAudit(owner.User.Id, "persona.attributes.change", state.WorkspaceId,
                personaId, new { revision, attributes, request.Reason });
            return changed;
        }
    }

    public PersonaActivationV1 Pause(string workspaceId, string personaId, PersonaChangeRequest request, HumanPrincipal owner)
        => Transition(workspaceId, personaId, request, owner, "pause", (activation, now) => activation.State switch
        {
            PersonaStates.Paused => null,
            PersonaStates.Inactive => throw NotActive(),
            _ => activation with { State = PersonaStates.Paused, PausedAt = now },
        });

    public PersonaActivationV1 Resume(string workspaceId, string personaId, PersonaChangeRequest request, HumanPrincipal owner)
        => Transition(workspaceId, personaId, request, owner, "resume", (activation, now) => activation.State switch
        {
            PersonaStates.Paused => activation with
            {
                State = now < activation.ShadowUntil ? PersonaStates.Shadow : PersonaStates.Active, PausedAt = null,
            },
            PersonaStates.Inactive => throw NotActive(),
            _ => null,
        });

    /// <summary>Needs no step-up: taking authority away never waits for a second factor.</summary>
    public PersonaActivationV1 Deactivate(string workspaceId, string personaId, PersonaChangeRequest request, HumanPrincipal owner)
        => Transition(workspaceId, personaId, request, owner, "deactivate", (activation, now) => activation.State switch
        {
            PersonaStates.Inactive => null,
            _ => activation with { State = PersonaStates.Inactive, PausedAt = null, DeactivatedAt = now },
        });

    /// <summary>Emergency team stop leaves descriptor snapshots and history available for later reactivation.</summary>
    public IReadOnlyList<string> StopAll(string workspaceId, HumanPrincipal owner)
    {
        _security.RequirePersonaOwner(owner);
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            var principalIds = _security.StopAllPersonaPrincipals(state.WorkspaceId, owner.User.Id);
            var now = Now;
            var activations = state.Activations.Select(activation => activation.State == PersonaStates.Inactive
                ? activation : activation with
                {
                    State = PersonaStates.Inactive, PausedAt = null, DeactivatedAt = now,
                    History = [.. activation.History, new PersonaActivationEventV1(now, owner.User.Id, "stop-all",
                        PersonaStates.Inactive, activation.ActivationRevision, activation.DescriptorRevision,
                        activation.DescriptorDigest, "Emergency team stop")],
                }).ToList();
            SaveLocked(state with { Activations = activations });
            return principalIds;
        }
    }

    /// <summary>Copies a catalogue descriptor into a custom persona of this workspace with its own revision chain.</summary>
    public PersonaForkV1 Fork(string workspaceId, string personaId, PersonaForkRequest request, HumanPrincipal owner)
    {
        var reason = Reason(request.Reason);
        _security.RequirePersonaOwner(owner);
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            var basis = _catalogue.Find(personaId);
            if (basis is null)
                throw state.Forks.Any(fork => fork.Id == personaId)
                    ? new SecurityOperationException(409, "fork-source-not-catalogue", "Fork a catalogue persona, not a custom one.")
                    : PersonaNotFound(personaId);
            var id = request.Id?.Trim() ?? "";
            if (id.Length > 64 || !PersonaDescriptorValidator.Slug().IsMatch(id))
                throw new SecurityOperationException(400, "invalid-fork", "A custom persona id is a lowercase slug of at most 64 characters.");
            if (_catalogue.Find(id) is not null || state.Forks.Any(fork => fork.Id == id))
                throw new SecurityOperationException(409, "persona-exists", $"A persona with id '{id}' already exists.");
            var overrides = new PersonaForkOverridesV1(request.Title, request.Charter, request.Attributes, request.Budget, request.Scope);
            var now = Now;
            var revision = ForkRevision(now, 1);
            var descriptor = ForkDescriptor(basis.Descriptor, id, revision, overrides, "invalid-fork");
            var fork = new PersonaForkV1(id, personaId, overrides,
                [new PersonaForkRevisionV1(revision, PersonaDescriptorDigest.Compute(descriptor), basis.Descriptor.Revision,
                    basis.Digest, descriptor, now, owner.User.Id, reason)], now, owner.User.Id);
            SaveLocked(state with { Forks = [.. state.Forks, fork] });
            _security.AppendPersonaAudit(owner.User.Id, "persona.fork", state.WorkspaceId, id, new
            {
                forkOf = personaId, revision, digest = fork.Revisions[0].Digest, basedOnRevision = basis.Descriptor.Revision,
                basedOnDigest = basis.Digest, reason,
            });
            return fork;
        }
    }

    /// <summary>
    /// Adopts the current catalogue revision: a fork gains a new revision built
    /// from it and the fork's own changes; a live activation then runs the new
    /// descriptor. Changing what a live activation runs needs a fresh step-up.
    /// </summary>
    public PersonaEntryV1 Adopt(string workspaceId, string personaId, PersonaChangeRequest request, HumanPrincipal owner)
    {
        var reason = Reason(request.Reason);
        _security.RequirePersonaOwner(owner);
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            var now = Now;
            var fork = state.Forks.FirstOrDefault(candidate => candidate.Id == personaId);
            var catalogueEntry = fork is null ? _catalogue.Find(personaId) ?? throw PersonaNotFound(personaId) : null;
            var basis = fork is null ? catalogueEntry! : _catalogue.Find(fork.ForkOf)
                ?? throw new SecurityOperationException(409, "catalogue-entry-missing",
                    $"The catalogue no longer has '{fork.ForkOf}', so there is no revision to adopt.");
            var activation = ActivationOf(state, personaId);
            var live = activation is not null && activation.State != PersonaStates.Inactive;

            PersonaDescriptorV1 descriptor;
            string digest;
            var forks = state.Forks;
            if (fork is not null)
            {
                if (fork.Revisions[^1].BasedOnDigest == basis.Digest) throw AlreadyCurrent();
                var revision = ForkRevision(now, fork.Revisions.Count + 1);
                descriptor = ForkDescriptor(basis.Descriptor, fork.Id, revision, fork.Overrides, "adopt-conflict");
                digest = PersonaDescriptorDigest.Compute(descriptor);
                forks = [.. state.Forks.Where(candidate => candidate.Id != fork.Id), fork with
                {
                    Revisions = [.. fork.Revisions, new PersonaForkRevisionV1(revision, digest, basis.Descriptor.Revision,
                        basis.Digest, descriptor, now, owner.User.Id, reason)],
                }];
            }
            else
            {
                if (!live || activation!.DescriptorDigest == basis.Digest) throw AlreadyCurrent();
                (descriptor, digest) = (basis.Descriptor, basis.Digest);
            }

            var activations = state.Activations;
            if (live)
            {
                var (attributes, allowance, scope) = Effective(descriptor, activation!.Settings, "adopt-conflict");
                ValidatePool(state, personaId, allowance);
                _security.ConsumePersonaActivationStepUp(owner);
                var activationRevision = activation.ActivationRevision + 1;
                activations = Replace(activations, activation with
                {
                    ActivationRevision = activationRevision, DescriptorRevision = descriptor.Revision, DescriptorDigest = digest,
                    Descriptor = descriptor, Attributes = attributes, Allowance = allowance, Scope = scope,
                    Operations = PersonaAutonomy.Grantable(descriptor, attributes.Autonomy),
                    History = [.. activation.History, new PersonaActivationEventV1(now, owner.User.Id, "adopt",
                        Project(activation, now).State, activationRevision, descriptor.Revision, digest, reason)],
                });
            }
            var next = state with { Activations = activations, Forks = forks };
            if (live && ActivationOf(next, personaId) is { } updated)
                _security.ActivatePersonaPrincipal(state.WorkspaceId, updated, owner.User.Id);
            SaveLocked(next);
            _security.AppendPersonaAudit(owner.User.Id, "persona.adopt", state.WorkspaceId, personaId, new
            {
                source = fork is null ? PersonaSources.Catalogue : PersonaSources.Fork, catalogueRevision = basis.Descriptor.Revision,
                descriptorRevision = descriptor.Revision, descriptorDigest = digest, activationUpdated = live, reason,
            });
            return Entry(descriptor, digest, fork is null ? PersonaSources.Catalogue : PersonaSources.Fork, fork?.ForkOf, null,
                ActivationOf(next, personaId), now);
        }
    }

    private PersonaActivationV1 Transition(string workspaceId, string personaId, PersonaChangeRequest request,
        HumanPrincipal owner, string action, Func<PersonaActivationV1, DateTime, PersonaActivationV1?> change)
    {
        var reason = Reason(request.Reason);
        _security.RequirePersonaOwner(owner);
        lock (_gate)
        {
            var state = StateLocked(Workspace(workspaceId));
            Descriptor(state, personaId);
            var now = Now;
            var current = ActivationOf(state, personaId) is { } stored ? Project(stored, now) : throw NotActive();
            var changed = change(current, now);
            if (changed is null) return current;
            if (action == "deactivate") _security.StopPersonaPrincipal(state.WorkspaceId, personaId, owner.User.Id, "persona deactivated");
            changed = changed with
            {
                History = [.. current.History, new PersonaActivationEventV1(now, owner.User.Id, action, changed.State,
                    current.ActivationRevision, current.DescriptorRevision, current.DescriptorDigest, reason)],
            };
            SaveLocked(state with { Activations = Replace(state.Activations, changed) });
            _security.AppendPersonaAudit(owner.User.Id, "persona." + action, state.WorkspaceId, personaId, new
            {
                from = current.State, state = changed.State, activationRevision = current.ActivationRevision,
                descriptorRevision = current.DescriptorRevision, descriptorDigest = current.DescriptorDigest, reason,
            });
            return changed;
        }
    }

    /// <summary>The shadow period ends by time; reads report it as active from then on.</summary>
    private static PersonaActivationV1 Project(PersonaActivationV1 activation, DateTime now)
        => activation.State == PersonaStates.Shadow && now >= activation.ShadowUntil
            ? activation with { State = PersonaStates.Active }
            : activation;

    private static PersonaEntryV1 Entry(PersonaDescriptorV1 descriptor, string digest, string source, string? forkOf,
        string? adoptable, PersonaActivationV1? activation, DateTime now)
        => new(descriptor.Id, source, descriptor.Title, descriptor.Aspect, descriptor.Kind, descriptor.StartsActive == true,
            descriptor.Revision, digest, forkOf, adoptable, descriptor, activation is null ? null : Project(activation, now));

    private (string Source, PersonaDescriptorV1 Descriptor, string Digest) Descriptor(PersonaWorkspaceStateV1 state, string personaId)
    {
        if (_catalogue.Find(personaId) is { } entry) return (PersonaSources.Catalogue, entry.Descriptor, entry.Digest);
        var fork = state.Forks.FirstOrDefault(candidate => candidate.Id == personaId) ?? throw PersonaNotFound(personaId);
        return (PersonaSources.Fork, fork.Revisions[^1].Descriptor, fork.Revisions[^1].Digest);
    }

    private static PersonaActivationV1? ActivationOf(PersonaWorkspaceStateV1 state, string personaId)
        => state.Activations.FirstOrDefault(activation => activation.PersonaId == personaId);

    private static IReadOnlyList<PersonaActivationV1> Replace(IReadOnlyList<PersonaActivationV1> activations, PersonaActivationV1 next)
        => [.. activations.Where(activation => activation.PersonaId != next.PersonaId), next];

    /// <summary>
    /// The attributes, allowance and scope an activation runs: the descriptor
    /// defaults with the Owner's settings applied. Focus areas stay those the
    /// descriptor names, A2 stays with managers, and scope only narrows.
    /// </summary>
    private static (PersonaAttributesV1, PersonaBudgetV1, PersonaScopeV1) Effective(
        PersonaDescriptorV1 descriptor, PersonaActivationSettingsV1 settings, string code)
    {
        var focus = settings.Attributes?.Focus;
        if (focus is not null && (descriptor.Attributes.Focus is null
                                  || focus.Keys.Any(key => !descriptor.Attributes.Focus.ContainsKey(key))))
            throw new SecurityOperationException(400, code, "Focus weights may only name the descriptor's focus areas.");
        var attributes = ApplyAttributes(descriptor.Attributes, settings.Attributes);
        var allowance = ApplyBudget(descriptor.Budget, settings.Allowance);
        var scope = Narrow(descriptor.Scope, settings.Scope, code);
        var errors = new List<string>();
        PersonaDescriptorValidator.ValidateAttributes(descriptor.Kind, attributes, errors);
        PersonaDescriptorValidator.ValidateBudget(allowance, errors);
        PersonaDescriptorValidator.ValidateScope(scope, errors);
        if (errors.Count > 0) throw new SecurityOperationException(400, code, string.Join(" ", errors));
        return (attributes, allowance, scope);
    }

    private static PersonaAttributesV1 ApplyAttributes(PersonaAttributesV1 basis, PersonaAttributesOverrideV1? change)
        => change is null ? basis : new PersonaAttributesV1(
            change.Autonomy ?? basis.Autonomy, change.RiskAppetite ?? basis.RiskAppetite, change.Thoroughness ?? basis.Thoroughness,
            change.SignalThreshold ?? basis.SignalThreshold, change.Focus ?? basis.Focus);

    private static PersonaBudgetV1 ApplyBudget(PersonaBudgetV1 basis, PersonaAllowanceOverrideV1? change)
        => change is null ? basis : new PersonaBudgetV1(
            change.TokensPerWeek ?? basis.TokensPerWeek, change.SessionsPerWeek ?? basis.SessionsPerWeek,
            change.ProposalsPerWeek ?? basis.ProposalsPerWeek, change.DraftCardsPerWeek ?? basis.DraftCardsPerWeek,
            change.MinutesPerSession ?? basis.MinutesPerSession, change.InvestigationsPerWeek ?? basis.InvestigationsPerWeek);

    private static PersonaScopeV1 Narrow(PersonaScopeV1 basis, PersonaScopeNarrowingV1? narrowing, string code)
    {
        if (narrowing is null) return basis;
        var projects = narrowing.Projects?.Select(project => project?.Trim() ?? "").ToList();
        if (projects is not null && !basis.Projects.Contains("*") && projects.Any(project => !basis.Projects.Contains(project)))
            throw new SecurityOperationException(400, code, "Scope may only narrow the descriptor's projects.");
        if (narrowing.Areas is not null && narrowing.Areas.Any(area => !basis.Areas.Contains(area)))
            throw new SecurityOperationException(400, code, "Scope may only narrow the descriptor's areas.");
        var excluded = (basis.ExcludedPaths ?? []).Concat(narrowing.ExcludedPaths ?? []).Distinct(StringComparer.Ordinal).ToList();
        return new PersonaScopeV1(projects ?? basis.Projects, narrowing.Areas ?? basis.Areas, excluded.Count == 0 ? null : excluded);
    }

    private static PersonaDescriptorV1 ForkDescriptor(PersonaDescriptorV1 basis, string id, string revision,
        PersonaForkOverridesV1 overrides, string code)
    {
        var (attributes, budget, scope) = Effective(basis,
            new PersonaActivationSettingsV1(overrides.Attributes, overrides.Budget, overrides.Scope), code);
        var descriptor = basis with
        {
            Id = id, Revision = revision, StartsActive = null, Title = overrides.Title?.Trim() ?? basis.Title,
            Charter = overrides.Charter?.Trim() ?? basis.Charter, Attributes = attributes, Budget = budget, Scope = scope,
        };
        var errors = PersonaDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0) throw new SecurityOperationException(400, code, string.Join(" ", errors));
        return descriptor;
    }

    /// <summary>A fork revision in the catalogue format; the sequence is the position in the fork's own chain.</summary>
    private static string ForkRevision(DateTime now, int sequence) => $"{now.Year}.{now.Month}.{sequence}";

    private static string? Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length <= MaxReasonLength ? trimmed
            : throw new SecurityOperationException(400, "invalid-reason", $"A reason has at most {MaxReasonLength} characters.");
    }

    private string Workspace(string workspaceId)
        => (string.IsNullOrWhiteSpace(workspaceId) ? null : _resolveWorkspace(workspaceId.Trim())) is { } id && SafeId().IsMatch(id)
            ? id
            : throw new SecurityOperationException(404, "workspace-not-found", $"Workspace '{workspaceId}' was not found.");

    private PersonaWorkspaceStateV1 StateLocked(string workspaceId)
    {
        if (_states.TryGetValue(workspaceId, out var cached)) return cached;
        var empty = new PersonaWorkspaceStateV1(1, workspaceId, [], []);
        var path = PathFor(workspaceId);
        if (path is null || !File.Exists(path)) return _states[workspaceId] = empty;
        try
        {
            var loaded = JsonSerializer.Deserialize<PersonaWorkspaceStateV1>(File.ReadAllText(path), Json)
                ?? throw new JsonException("empty persona state");
            return _states[workspaceId] = loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new SecurityOperationException(500, "persona-store-unreadable",
                $"The persona records of workspace '{workspaceId}' could not be read: {ex.Message}");
        }
    }

    private void SaveLocked(PersonaWorkspaceStateV1 state)
    {
        if (PathFor(state.WorkspaceId) is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, path, overwrite: true);
        }
        _states[state.WorkspaceId] = state;
    }

    private string? PathFor(string workspaceId) => _root is null ? null : Path.Combine(_root, workspaceId + ".json");

    private static SecurityOperationException PersonaNotFound(string personaId)
        => new(404, "persona-not-found", $"Persona '{personaId}' is neither in the catalogue nor a custom persona of this workspace.");

    private static SecurityOperationException NotActive()
        => new(409, "persona-not-active", "This persona is not activated in this workspace.");

    private static SecurityOperationException AlreadyCurrent()
        => new(409, "persona-current", "This persona already runs the current catalogue revision.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex SafeId();
}
