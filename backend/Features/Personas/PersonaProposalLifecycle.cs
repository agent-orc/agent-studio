using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Watcher;

namespace AgentStudio.Personas;

/// <summary>Body for <c>POST /api/workspaces/{id}/personas/items/{itemId}/decision</c>.</summary>
public sealed record PersonaItemDecision(string Outcome, string? Reason = null, DateTime? ReviewAtUtc = null);

/// <summary>A decision the lifecycle refuses with <c>409</c>, for example an already-decided item.</summary>
public sealed class PersonaDecisionConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Adapter from persona items to the scout proposal lifecycle (persona team
/// PT-6). The scout has no code of its own yet, so its lifecycle is the parts
/// it is built from: accept promotes through the <c>promote-concept</c> plan
/// and spawn ledger of the proposal's concept Dossier, reject writes the
/// Watcher fingerprint suppression, and defer records a review date and keeps
/// the proposal decidable. Findings and decision requests take only
/// <c>resolved</c>. <see cref="PersonaItemStore"/> keeps the feed record.
/// </summary>
public sealed class PersonaProposalLifecycle
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Deferred = "deferred";
    public const string Resolved = "resolved";
    private static readonly TimeSpan DefaultDeferral = TimeSpan.FromDays(7);

    private readonly PersonaItemStore _items;
    private readonly WatcherSuppressionStore _suppressions;
    private readonly TaskScannerService _scanner;
    private readonly ConceptPromotionService _promotion;
    private readonly IConfiguration _configuration;

    public PersonaProposalLifecycle(PersonaItemStore items, WatcherSuppressionStore suppressions,
        TaskScannerService scanner, ConceptPromotionService promotion, IConfiguration configuration)
    {
        _items = items;
        _suppressions = suppressions;
        _scanner = scanner;
        _promotion = promotion;
        _configuration = configuration;
    }

    public PersonaItemV1 Decide(string workspaceId, string itemId, PersonaItemDecision request, DateTime now)
    {
        var item = _items.Find(workspaceId, itemId) ?? throw new KeyNotFoundException("Persona item not found.");
        var outcome = request.Outcome?.Trim().ToLowerInvariant() ?? "";
        if (item.Kind == PersonaItemKinds.Proposal
                ? outcome is not (Accepted or Rejected or Deferred)
                : outcome != Resolved)
            throw new ArgumentException(item.Kind == PersonaItemKinds.Proposal
                ? "A proposal takes accepted, rejected or deferred."
                : "This item takes resolved.");
        if (!PersonaItemStates.IsDecidable(item.State))
            throw new PersonaDecisionConflictException("persona-item-already-decided", "The persona item is already decided.");

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "owner-decision" : request.Reason.Trim();
        DateTime? deferredUntil = null;
        string? promoted = null;
        switch (outcome)
        {
            case Accepted:
                promoted = Promote(item);
                break;
            case Rejected:
                var root = _configuration["TaskRepository"];
                if (!string.IsNullOrWhiteSpace(root))
                    _suppressions.Suppress(root, item.Fingerprint, reason,
                        TimeSpan.FromDays(WatcherOptions.FromConfiguration(_configuration).SuppressionDays), now);
                break;
            case Deferred:
                deferredUntil = request.ReviewAtUtc ?? now + DefaultDeferral;
                if (deferredUntil <= now) throw new ArgumentException("The review date must be in the future.");
                break;
        }
        return _items.RecordDecision(workspaceId, itemId, outcome, reason, now, deferredUntil, promoted)
            ?? throw new PersonaDecisionConflictException("persona-item-already-decided", "The persona item is already decided.");
    }

    private string Promote(PersonaItemV1 item)
    {
        var source = string.IsNullOrWhiteSpace(item.DossierKey) ? null : _scanner.FindJob(item.DossierKey);
        if (source is null || !TaskModes.IsConcept(source.Mode))
            throw new PersonaDecisionConflictException("proposal-dossier-unavailable",
                "The proposal's concept Dossier card was not found.");
        if (source.State is not (TaskStates.HumanReview or TaskStates.Completed))
            throw new PersonaDecisionConflictException("proposal-dossier-not-reviewed",
                "Complete sight review of the concept before accepting proposals into it.");
        var plan = _scanner.BuildPromoteConceptPlan(source.Id, source.WatchPath)
            ?? throw new PersonaDecisionConflictException("proposal-dossier-not-reviewed",
                "The concept Dossier is missing or did not pass concept review.");
        var origin = item.History.FirstOrDefault()?.OccurrenceKey ?? "";
        var created = _promotion.PromoteProposal(source, plan, new ConceptImplementationTask
        {
            Id = item.Id,
            Title = (item.DraftTitle ?? item.Title).Trim(),
            Section = item.DossierSection,
            PromptMarkdown = $"""
                {(item.DraftPrompt ?? item.Body).Trim()}

                Provenance: persona `{item.PersonaId}` (role `{item.RoleId}`), item `{item.Id}`, occurrence `{origin}`, Dossier `{item.DossierKey}` {item.DossierSection}.
                """,
        }, "persona-proposal:" + item.Id);
        return created.TaskKey ?? created.JobId;
    }
}
