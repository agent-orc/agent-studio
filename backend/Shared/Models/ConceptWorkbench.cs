namespace AgentStudio.Shared;

/// <summary>
/// Presentation patterns supported by article-style documents. Descriptor
/// readers deliberately fail open to <see cref="Concept"/> so older files and
/// future values remain readable while the two current variants stay explicit.
/// </summary>
public static class ArticlePatterns
{
    public const string Ui = "ui";
    public const string Concept = "concept";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Ui, StringComparison.OrdinalIgnoreCase)
            ? Ui
            : Concept;
}

/// <summary>One implementation card proposed by a concept Workbench.</summary>
public sealed record ConceptImplementationTask
{
    public string Title { get; init; } = "";
    public string PromptMarkdown { get; init; } = "";
    /// <summary>
    /// Optional explicit one-slice boundary. Promotion normalizes every Dossier
    /// item to a bounded acceptance scope; older descriptors without this field
    /// use the item title and prompt as their one-card boundary.
    /// </summary>
    public TaskAcceptanceScope? AcceptanceScope { get; init; }
}

/// <summary>
/// The machine-readable descriptor beside a concept Workbench's
/// <c>index.html</c>. It extends the existing Workbench descriptor additively:
/// older Workbenches without <see cref="ImplementationTasks"/> remain readable.
/// </summary>
public sealed record ConceptWorkbenchDescriptor
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Entrypoint { get; init; } = "index.html";
    public string Status { get; init; } = "decision-pending";
    public string Phase { get; init; } = "shaping";
    public string Pattern { get; init; } = ArticlePatterns.Concept;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// AGT-2803: area and facet tag ids from the project's tag vocabulary.
    /// Part of the descriptor schema, so a scaffolded Dossier already carries
    /// the field and the catalogue projection can surface it.
    /// </summary>
    public List<string> Tags { get; init; } = [];
    public List<string> SourceTaskKeys { get; init; } = [];
    public List<ConceptImplementationTask> ImplementationTasks { get; init; } = [];
}

/// <summary>Durable source-document reference returned by concept promotion.</summary>
public sealed record ConceptSourceDocument
{
    public string RepoRelativePath { get; init; } = "";
    public string Title { get; init; } = "";
    public List<ConceptDecisionAssumption> Decisions { get; init; } = [];
}

public sealed record ConceptDecisionAssumption
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string OptionId { get; init; } = "";
    public string OptionLabel { get; init; } = "";
    public bool OperatorSelected { get; init; }
    public bool IsWorkingAssumption { get; init; }
}

public sealed record PromoteConceptResponse
{
    public ConceptSourceDocument Source { get; init; } = new();
    public List<ConceptImplementationTask> Items { get; init; } = [];
    public string Mode { get; init; } = TaskModes.Coding;
    public string TargetState { get; init; } = TaskStates.Preparation;
    public string WatchPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
}

public sealed record PromoteConceptRequest
{
    /// <summary>
    /// Zero-based descriptor items to create. Null or empty creates every
    /// proposed implementation item.
    /// </summary>
    public List<int>? ItemIndexes { get; init; }
}

public sealed record PromotedConceptTask
{
    public string JobId { get; init; } = "";
    public string? TaskKey { get; init; }
    public string Title { get; init; } = "";
}

public sealed record PromoteConceptTasksResponse
{
    public ConceptSourceDocument Source { get; init; } = new();
    public List<PromotedConceptTask> Created { get; init; } = [];
}
