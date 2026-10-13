namespace AgentStudio.Shared;

public enum ReviewTerminalOutcome
{
    InfrastructureFailure,
    ProductFailure,
    Inconclusive,
    Pass,
    Cancellation,
    Superseded,

    /// <summary>
    /// The review found no failure the delivery introduced, but at least one
    /// deterministic gate was already red on the merge base (AGT-2819). Distinct
    /// from <see cref="ProductFailure"/>, which charges the card, and from
    /// <see cref="Pass"/>, which would hide the branch defect. Admissible for
    /// integration: the card does not make the branch worse.
    /// </summary>
    IntegrationBranchDefect,
}

