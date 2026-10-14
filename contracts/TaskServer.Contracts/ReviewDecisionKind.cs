using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewDecisionKind
{
    Reissue,
    Escalate,
    AcceptAsDone,
    Skipped,
    /// <summary>
    /// Human-authored boundary that reopens a task from a decision lane. It is
    /// not an orchestrator verdict; it invalidates prior active-budget and
    /// stale-verdict state while retaining those rows as history.
    /// </summary>
    OperatorRequeue
}
