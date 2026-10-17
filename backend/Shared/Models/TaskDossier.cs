namespace AgentStudio.Shared;

/// <summary>The one Dossier that owns a card's implementation progress.</summary>
public sealed record TaskDossier
{
    public string Path { get; init; } = "";
    public string? Section { get; init; }
    public string? Decision { get; init; }
    public int? ImplementationIndex { get; init; }
    /// <summary>Deployment environments named by this implementation slice.</summary>
    public string[] Environments { get; init; } = [];
}
