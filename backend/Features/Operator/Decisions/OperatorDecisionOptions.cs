namespace AgentStudio.Operator;

/// <summary>
/// Settings of the operator decision stream (<c>OperatorDecisions:*</c>).
/// The sweep interval is capped at 5 seconds so an event follows its fact
/// within 10 seconds; the retention window never drops below 24 hours.
/// </summary>
public sealed record OperatorDecisionOptions(
    TimeSpan SweepInterval,
    TimeSpan Retention,
    TimeSpan WatcherLeaseTimeout,
    TimeSpan StreamKeepAlive)
{
    public const int MaxSweepSeconds = 5;

    public static OperatorDecisionOptions FromConfiguration(IConfiguration configuration) => new(
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("OperatorDecisions:SweepSeconds", MaxSweepSeconds), 1, MaxSweepSeconds)),
        TimeSpan.FromHours(Math.Clamp(configuration.GetValue("OperatorDecisions:RetentionHours", 72), 24, 24 * 30)),
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("OperatorDecisions:WatcherLeaseSeconds", 90), 15, 3600)),
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("OperatorDecisions:KeepAliveSeconds", 15), 1, 300)));

    /// <summary>The folder of the decision log and watcher lease: <c>&lt;TaskRepository&gt;/.operator</c>.</summary>
    public static string Folder(IConfiguration configuration) => Path.Combine(
        string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            ? Path.Combine(AppContext.BaseDirectory, "workspace")
            : configuration["TaskRepository"]!, ".operator");
}
