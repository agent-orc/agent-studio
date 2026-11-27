using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>Restart-safe custody of one immutable delivery's stale-verdict recovery.</summary>
internal sealed record StaleVerdictRecovery(string Delivery, string Verdict, string Phase,
    StaleVerdictRecoveryFacts? DecisionFacts = null, string? Error = null, long LaneEnteredTicks = 0)
{
    private static string PathFor(TaskInfo card)
        => Path.Combine(TaskPaths.LogsDir(card.FolderPath), "stale-verdict-recovery.json");

    public static StaleVerdictRecovery? Read(TaskInfo card)
    {
        var path = PathFor(card);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StaleVerdictRecovery>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SilentCatch.Note(ex, "Stale-verdict recovery: reconstruct custody from the lane receipt and current immutable delivery");
            return null;
        }
    }

    public void Write(TaskInfo card)
        => new AtomicJsonFileWriter().Write(PathFor(card), JsonSerializer.Serialize(this));

    public string Failure(string detail)
        => $"Delivery {Delivery} needs a fresh review because verdict {Verdict} was superseded. "
            + $"Recovery stopped: {detail} Inspect the integration evidence and resolve the named failure, "
            + "then use Continue with repair instructions. Verified integration by another worker "
            + "will request a fresh review automatically.";
}
