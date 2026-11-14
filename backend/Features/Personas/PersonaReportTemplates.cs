namespace AgentStudio.Personas;

/// <summary>Fixed report obligations for the starter roles. Anchors name the facts a report must cover, not model scores.</summary>
public sealed record PersonaReportTemplateV1(string Id, IReadOnlyList<string> Anchors,
    IReadOnlyList<string> SourceKinds);

public static class PersonaReportTemplates
{
    private static readonly IReadOnlyDictionary<string, PersonaReportTemplateV1> Templates =
        new Dictionary<string, PersonaReportTemplateV1>(StringComparer.Ordinal)
        {
            ["security-v1"] = new("security-v1", ["code", "dependencies", "permissions", "prompts"], ["watcher-case", "card", "dossier"]),
            ["reliability-v1"] = new("reliability-v1", ["runs", "batches", "latency", "hosts"], ["watcher-case", "card"]),
            ["quality-v1"] = new("quality-v1", ["review", "tests", "evidence"], ["watcher-case", "card"]),
            ["design-v1"] = new("design-v1", ["hard-rules", "themes", "consistency"], ["watcher-case", "card"]),
            ["product-v1"] = new("product-v1", ["decisions", "roadmap", "backlog"], ["dossier", "card"]),
            ["cost-v1"] = new("cost-v1", ["quota", "cost-per-card", "personas"], ["workspace-quota", "card-cost", "persona-budget"]),
            ["docs-v1"] = new("docs-v1", ["drift", "links", "index"], ["dossier", "card"]),
            ["deck-v1"] = new("deck-v1", ["claims", "numbers", "coverage"], ["magazine-issue"]),
            ["improvement-v1"] = new("improvement-v1", ["delivery-integrity", "review-quality", "capacity-economics", "credentials-operations"], ["watcher-case", "dossier", "card"]),
        };

    public static PersonaReportTemplateV1? Find(string id) => Templates.GetValueOrDefault(id);
}
