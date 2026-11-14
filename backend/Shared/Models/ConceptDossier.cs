namespace AgentStudio.Shared;

/// <summary>
/// Read-time concept-delivery projection. The visible path is the primary
/// <c>task.json.dossier</c> association shared with the board and Dossier cards.
/// </summary>
public sealed record ConceptDossierSummary
{
    public string? RepoRelativePath { get; init; }
    public string? ReferenceSource { get; init; }
    public bool NoDossierNeeded { get; init; }
    public string? NoDossierReason { get; init; }
    public DateTime? DeclaredAt { get; init; }
    public bool ContractSatisfied =>
        !string.IsNullOrWhiteSpace(RepoRelativePath) || NoDossierNeeded;
}

/// <summary>Operator correction for a concept card's dossier contract.</summary>
public sealed record SetConceptDossierRequest
{
    public string? Path { get; init; }
    public bool NoDossierNeeded { get; init; }
    public string? Reason { get; init; }
}
