using System.Globalization;
using System.Text;
using AgentStudio.Docs;
using AgentStudio.Prompts;

namespace AgentStudio.Orchestrator;

/// <summary>
/// Resolves the active Dossier (workbench) through the existing catalogue
/// reader and renders a size-limited prompt block for one Dossier-scoped
/// orchestrator chat turn (AGT-2725). Mirrors
/// <see cref="OrchestratorTaskPromptContextComposer"/>'s bounded-block
/// convention; unlike a task, a Dossier has no board/lane state to fold in,
/// so this composer is the entire implicit context for a workbench-scoped
/// turn.
/// </summary>
public sealed class OrchestratorWorkbenchPromptContextComposer
{
    internal const int MetadataTokenLimit = 256;
    internal const int EntryHtmlTokenLimit = 1_800;
    internal const int CardTokenLimit = 700;
    private const int EstimatedCharactersPerToken = 4;

    private readonly WorkbenchCatalogueService _workbenches;
    private readonly DossierCardsService? _cards;

    public OrchestratorWorkbenchPromptContextComposer(
        WorkbenchCatalogueService workbenches, DossierCardsService? cards = null)
    {
        _workbenches = workbenches;
        _cards = cards;
    }

    /// <summary>
    /// Returns null when <paramref name="workbenchKey"/> is missing or does
    /// not resolve to a known Dossier. Callers that require a workbench scope
    /// to always resolve should surface that as an unavailable context block
    /// rather than throwing, the same convention the task composer uses.
    /// </summary>
    public OrchestratorWorkbenchPromptContext? Compose(string projectName, string? workbenchKey)
    {
        if (string.IsNullOrWhiteSpace(workbenchKey)) return null;
        var catalogue = _workbenches.List(projectName, includeHistory: true);
        var item = catalogue?.Items.FirstOrDefault(candidate =>
            candidate.Valid && string.Equals(candidate.Key, workbenchKey, StringComparison.OrdinalIgnoreCase));
        if (item == null) return null;

        var included = new List<string> { "dossier metadata" };
        var blocks = new List<string>
        {
            RenderBoundedBlock("DOSSIER METADATA", RenderMetadata(item), MetadataTokenLimit),
        };

        var document = _workbenches.Read(projectName, item.Id);
        if (document != null && !string.IsNullOrWhiteSpace(document.Html))
        {
            included.Add("index.html excerpt");
            blocks.Add(RenderBoundedBlock("index.html EXCERPT", document.Html, EntryHtmlTokenLimit));
        }

        if (_cards != null)
        {
            var cardList = _cards.List(projectName, item);
            included.Add("Dossier cards");
            blocks.Add(RenderCardBlock(cardList));
        }

        var sb = new StringBuilder();
        sb.AppendLf("=== ACTIVE DOSSIER CONTEXT ===");
        sb.AppendLf("The operator is chatting from this Dossier. Treat this block as the authoritative scope for the current message. Use the included card states and open decisions to answer what remains.");
        sb.AppendLf($"Included context blocks: {string.Join(", ", included)}.");
        sb.AppendLf();
        foreach (var block in blocks)
        {
            sb.AppendLf(block);
            sb.AppendLf();
        }

        var repositoryRoot = _workbenches.ResolveCanonicalForMutation(projectName, item.Id)?.Root;
        return new OrchestratorWorkbenchPromptContext(
            item.Key ?? workbenchKey, sb.ToString().TrimEnd(), included, repositoryRoot);
    }

    private static string RenderMetadata(WorkbenchListItem item)
    {
        var lines = new List<string>
        {
            $"Key: {item.Key ?? item.Id}",
            $"Title: {item.Title}",
            $"Summary: {item.Summary}",
            $"Status: {item.Status}",
            $"Phase: {item.Phase ?? "(none)"}",
        };
        if (!string.IsNullOrWhiteSpace(item.LifecycleState))
            lines.Add($"Lifecycle state: {item.LifecycleState}");
        lines.Add(item.OpenDecisionCount > 0
            ? $"Pending decision: {item.OpenDecisionCount} open decision point(s)"
              + (string.IsNullOrWhiteSpace(item.DecisionStage) ? "" : $" (stage: {item.DecisionStage})")
            : "Pending decision: none open");
        if (item.Decision is { } decision)
            lines.Add($"Last decision: {decision.Outcome} ({decision.State}) at {decision.DecidedAt ?? decision.PreparedAt}");
        return string.Join("\n", lines);
    }

    internal static string RenderCardBlock(DossierCardList cardList)
    {
        var lines = new List<string>
        {
            $"Implementation slices: {cardList.CompletedSlices}/{cardList.ImplementationSlices} complete.",
        };
        lines.AddRange(cardList.Cards.Select(card =>
            $"- {card.Key}: {card.Title} | lane {card.Lane} | state {card.State}"
            + (card.Environments.Length == 0 ? "" : $" | environments {string.Join(", ", card.Environments)}")
            + (card.Section is null ? "" : $" | section {card.Section}")
            + (card.Decision is null ? "" : $" | decision {card.Decision}")));
        return RenderBoundedBlock("DOSSIER CARDS", string.Join("\n", lines), CardTokenLimit);
    }

    internal static string RenderBoundedBlock(string label, string content, int tokenLimit)
    {
        var normalized = content.Trim();
        var characterLimit = tokenLimit * EstimatedCharactersPerToken;
        var truncated = normalized.Length > characterLimit;
        var bounded = truncated
            ? normalized[..Math.Max(0, characterLimit - 20)].TrimEnd() + "\n[content truncated]"
            : normalized;
        return $"--- {label} (limit: {tokenLimit.ToString(CultureInfo.InvariantCulture)} estimated tokens; truncated: {(truncated ? "yes" : "no")}) ---\n{bounded}\n--- END {label} ---";
    }
}

public sealed record OrchestratorWorkbenchPromptContext(
    string WorkbenchKey,
    string PromptBlock,
    IReadOnlyList<string> IncludedBlocks,
    string? RepositoryRoot);
