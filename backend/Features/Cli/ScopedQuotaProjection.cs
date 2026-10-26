using AgentStudio.Shared;

namespace AgentStudio.Cli;

internal static class ScopedQuotaProjection
{
    public static QuotaReport Redact(QuotaReport report)
        => report with
        {
            Snapshots = report.Snapshots
                .Select(snapshot => snapshot with { RawSample = null })
                .ToList(),
        };
}
