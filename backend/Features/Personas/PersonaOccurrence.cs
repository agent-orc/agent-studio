using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Bus;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Security;
using AgentStudio.Management;
using AgentStudio.Tasks;
using AgentStudio.Watcher;

namespace AgentStudio.Personas;

public sealed record PersonaGroundsSourceV1(string Ref, string Kind, string? Project,
    int FocusWeight, string Summary, bool Measured, int Floor = 0,
    DateTime? ObservedAtUtc = null, string? Fingerprint = null);
public sealed record PersonaGroundsV1(int SchemaVersion, string OccurrenceKey, string PersonaId,
    string RoleId, string WorkspaceId, string DescriptorRevision, string DescriptorDigest,
    IReadOnlyList<PersonaGroundsSourceV1> Sources, IReadOnlyList<string> TriggerRefs,
    int MaxInputTokens = 12_000)
{
    public int ActivationRevision { get; init; }
    public int OmittedSourceCount { get; init; }
    public IReadOnlyList<string> AllowedProjects { get; init; } = [];
    public IReadOnlyList<string> AllowedAreas { get; init; } = [];
    public IReadOnlyList<PersonaManagerGroundItemV1> ManagerItems { get; init; } = [];
}
public sealed record PersonaManagerGroundItemV1(string Id, string Kind, int Impact, int Urgency,
    double Confidence, int Floor, int Score, string PersonaId, string Fingerprint,
    string State, IReadOnlyList<string> Links);
public sealed record PersonaOccurrenceResponseV1(IReadOnlyList<PersonaItemDraftV1> Items);
public sealed record PersonaLeaseV1(string Key, string ClaimToken, string WorkspaceId,
    string PersonaId, string RoleId, string Prompt, string Model, string Thinking,
    int TimeoutSeconds, int MaxInputTokens, int MaxOutputTokens, bool Investigation = false,
    string? SessionId = null, IReadOnlyList<string>? RepositoryPaths = null);
public sealed record PersonaLeaseClaimRequest(string RunnerId);
public sealed record PersonaLeaseCompletion(string RunnerId, string WorkspaceId, string Key,
    string ClaimToken, bool Success, string? Response, long InputTokens, long OutputTokens,
    string? Model, string? Error = null, long CacheReadTokens = 0,
    long CacheWriteTokens = 0);

public static class PersonaOutputValidator
{
    public static string? Validate(PersonaOccurrenceResponseV1 response, PersonaGroundsV1 grounds,
        PersonaActivationV1 activation, IReadOnlyList<PersonaItemV1> previous,
        IReadOnlyCollection<WatcherProposal> watcherProposals, IReadOnlyList<TaskInfo> cards,
        DateTime now)
    {
        if (response.Items is null || response.Items.Count > activation.Descriptor.Report.MaxItems)
            return "item-count-exceeded";
        var refs = grounds.Sources.Select(s => s.Ref).ToHashSet(StringComparer.Ordinal);
        var projects = grounds.Sources.Where(s => s.Project is not null)
            .Select(s => s.Project!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        projects.UnionWith(grounds.AllowedProjects);
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var proposals = previous.Count(i => i.PersonaId == activation.PersonaId
            && i.Kind == PersonaItemKinds.Proposal && i.CreatedAtUtc >= weekStart);
        var cardsDrafted = previous.Count(i => i.PersonaId == activation.PersonaId
            && i.DraftCard && i.CreatedAtUtc >= weekStart);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in response.Items)
        {
            if (item is null || !PersonaItemKinds.All.Contains(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 120 ||
                string.IsNullOrWhiteSpace(item.Body) || item.Body.Length > 1_200 ||
                string.IsNullOrWhiteSpace(item.FingerprintKey) || item.FingerprintKey.Length > 200 ||
                item.Evidence is null || item.Evidence.Count > 10 ||
                item.Kind != PersonaItemKinds.ReportLine && item.Evidence.Count == 0 ||
                item.Area is { Length: > 100 } || item.DossierKey is { Length: > 100 } ||
                item.DossierSection is { Length: > 100 } ||
                item.DraftTitle is { Length: > 120 } || item.DraftPrompt is { Length: > 4_000 } ||
                item.Impact is < 1 or > 5 || item.Urgency is < 1 or > 5 ||
                !double.IsFinite(item.Confidence) || item.Confidence is < 0 or > 1)
                return "item-schema-invalid";
            if (item.Evidence.Any(e => e is null || !refs.Contains(e.Ref)))
                return "reference-unresolvable";
            if (item.Project is not null && !projects.Contains(item.Project))
                return "project-out-of-scope";
            if (item.Area is not null && !activation.Scope.Areas.Contains(item.Area,
                    StringComparer.OrdinalIgnoreCase))
                return "area-out-of-scope";
            if (item.Evidence.Any(e => e.Measured && grounds.Sources.Any(s => s.Ref == e.Ref && !s.Measured)))
                return "measurement-unverified";
            if (item.Evidence.Any(e => grounds.Sources.Any(s => s.Ref == e.Ref && s.Project is not null
                && !string.Equals(s.Project, item.Project, StringComparison.OrdinalIgnoreCase))))
                return "reference-out-of-scope";
            if (item.Kind == PersonaItemKinds.DecisionRequest &&
                (item.Options is null || item.Options.Count is < 2 or > 4 ||
                 item.RecommendedOption is null || !item.Options.Contains(item.RecommendedOption) ||
                 item.DeadlineUtc is null || !item.Body.Contains("nothing changes", StringComparison.OrdinalIgnoreCase)))
                return "decision-options-invalid";
            if (item.Kind == PersonaItemKinds.Proposal &&
                (string.IsNullOrWhiteSpace(item.DossierKey) || string.IsNullOrWhiteSpace(item.DossierSection)
                 || item.DossierKey.Length > 100 || item.DossierSection.Length > 100 ||
                 item.DraftCard && (string.IsNullOrWhiteSpace(item.DraftTitle)
                     || string.IsNullOrWhiteSpace(item.DraftPrompt)
                     || item.DraftTitle.Length > 120 || item.DraftPrompt.Length > 4_000)))
                return "proposal-schema-invalid";
            if (item.Kind == PersonaItemKinds.Proposal && !grounds.Sources.Any(s =>
                    s.Ref == "dossier:" + item.DossierKey &&
                    string.Equals(s.Project, item.Project, StringComparison.OrdinalIgnoreCase)))
                return "dossier-reference-unresolvable";
            if (item.DraftCard && item.Kind != PersonaItemKinds.Proposal)
                return "draft-card-kind-invalid";
            var operation = item.Kind switch
            {
                PersonaItemKinds.Finding => "persona.item.publish",
                PersonaItemKinds.Proposal => "persona.proposal.draft",
                PersonaItemKinds.DecisionRequest => "persona.decision.request",
                _ => "persona.report.publish",
            };
            if (!activation.Operations.Contains(operation, StringComparer.Ordinal))
                return "persona-operation-not-granted";
            var fingerprint = PersonaFingerprint.Compute(item, grounds);
            if (!seen.Add(fingerprint)) return "duplicate-in-response";
            var prior = previous.Any(i => i.PersonaId == activation.PersonaId
                && i.Fingerprint == fingerprint && PersonaItemStates.IsDecidable(i.State));
            // Scout lifecycle: a rejected proposal stays suppressed until the persona cites new evidence.
            if (item.Kind == PersonaItemKinds.Proposal && !prior && previous.Any(i => i.Fingerprint == fingerprint
                    && i.State == "rejected" && item.Evidence.All(e => i.Evidence.Any(r => r.Ref == e.Ref))))
                return "proposal-rejection-suppressed";
            if (!prior && previous.Any(i => i.PersonaId != activation.PersonaId
                    && i.Fingerprint == fingerprint && i.State is "open" or "draft"))
                return "duplicate-existing-item";
            if (item.Kind == PersonaItemKinds.Proposal && !prior)
            {
                if (watcherProposals.Any(p => p.Fingerprint == fingerprint ||
                    p.Project.Equals(item.Project, StringComparison.OrdinalIgnoreCase)
                    && p.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase)) ||
                    cards.Any(c => c.ProjectName.Equals(item.Project, StringComparison.OrdinalIgnoreCase)
                        && (c.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase)
                            || c.Tags.Contains(fingerprint, StringComparer.OrdinalIgnoreCase))
                        && c.State != TaskStates.Archive))
                    return "proposal-duplicate-existing";
                proposals++;
                if (item.DraftCard) cardsDrafted++;
            }
        }
        if (proposals > activation.Allowance.ProposalsPerWeek) return "proposal-week-limit";
        if (cardsDrafted > activation.Allowance.DraftCardsPerWeek) return "card-week-limit";
        if (activation.Descriptor.Report.MaxWords is { } words && response.Items
                .Where(i => i.Kind == PersonaItemKinds.ReportLine)
                .Sum(i => i.Body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) > words)
            return "report-word-limit";
        return null;
    }
}

/// <summary>Host lease authority and the only path from model output to persona items.</summary>
public sealed class PersonaOccurrenceService
{
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly object _gate = new();
    private readonly PersonaSchedulingService _scheduling;
    private readonly PersonaActivationStore _activations;
    private readonly PersonaItemStore _items;
    private readonly PersonaReportStore _reports;
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceRegistry _workspaces;
    private readonly TaskScannerService _cards;
    private readonly WatcherCaseStore _cases;
    private readonly WatcherProposalStore _proposals;
    private readonly AgentMessageBusStore _bus;
    private readonly AccessSecurityStore _security;
    private readonly PersonaManagerService _managers;
    private readonly StudioBackupService _backups;
    private readonly SecurityPostureService _posture;
    private readonly HashSet<string> _wardenDays = new(StringComparer.Ordinal);
    private readonly string? _root;

    public PersonaOccurrenceService(PersonaManagerService managers,
        PersonaSchedulingService scheduling, PersonaActivationStore activations,
        PersonaItemStore items, PersonaReportStore reports, ProjectRegistry projects,
        WorkspaceRegistry workspaces,
        TaskScannerService cards,
        WatcherCaseStore cases, WatcherProposalStore proposals, AgentMessageBusStore bus,
        AccessSecurityStore security, StudioBackupService backups, SecurityPostureService posture,
        IConfiguration configuration)
    {
        _managers = managers;
        _scheduling = scheduling; _activations = activations; _items = items; _reports = reports;
        _projects = projects;
        _workspaces = workspaces;
        _cards = cards; _cases = cases; _proposals = proposals; _bus = bus;
        _security = security;
        _backups = backups; _posture = posture;
        _root = configuration["TaskRepository"];
    }

    /// <summary>Daily model-free warden pass. It also runs when admission declines the model call.</summary>
    public async Task RunWardenAsync(DateTime now, CancellationToken ct)
    {
        if (_root is null) return;
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var pending = _workspaces.List().SelectMany(workspace =>
            _activations.List(workspace.Id).Personas.Select(p => p.Activation)
                .Where(a => a?.RoleId == "operations-warden" &&
                    a.State is PersonaStates.Active or PersonaStates.Shadow)
                .Select(activation => (workspace, activation)))
            .Where(row => row.activation is not null &&
                !_wardenDays.Contains(row.workspace.Id + ":" + row.activation.PersonaId + ":" + day) &&
                _scheduling.Receipts(row.workspace.Id).Any(receipt =>
                    receipt.PersonaId == row.activation.PersonaId && receipt.CreatedAtUtc.Date == now.Date))
            .ToArray();
        if (pending.Length == 0) return;
        BackupOverview? backup;
        SecurityPostureResponse? security;
        try { backup = _backups.Overview(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { backup = null; }
        try { security = await _posture.ReadAsync(ct); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException) { security = null; }
        var snapshot = OpsPostureReader.Read(now, backup, security);
        foreach (var (workspace, activation) in pending)
        {
            if (activation is null) continue;
            var runKey = workspace.Id + ":" + activation.PersonaId + ":" + day;
            lock (_gate) if (!_wardenDays.Add(runKey)) continue;
            var roleId = activation.RoleId ?? activation.Descriptor.Id;
            var backupCard = OpsWardenEvaluator.OpenBackupCard(_cards.GetResidentIdentityFacts());
            foreach (var finding in OpsWardenEvaluator.Evaluate(snapshot, activation.Descriptor))
            {
                var evidence = new[] { new PersonaEvidenceV1("ops-source:" + finding.Fact.Source, true),
                    new PersonaEvidenceV1("ops-fact:" + finding.Fact.Id, true) };
                var draft = new PersonaItemDraftV1(PersonaItemKinds.Finding, finding.Title,
                    "Observed " + finding.Fact.Id + " as " + (finding.Fact.Value ?? "unknown") +
                    " from " + finding.Fact.Source + "." +
                    (finding.Fact.Id == "backup.schedule" && backupCard is not null
                        ? " Proposed fix belongs to open backup card " + backupCard + "." : ""),
                    null, evidence, finding.Impact,
                    finding.Urgency, finding.Confidence, OpsWardenEvaluator.Fingerprint(finding.Fact),
                    Area: finding.Fact.Id.StartsWith("backup.", StringComparison.Ordinal)
                        ? "operations.backups" : "operations.security");
                var item = _items.Upsert(workspace.Id, activation.PersonaId, roleId, runKey, draft,
                    finding.Floor, now);
                await PublishAsync(item, runKey, ct);
                if (finding.Fact.Id != "backup.schedule" || finding.Fact.Value != "missing") continue;
                var decision = OpsWardenDecision.ForMissingSchedule(now);
                var request = _items.Upsert(workspace.Id, activation.PersonaId, roleId, runKey,
                    decision, 70, now);
                await PublishAsync(request, runKey, ct);
                if (backupCard is not null)
                {
                    var proposal = OpsWardenFixProposal.ForOpenBackupCard(backupCard);
                    if (!_items.List(workspace.Id).Any(existing => existing.PersonaId == activation.PersonaId
                        && existing.Fingerprint == proposal.FingerprintKey && existing.State == "draft"))
                    {
                        var fix = _items.Upsert(workspace.Id, activation.PersonaId, roleId, runKey,
                            proposal, 0, now);
                        await PublishAsync(fix, runKey, ct);
                    }
                }
            }
        }
    }

    public PersonaLeaseV1? Claim(string hostId, DateTime now)
    {
        if (_root is null) return null;
        lock (_gate)
        {
            var workspaces = _workspaces.List().Select(w => w.Id).ToArray();
            foreach (var workspace in workspaces)
            foreach (var receipt in _scheduling.Receipts(workspace).Where(r =>
                         r.State == PersonaOccurrenceStates.Admitted && r.HostId == hostId)
                         .OrderBy(r => r.CreatedAtUtc))
            {
                var activation = _activations.List(workspace).Personas.Select(p => p.Activation)
                    .FirstOrDefault(a => a?.PersonaId == receipt.PersonaId);
                if (activation is not { State: PersonaStates.Active or PersonaStates.Shadow }
                    || activation.DescriptorRevision != receipt.DescriptorRevision) continue;
                if (_cards.GetResidentIdentityFacts().Any(c => c.State == TaskStates.Ready
                    && (activation.Scope.Projects.Contains("*") ||
                        activation.Scope.Projects.Contains(c.ProjectName, StringComparer.OrdinalIgnoreCase))))
                    continue;
                var grounds = BuildGrounds(receipt, activation);
                var investigation = activation.Descriptor.Kind != "manager"
                    && receipt.TriggerRefs.Contains("investigation", StringComparer.Ordinal);
                if (investigation && activation.Attributes.Thoroughness < 4) continue;
                var json = JsonSerializer.Serialize(grounds, StrictJson);
                var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                var model = activation.Descriptor.Model.CapabilityClass == "frontier" ? "claude-opus-5" : "claude-sonnet-4-5";
                var claimed = _scheduling.Claim(workspace, receipt.Key, hostId, now, digest, model);
                if (claimed is null) continue;
                string sessionId;
                try
                {
                    sessionId = _security.MintPersonaOccurrenceSession(workspace, activation.PersonaId,
                        receipt.Key, activation.Allowance.MinutesPerSession).Session.Id;
                    _scheduling.BindSession(workspace, receipt.Key, claimed.ClaimToken!, sessionId);
                }
                catch (SecurityOperationException ex)
                {
                    _scheduling.Finish(workspace, receipt.Key, hostId, claimed.ClaimToken!, false,
                        ex.Code, 0, 0, null);
                    continue;
                }
                try { SaveGrounds(claimed.ClaimToken!, grounds); }
                catch (IOException)
                {
                    _scheduling.Finish(workspace, receipt.Key, hostId, claimed.ClaimToken!, false,
                        "grounds-persist-failed", 0, 0, null);
                    continue;
                }
                var prompt = "You are persona " + activation.PersonaId + " in role " +
                    (activation.RoleId ?? activation.Descriptor.Id) + ". Charter: " + activation.Descriptor.Charter +
                    "\nReturn one JSON object: {\"items\":[{\"kind\":\"finding|proposal|decision-request|report-line\",\"title\":\"...\",\"body\":\"...\",\"project\":null,\"area\":null,\"evidence\":[{\"ref\":\"case:id\",\"measured\":false}],\"impact\":1,\"urgency\":1,\"confidence\":0.5,\"fingerprintKey\":\"stable subject\",\"deadlineUtc\":null,\"draftCard\":false,\"options\":null,\"recommendedOption\":null,\"dossierKey\":null,\"dossierSection\":null,\"draftTitle\":null,\"draftPrompt\":null}]}. " +
                    "Title at most 120 characters; body at most 1200. Cite only exact source refs in these grounds. " +
                    "Use only in-scope project names. Decision requests need 2 to 4 options, one recommended option, " +
                    "a deadline and say that no answer changes nothing. Proposals need an in-scope home Dossier key from a dossier: ref and a section; " +
                    "card-shaped proposals also need draftTitle and draftPrompt. The platform computes importance.\n" + json;
                return new PersonaLeaseV1(receipt.Key, claimed.ClaimToken!, workspace, activation.PersonaId,
                    activation.RoleId ?? activation.Descriptor.Id, prompt, model,
                    activation.Descriptor.Model.Thinking, investigation ? 900 : 300,
                    12_000, 2_500, Investigation: investigation, SessionId: sessionId,
                    RepositoryPaths: investigation ? _projects.List().Where(p => p.WorkspaceId == workspace
                        && !p.Archived && p.RepositoryPath is not null
                        && (activation.Scope.Projects.Contains("*") ||
                            activation.Scope.Projects.Contains(p.Id) ||
                            activation.Scope.Projects.Contains(p.DisplayName, StringComparer.OrdinalIgnoreCase)))
                        .Select(p => p.RepositoryPath!).Take(4).ToArray() : []);
            }
            return null;
        }
    }

    public async Task<PersonaLeaseV1?> ClaimAsync(string hostId, DateTime now, CancellationToken ct)
    {
        var lease = Claim(hostId, now);
        if (lease is not null)
            try { await PublishLifecycleAsync(lease.PersonaId, lease.Key, "started", null, ct); }
            catch (IOException ex) { SilentCatch.Warn(ex, "persona lease lifecycle bus append failed"); }
        return lease;
    }

    public async Task<(bool Accepted, string? Error)> CompleteAsync(PersonaLeaseCompletion completion,
        DateTime now, CancellationToken ct)
    {
        if (_root is null) return (false, "workspace-unavailable");
        var receipt = _scheduling.Receipts(completion.WorkspaceId).FirstOrDefault(r => r.Key == completion.Key);
        if (receipt is null || receipt.State != PersonaOccurrenceStates.Claimed ||
            receipt.HostId != completion.RunnerId || receipt.ClaimToken != completion.ClaimToken)
            return (false, "stale-persona-lease");
        var activation = _activations.List(completion.WorkspaceId).Personas.Select(p => p.Activation)
            .FirstOrDefault(a => a?.PersonaId == receipt.PersonaId);
        var grounds = LoadGrounds(completion.ClaimToken);
        if (activation is null || grounds is null || receipt.GroundsDigest !=
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(grounds, StrictJson)))))
            return Fail("grounds-unavailable");
        if (activation.State is not (PersonaStates.Active or PersonaStates.Shadow) ||
            activation.ActivationRevision != grounds.ActivationRevision ||
            !_security.HasLivePersonaGrant(completion.WorkspaceId, activation.PersonaId))
            return Fail("persona-grant-or-activation-changed");
        if (receipt.ClaimedAtUtc is null ||
            receipt.ClaimedAtUtc.Value.AddMinutes(
                receipt.TriggerRefs.Contains("investigation", StringComparer.Ordinal) ? 15 : 5) < now)
            return Fail("persona-session-timeout");
        if (!completion.Success || completion.Response is null) return Fail(completion.Error ?? "model-call-failed");
        PersonaOccurrenceResponseV1 response;
        try { response = JsonSerializer.Deserialize<PersonaOccurrenceResponseV1>(completion.Response, StrictJson)!; }
        catch (JsonException) { return Fail("response-schema-invalid"); }
        if (response is null) return Fail("response-schema-invalid");
        var existing = _items.List(completion.WorkspaceId);
        var error = PersonaOutputValidator.Validate(response, grounds, activation, existing,
            _proposals.All(_root), _cards.GetResidentIdentityFacts(), now);
        if (error is not null) return Fail(error);
        var project = response.Items.FirstOrDefault(i => i.Project is not null)?.Project;
        var finished = _scheduling.Finish(completion.WorkspaceId, completion.Key, completion.RunnerId,
            completion.ClaimToken, true, null, completion.InputTokens, completion.OutputTokens,
            project, completion.Model, completion.CacheReadTokens, completion.CacheWriteTokens);
        if (finished is null) return (false, "stale-persona-lease");
        foreach (var draft in response.Items)
        {
            var floor = grounds.Sources.Where(s => draft.Evidence.Any(e => e.Ref == s.Ref))
                .Select(s => s.Floor).DefaultIfEmpty(0).Max();
            var item = _items.Upsert(completion.WorkspaceId, activation.PersonaId,
                activation.RoleId ?? activation.Descriptor.Id, completion.Key, draft, floor, now,
                grounds);
            await PublishAsync(item, completion.Key, ct, receipt.SessionId);
        }
        return (true, null);

        (bool, string?) Fail(string reason)
        {
            _scheduling.Finish(completion.WorkspaceId, completion.Key, completion.RunnerId,
                completion.ClaimToken, false, reason, completion.InputTokens, completion.OutputTokens,
                null, completion.Model, completion.CacheReadTokens, completion.CacheWriteTokens);
            return (false, reason);
        }
    }

    public async Task PublishRefusalsAsync(IReadOnlyList<PersonaOccurrenceReceipt> receipts,
        DateTime now, CancellationToken ct)
    {
        if (_root is null) return;
        foreach (var receipt in receipts.Where(r => r.State == PersonaOccurrenceStates.Waiting ||
                     r.State == PersonaOccurrenceStates.Failed && r.Reason == "runner-lease-unclaimed"))
        {
            var activation = _activations.List(receipt.WorkspaceId).Personas.Select(p => p.Activation)
                .FirstOrDefault(a => a?.PersonaId == receipt.PersonaId);
            if (activation is null) continue;
            var items = RefusalDrafts(receipt, activation.Descriptor)
                .Select(entry => _items.Upsert(receipt.WorkspaceId, receipt.PersonaId, receipt.RoleId,
                    receipt.Key, entry.Draft, entry.Floor, now)).ToArray();
            var report = _reports.SaveStub(receipt.WorkspaceId, receipt, activation.Descriptor,
                _items.List(receipt.WorkspaceId), _scheduling.Receipts(receipt.WorkspaceId), now);
            await PublishLifecycleAsync(receipt.PersonaId, receipt.Key, "refused", receipt.Reason, ct);
            foreach (var item in items) await PublishAsync(item, receipt.Key, ct);
            await PublishReportAsync(report, ct);
        }
    }

    public static IReadOnlyList<(PersonaItemDraftV1 Draft, int Floor)> RefusalDrafts(
        PersonaOccurrenceReceipt receipt, PersonaDescriptorV1 descriptor)
    {
        var rows = new List<(PersonaItemDraftV1, int)>
        {
            (new PersonaItemDraftV1(PersonaItemKinds.ReportLine, "Persona report unavailable",
                Truncate("No model call was made: " + receipt.Reason + ". Unread triggers: " +
                string.Join(", ", receipt.TriggerRefs.Take(12)) + ".", 1_200), null, [], 1, 1, 0,
                "stub:" + receipt.Key), 0),
        };
        var missingSchedule = receipt.TriggerRefs.Any(trigger =>
            trigger.StartsWith("backup:backup-not-configured", StringComparison.Ordinal) ||
            trigger.StartsWith("backup:backup-disabled", StringComparison.Ordinal));
        foreach (var trigger in receipt.TriggerRefs)
        {
            if (descriptor.Id == "operations-warden" && missingSchedule &&
                (trigger.StartsWith("backup:backup-missing", StringComparison.Ordinal) ||
                 trigger.StartsWith("backup:backup-stale", StringComparison.Ordinal))) continue;
            // The Watcher supplies typed fact floors and the descriptor can
            // raise them. Arbitrary model text never creates a fact floor.
            var fact = trigger.StartsWith("backup:backup-not-configured", StringComparison.Ordinal)
                || trigger.StartsWith("backup:backup-disabled", StringComparison.Ordinal)
                ? "backup.none-scheduled"
                : trigger.StartsWith("backup:backup-stale", StringComparison.Ordinal)
                    || trigger.StartsWith("backup:backup-missing", StringComparison.Ordinal)
                    ? "backup.last-good-age>48h"
                : trigger.StartsWith("audit:", StringComparison.Ordinal) ? "audit.chain-broken" : trigger;
            var floor = Math.Max(receipt.FactFloors.GetValueOrDefault(trigger),
                PersonaImportanceService.DescriptorFloor(descriptor, fact));
            if (floor < 70) continue;
            var schedule = descriptor.Id == "operations-warden" && fact == "backup.none-scheduled";
            rows.Add((new PersonaItemDraftV1(PersonaItemKinds.Finding,
                schedule ? "No scheduled backup on the networked installation" : Truncate("Persona fact: " + fact, 120),
                Truncate("Typed fact " + trigger + " remained active while the persona session was refused.", 1_200),
                null, schedule
                    ? [new PersonaEvidenceV1(trigger, true), new PersonaEvidenceV1("ops-fact:backup.schedule", true)]
                    : [new PersonaEvidenceV1(trigger, true)], schedule ? 5 : 1, schedule ? 5 : 1,
                schedule ? .94 : 0, schedule ? "ops:backup.schedule:installation" : "floor:" + fact), floor));
        }
        return rows;
    }

    private static string Truncate(string value, int max) => value[..Math.Min(value.Length, max)];

    public async Task RecomputeOpenAsync(string workspace, DateTime now, CancellationToken ct)
    {
        foreach (var item in _items.RecomputeOpen(workspace, now))
            await PublishAsync(item, "sweep", ct);
    }

    public async Task RecomputeAllOpenAsync(DateTime now, CancellationToken ct)
    {
        foreach (var workspace in _workspaces.List().Select(w => w.Id))
        {
            await RecomputeOpenAsync(workspace, now, ct);
            var chief = _activations.List(workspace).Personas.FirstOrDefault(p =>
                p.Activation is { State: PersonaStates.Active }
                && (p.Activation.RoleId ?? p.Activation.Descriptor.Id) == "chief-of-staff");
            if (chief is not null && _security.HasLivePersonaGrant(workspace, chief.Id)
                && _items.List(workspace).Any(i => i.Kind == PersonaItemKinds.DecisionRequest
                    && i.State == "open" && i.DeadlineUtc <= now))
            {
                var session = _security.MintPersonaOccurrenceSession(workspace, chief.Id,
                    "manager-decision-expiry:" + now.ToString("yyyyMMddHHmm"), 5);
                var grantId = _security.ListStandingGrants(session.Principal.Id)
                    .First(g => g.RevokedAt is null && g.ExpiresAt > now).Id;
                foreach (var item in _items.ExpireStaleDecisionRequests(workspace, now))
                {
                    await _managers.RecordExpiryAsync(workspace, chief.Id, item.Id,
                        session.Session.Id, grantId, now, ct);
                    await PublishAsync(item, "manager-sweep", ct);
                }
            }
        }
    }

    public async Task PublishCompletionLifecycleAsync(PersonaLeaseCompletion completion,
        string? error, CancellationToken ct)
    {
        if (error == "stale-persona-lease") return;
        await PublishLifecycleAsync(_scheduling.Receipts(completion.WorkspaceId)
            .FirstOrDefault(r => r.Key == completion.Key)?.PersonaId ?? "unknown",
            completion.Key, error is null ? "completed" : "failed", error, ct);
    }

    private async Task PublishLifecycleAsync(string personaId, string key,
        string state, string? reason, CancellationToken ct)
    {
        if (_root is null || personaId == "unknown") return;
        await _bus.RegisterParticipantAsync(_root, new AgentParticipant
        {
            Id = "persona:" + personaId, Kind = "Persona", DisplayName = personaId,
        }, ct);
        await _bus.AppendAsync(_root, new AgentMessage
        {
            Id = Guid.CreateVersion7().ToString("N"), CreatedAt = DateTime.UtcNow,
            ParticipantId = "persona:" + personaId, Role = "system", Kind = "lifecycle",
            Severity = state is "failed" or "refused" ? "Warn" : "Info",
            Summary = "Persona occurrence " + state, CorrelationId = key,
            Payload = JsonSerializer.SerializeToElement(new { state, reason, occurrenceKey = key }),
        }, ct);
    }

    private async Task PublishReportAsync(PersonaReportRecordV1 report, CancellationToken ct)
    {
        await _bus.AppendAsync(_root!, new AgentMessage
        {
            Id = Guid.CreateVersion7().ToString("N"), CreatedAt = DateTime.UtcNow,
            ParticipantId = "persona:" + report.PersonaId, Role = "actor", Kind = "advisory",
            Severity = report.OpenItems.Any(i => i.Importance >= 70) ? "High" : "Info",
            Summary = "Persona stub report", CorrelationId = report.OccurrenceKey,
            Payload = JsonSerializer.SerializeToElement(report, StrictJson),
        }, ct);
    }

    private PersonaGroundsV1 BuildGrounds(PersonaOccurrenceReceipt receipt, PersonaActivationV1 activation)
    {
        if (activation.Descriptor.Kind == "manager")
        {
            // Manager decisions use only validated item fields. Do not add card, Dossier,
            // watcher or prior-outcome summaries here: those can contain untrusted prose.
            return PersonaManagerPolicy.Grounds(receipt, activation,
                _items.List(receipt.WorkspaceId));
        }
        var allowed = _projects.List().Where(p => p.WorkspaceId == receipt.WorkspaceId && !p.Archived
            && (activation.Scope.Projects.Contains("*") || activation.Scope.Projects.Contains(p.Id)
                || activation.Scope.Projects.Contains(p.DisplayName, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var names = allowed.Select(p => p.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var focus = activation.Attributes.Focus ?? new Dictionary<string, int>();
        var sources = new List<PersonaGroundsSourceV1>();
        foreach (var fact in receipt.FactFloors)
            sources.Add(new(fact.Key, "typed-fact", null, 100,
                "Verified trigger " + fact.Key + " (importance floor " + fact.Value + ")", true,
                fact.Value));
        foreach (var project in allowed)
        {
            sources.Add(new("project:" + project.Id, "project", project.DisplayName, 0,
                project.DisplayName, false));
            if (project.RepositoryPath is not { } repository) continue;
            var docsRoot = Path.Combine(repository, "docs");
            if (!Directory.Exists(docsRoot)) continue;
            string[] folders;
            try { folders = Directory.EnumerateDirectories(docsRoot).Take(80).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
            {
                var manifest = Path.Combine(folder, "workbench.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                    var root = document.RootElement;
                    var key = root.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : key;
                    sources.Add(new("dossier:" + key, "dossier", project.DisplayName,
                        0, Truncate(title ?? key, 160), false));
                }
                catch (JsonException ex) { SilentCatch.Note(ex, "persona Dossier manifest is invalid"); }
                catch (IOException ex) { SilentCatch.Note(ex, "persona Dossier manifest could not be read"); }
                catch (UnauthorizedAccessException ex) { SilentCatch.Warn(ex, "persona Dossier manifest access denied"); }
            }
        }
        foreach (var item in _cases.All(_root!).Where(c => names.Contains(c.Project))
                     .OrderByDescending(c => focus.Where(pair => c.LastSummary.Contains(pair.Key,
                         StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value))
                     .ThenByDescending(c => c.LastSeenUtc).Take(60))
            sources.Add(new("case:" + item.Id, "watcher-case", item.Project,
                focus.Where(pair => item.LastSummary.Contains(pair.Key,
                    StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value),
                item.LastSummary[..Math.Min(280, item.LastSummary.Length)] +
                " (observed " + item.OccurrenceCount + " times)",
                item.OccurrenceCount > 0, ObservedAtUtc: item.LastSeenUtc,
                Fingerprint: item.Fingerprint));
        foreach (var card in _cards.GetResidentIdentityFacts().Where(c => names.Contains(c.ProjectName))
                     .Take(60))
            sources.Add(new("card:" + card.Id, "card", card.ProjectName, 0,
                (card.Title + " (" + card.State + ")")[..Math.Min(320, card.Title.Length + card.State.Length + 3)], false));
        foreach (var prior in _items.List(receipt.WorkspaceId).Where(i => i.PersonaId == receipt.PersonaId)
                     .OrderByDescending(i => i.UpdatedAtUtc).Take(40))
            sources.Add(new("item:" + prior.Id, "prior-outcome", prior.Project, 0,
                prior.Title + " (" + prior.State + ", importance " + prior.Importance + ")",
                false, ObservedAtUtc: prior.UpdatedAtUtc));
        // A dense JSON envelope can tokenize close to one token per character.
        // Reserve framing room and truncate by serialized UTF-8 bytes.
        var selected = new List<PersonaGroundsSourceV1>();
        foreach (var source in sources.OrderByDescending(s => s.FocusWeight))
        {
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(selected, StrictJson)) +
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(source, StrictJson)) > 18_000) break;
            selected.Add(source);
        }
        return new(1, receipt.Key, receipt.PersonaId, receipt.RoleId, receipt.WorkspaceId,
            receipt.DescriptorRevision, activation.DescriptorDigest, selected,
            receipt.TriggerRefs.Take(50).ToArray())
        {
            ActivationRevision = activation.ActivationRevision,
            OmittedSourceCount = Math.Max(0, sources.Count - selected.Count),
            AllowedProjects = allowed.Select(p => p.DisplayName).ToArray(),
            AllowedAreas = activation.Scope.Areas,
        };
    }

    private void SaveGrounds(string token, PersonaGroundsV1 grounds)
    {
        var path = Path.Combine(_root!, ".metadata", "persona-grounds", token + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(grounds, StrictJson));
    }

    private PersonaGroundsV1? LoadGrounds(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) return null;
        var path = Path.Combine(_root!, ".metadata", "persona-grounds", token + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<PersonaGroundsV1>(File.ReadAllText(path), StrictJson) : null;
    }

    private async Task PublishAsync(PersonaItemV1 item, string occurrenceKey, CancellationToken ct,
        string? sessionId = null)
    {
        await _bus.RegisterParticipantAsync(_root!, new AgentParticipant
        {
            Id = "persona:" + item.PersonaId, Kind = "Persona", DisplayName = item.PersonaId,
        }, ct);
        var message = new AgentMessage
        {
            Id = Guid.CreateVersion7().ToString("N"), CreatedAt = DateTime.UtcNow,
            ParticipantId = "persona:" + item.PersonaId, Role = "actor",
            Kind = item.Kind switch
            {
                PersonaItemKinds.Finding => "observation",
                PersonaItemKinds.DecisionRequest => "question",
                _ => "advisory",
            },
            Severity = item.Importance >= 70 ? "High" : item.Importance >= 40 ? "Warn" : "Info",
            Project = item.Project, Summary = item.Title,
            CorrelationId = occurrenceKey,
            CliSessionId = sessionId,
            Payload = JsonSerializer.SerializeToElement(item, StrictJson),
        };
        await _bus.AppendAsync(_root!, message, ct);
    }
}
