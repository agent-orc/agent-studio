namespace AgentStudio.Security;

/// <summary>ES-D6 A, ES-D9 A and ES-D16 A. Adding authority requires a code catalogue change (T2).</summary>
public static class ElevatedActionClasses
{
    public const string DirectAction = "direct-action";
    public const string Batch = "batch";
    public sealed record ActionClass(string Id, string Title, string Mode, IReadOnlyList<string> Operations,
        int RatePerHour, bool PerProject = false);

    public static readonly IReadOnlyList<ActionClass> All =
    [
        new("cards.accept-override", "Accept with override", Batch, [OperatorOperationIds.CardAcceptOverride], 30),
        new("cards.archive-override", "Archive with override", Batch, [OperatorOperationIds.CardArchiveOverride], 10),
        new("project.testing", "Set the testing strategy", DirectAction, [OperatorOperationIds.ProjectTestingSet], 6, true),
    ];
    public static ActionClass? Find(string? id) => All.FirstOrDefault(candidate => candidate.Id == id);
}

public sealed record ElevationUse(DateTime At, string? Project);
public sealed record ElevationClass(string Id, string ConfirmedBy, string? ArmingId, DateTime? ArmedAt, DateTime? ArmedUntil)
{
    public List<ElevationUse> Uses { get; init; } = [];
    public int RatePerHour { get; init; }
    public string? Mode { get; init; }
}
public sealed record OperatorElevation(List<ElevationClass> Classes)
{
    public string? ConfirmedBy { get; init; }
    public List<DateTime> RateRefusals { get; init; } = [];
    public static OperatorElevation? Confirm(IEnumerable<string> classes, string owner, DateTime now, DateTime sessionEnd)
    {
        var confirmed = classes.Select(id => Arm(id, owner, now, sessionEnd)).ToList();
        return confirmed.Count == 0 ? null : new(confirmed) { ConfirmedBy = owner };
    }
    public static ElevationClass Arm(string id, string owner, DateTime now, DateTime end)
    {
        var catalog = ElevatedActionClasses.Find(id) ?? throw new InvalidOperationException("Unknown elevated class.");
        return new(id, owner, catalog.Mode == ElevatedActionClasses.DirectAction ? Guid.NewGuid().ToString("N") : null,
            catalog.Mode == ElevatedActionClasses.DirectAction ? now : null,
            catalog.Mode == ElevatedActionClasses.DirectAction ? (now.AddHours(24) < end ? now.AddHours(24) : end) : null);
    }
    public OperatorElevation View(DateTime now) => this with
    {
        Classes = Classes.Select(entry => entry with
        {
            Uses = entry.Uses.Where(use => use.At > now.AddHours(-1) && use.At <= now).ToList(),
            RatePerHour = ElevatedActionClasses.Find(entry.Id)?.RatePerHour ?? 0,
            Mode = ElevatedActionClasses.Find(entry.Id)?.Mode,
        }).ToList(),
    };
}
public sealed record OperatorRearmRequest(string ClassId);

/// <summary>No class, grant, approval or break-glass removes these operator refusals.</summary>
public static class OperatorHardRefusals
{
    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
    {
        OperatorOperationIds.AccessChange, OperatorOperationIds.GrantChange, OperatorOperationIds.CatalogueChange,
        OperatorOperationIds.SecretsChange, OperatorOperationIds.KeysRotate, OperatorOperationIds.ProductionPromote,
    };
}
