using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tags;

public sealed record TagMaintenanceItem(string Project, string Kind, string Id, string Title,
    string[] Tags, string Text, bool Active, bool TerminologySource = false);
public sealed record TagMaintenanceSnapshot(List<TagRegistryEntry> Registry, List<string> AreaIds,
    Dictionary<string, List<GlossaryTerm>> Glossaries, List<TagMaintenanceItem> Items);
public sealed record TagMaintenanceProposal
{
    public string Kind { get; init; } = "";
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public string Label { get; init; } = "";
    public string Reason { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public string Area { get; init; } = "";
    public List<GlossaryTerm> Terms { get; init; } = [];
}
public sealed record TagMaintenanceChange(string Kind, string Project, string Id, string Before, string After);
public sealed record TagMaintenanceDecision
{
    public string Id { get; init; } = "";
    public string CardId { get; set; } = "";
    public TagMaintenanceProposal Proposal { get; init; } = new();
    public List<TagMaintenanceChange> Changes { get; init; } = [];
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
}
public sealed record TagMaintenanceRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; }
    public string Status { get; set; } = "running";
    public string Model { get; set; } = "";
    public string ThinkingLevel { get; init; } = "high";
    public int ItemsReviewed { get; set; }
    public int EligibleItems { get; set; }
    public int TextOffset { get; set; }
    public int ExcerptLimit { get; init; } = 4000;
    public Dictionary<string, int> GlobalUsage { get; set; } = [];
    public string? Error { get; set; }
    public List<string> Decisions { get; init; } = [];
    public TagGoldenSetReport GoldenSet { get; set; } = new();
}
public sealed record TagMaintenanceAudit(DateTimeOffset At, string DecisionId, string Actor,
    string Outcome, string Detail);
public sealed record TagMaintenanceState
{
    public List<TagMaintenanceRun> Runs { get; init; } = [];
    public List<TagMaintenanceDecision> Decisions { get; init; } = [];
    public List<TagMaintenanceAudit> Audit { get; init; } = [];
}

/// <summary>Pure proposal validation and the exact writes shown to the operator.</summary>
public static class TagMaintenancePolicy
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static string Fingerprint(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static bool Due(TagMaintenanceState state, DateTimeOffset now, TimeSpan interval,
        TimeSpan retryDelay)
    {
        var successful = state.Runs.Where(run => run.Status == "reported")
            .OrderByDescending(run => run.StartedAt).FirstOrDefault();
        var failedSinceSuccess = state.Runs.Where(run => run.Status == "failed"
                && (successful == null || run.StartedAt > successful.StartedAt))
            .OrderByDescending(run => run.StartedAt).FirstOrDefault();
        if (failedSinceSuccess != null) return now - failedSinceSuccess.StartedAt >= retryDelay;
        return successful == null || now - successful.StartedAt >= interval;
    }

    public static List<TagMaintenanceChange> Plan(string project, TagMaintenanceSnapshot snapshot,
        TagMaintenanceProposal proposal)
    {
        if (proposal.Kind is not ("retire" or "merge" or "add" or "glossary"))
            throw new ArgumentException("Unknown proposal kind.");
        if (string.IsNullOrWhiteSpace(proposal.Reason) || proposal.Evidence.Count == 0)
            throw new ArgumentException("A proposal needs a reason and source evidence.");
        if (!snapshot.Glossaries.TryGetValue(proposal.Area, out var terms))
            throw new ArgumentException("A proposal must name an existing area glossary.");
        var evidence = snapshot.Items.Where(item => item.Project == project
            && proposal.Evidence.Contains(item.Kind + ":" + item.Id)).ToArray();
        if (proposal.Evidence.Any(reference => !evidence.Any(item => item.Kind + ":" + item.Id == reference)
                && !snapshot.Registry.Any(tag => "registry:" + tag.Id == reference)))
            throw new ArgumentException("Proposal evidence must identify an inventoried source.");
        if (proposal.Kind == "add" && evidence.Count(item => item.Active) < 3)
            throw new ArgumentException("A new tag needs at least three distinct active source items.");
        if (proposal.Kind == "glossary" && !evidence.Any(item => item.TerminologySource && item.Active))
            throw new ArgumentException("Glossary proposals need a Dossier decision or ADR source.");
        if (terms.Any(term => !proposal.Terms.Any(next => next.Term == term.Term)
                && !string.Equals(term.Term, proposal.Source, StringComparison.OrdinalIgnoreCase)
                && !snapshot.Registry.Any(tag => tag.Id == proposal.Source
                    && string.Equals(term.Term, tag.Label, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("A proposal cannot remove unrelated glossary terms.");
        var validation = AreaGlossaryDocument.ValidateTerms(proposal.Terms);
        if (validation != null) throw new ArgumentException(validation);
        var normalizedTerms = AreaGlossaryDocument.Parse(AreaGlossaryDocument.Render(
            new AreaDefinition { Id = proposal.Area, Label = proposal.Area }, proposal.Terms));
        var result = new List<TagMaintenanceChange>();
        var source = snapshot.Registry.SingleOrDefault(tag => tag.Id == proposal.Source);
        if (proposal.Kind is "retire" or "merge")
        {
            if (source == null || snapshot.AreaIds.Contains(proposal.Source)
                || proposal.Source is "orchestrator-moved" or "outcome-silent-finish")
                throw new ArgumentException("Area ids and platform provenance tags cannot be retired or merged.");
            var uses = snapshot.Items.Where(item => item.Tags.Contains(proposal.Source)).ToList();
            if (proposal.Kind == "retire" && uses.Count != 0)
                throw new ArgumentException("Retirement requires zero uses, including history and other projects.");
            if (proposal.Kind == "merge")
            {
                if (proposal.Source == proposal.Target || !snapshot.Registry.Any(tag => tag.Id == proposal.Target))
                    throw new ArgumentException("A merge needs a different existing target.");
                // The workspace registry is shared. A project decision cannot approve writes elsewhere.
                if (uses.Any(item => item.Project != project))
                    throw new ArgumentException("Merge affects another project; a project-scoped decision cannot apply it.");
                result.AddRange(uses.Select(item => new TagMaintenanceChange(item.Kind, item.Project, item.Id,
                    Encode(item.Tags), Encode(Rewrite(item.Tags, proposal.Source, proposal.Target)))));
            }
        }
        if (proposal.Kind == "add")
        {
            if (!AreaTaxonomy.IsValidId(proposal.Target) || proposal.Target.Length > 32
                || snapshot.Registry.Any(tag => tag.Id == proposal.Target)
                || snapshot.AreaIds.Contains(proposal.Target) || string.IsNullOrWhiteSpace(proposal.Label))
                throw new ArgumentException("A new facet needs a unique valid id and label.");
            result.Add(new("registry", project, proposal.Target, "null", Encode(new TagRegistryEntry
            {
                Id = proposal.Target, Label = proposal.Label.Trim(), Description = proposal.Reason.Trim(), Kind = TagKinds.Facet,
            })));
        }
        // Glossary lists are complete replacements, displayed verbatim in the decision card.
        if (Encode(terms) != Encode(normalizedTerms))
            result.Add(new("glossary", project, proposal.Area, Encode(terms), Encode(normalizedTerms)));
        if (proposal.Kind is "retire" or "merge")
            result.Add(new("registry", project, proposal.Source, Encode(source), "null"));
        if (result.Count == 0) throw new ArgumentException("The proposal has no changes.");
        return result;
    }

    public static string[] Rewrite(IEnumerable<string> tags, string source, string target) =>
        tags.Select(tag => tag == source ? target : tag).Distinct(StringComparer.Ordinal).ToArray();

    public static string Card(string project, TagMaintenanceDecision decision) => $"""
        # Tag maintenance decision: {decision.Proposal.Kind}

        Project: {project}. Decision: {decision.Id}.
        {decision.Proposal.Reason}

        Evidence: {string.Join(", ", decision.Proposal.Evidence)}

        ## Options and consequences

        - Keep: reject this proposal. Registry, tags and glossary remain as they are.
        - Apply: explicitly approve the exact changes below. Facet registry changes are workspace-wide;
          tag and glossary rewrites are limited to this project. Saved filters using a removed id need updating.
          Area ids and platform provenance tags are protected. Stale data blocks application.

        This is a prose decision card pending W54. Moving or running this card does not approve it.
        Submit POST /api/projects/{Uri.EscapeDataString(project)}/tag-maintenance/decisions/{decision.Id}
        with JSON {Encode(new { option = "apply" })} or {Encode(new { option = "keep" })} and the operator's X-Client-Id.
        Only submit apply after an explicit operator choice. The response and maintenance report contain the audit.

        ## Exact changes (before and after)

        ```json
        {JsonSerializer.Serialize(decision.Changes, new JsonSerializerOptions(Json) { WriteIndented = true })}
        ```
        """;
}
