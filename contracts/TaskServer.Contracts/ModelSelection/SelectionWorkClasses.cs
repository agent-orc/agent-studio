namespace AgentStudio.TaskServer.Contracts;

/// <summary>Work duration classes, independent of the selected provider and model.</summary>
public static class SelectionWorkClasses
{
    public const string Coding = "coding";
    public const string Review = "review-aspect";
    public const string Grade = "grade";
    public const string Summary = "summary";
    public const string Enrichment = "enrichment";
    public const string Conflict = "conflict-resolution";
    public const string Concept = "concept";
    public const string Decision = "decision";
    public const string Analysis = "analysis";
    public const string Deterministic = "deterministic";

    public static bool UsesReserve(string? workClass) => workClass is
        Review or Grade or Summary or Enrichment or Decision;

    public static string ForConsumer(string consumer) => consumer switch
    {
        SelectionConsumers.Coding => Coding,
        SelectionConsumers.ReviewAspect or SelectionConsumers.ConceptFit => Review,
        SelectionConsumers.ReviewDeep => Analysis,
        SelectionConsumers.ReviewGrade => Grade,
        SelectionConsumers.MergeResolver => Conflict,
        _ => Enrichment,
    };
}
