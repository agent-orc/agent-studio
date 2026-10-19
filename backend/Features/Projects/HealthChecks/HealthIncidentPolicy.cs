namespace AgentStudio.Projects;

public enum HealthIncidentAction
{
    None,
    Open,
    Continue,
    Close,
}

/// <summary>Consecutive-result counters and whether an incident is open for one check.</summary>
public sealed record HealthCheckStreak(int ConsecutiveFailures, int ConsecutiveSuccesses, bool IncidentOpen)
{
    public static readonly HealthCheckStreak Initial = new(0, 0, false);
}

public sealed record HealthIncidentStep(HealthCheckStreak Streak, HealthIncidentAction Action);

/// <summary>
/// Pure incident rule (IP-14). An incident opens after <c>failureThreshold</c> consecutive
/// failures and closes after <c>recoveryThreshold</c> consecutive successes. While open, more
/// failures continue the same incident, so a flapping target never opens a second one.
/// </summary>
public static class HealthIncidentPolicy
{
    public static HealthIncidentStep Next(HealthCheckStreak current, bool success, int failureThreshold,
        int recoveryThreshold)
    {
        if (success)
        {
            var successes = current.ConsecutiveSuccesses + 1;
            if (current.IncidentOpen && successes >= Math.Max(1, recoveryThreshold))
                return new(new(0, successes, false), HealthIncidentAction.Close);
            return new(current with { ConsecutiveFailures = 0, ConsecutiveSuccesses = successes },
                HealthIncidentAction.None);
        }
        var failures = current.ConsecutiveFailures + 1;
        if (current.IncidentOpen)
            return new(new(failures, 0, true), HealthIncidentAction.Continue);
        return failures >= Math.Max(1, failureThreshold)
            ? new(new(failures, 0, true), HealthIncidentAction.Open)
            : new(new(failures, 0, false), HealthIncidentAction.None);
    }
}

public enum EnvironmentIncidentAction
{
    None,
    Open,
    Close,
}

/// <summary>
/// Pure grouping rule: an environment incident is open while at least one of its checks has an
/// open incident, so the checks of one down site share one incident, one alert and one card.
/// </summary>
public static class EnvironmentIncidentPolicy
{
    public static EnvironmentIncidentAction Next(bool incidentOpen, int openCheckIncidents)
        => (incidentOpen, openCheckIncidents > 0) switch
        {
            (false, true) => EnvironmentIncidentAction.Open,
            (true, false) => EnvironmentIncidentAction.Close,
            _ => EnvironmentIncidentAction.None,
        };
}

/// <summary>
/// Pure certificate expiry rule: warn once per certificate when it is within 14 days and
/// once more within 3 days. A certificate first seen inside 3 days warns once, at 3.
/// </summary>
public static class CertificateExpiryPolicy
{
    public static readonly IReadOnlyList<int> WarningDays = [14, 3];

    /// <returns>The threshold to warn for now, or null.</returns>
    public static int? Next(DateTimeOffset notAfter, DateTimeOffset now, IReadOnlyCollection<int> alreadyWarned)
    {
        if (notAfter <= now) return null;
        var remaining = notAfter - now;
        int? due = null;
        foreach (var days in WarningDays)
            if (remaining <= TimeSpan.FromDays(days)) due = days;
        return due is { } threshold && !alreadyWarned.Any(day => day <= threshold) ? threshold : null;
    }
}
