using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>Fleet-level daily rollup of safe provider refusal facts.</summary>
public sealed class ProviderRejectionFleetService(
    TaskScannerService scanner,
    TimelineLog timeline)
{
    public IReadOnlyList<ProviderRejectionDailyCountDto> Snapshot(int days, DateTime? now = null)
    {
        var boundedDays = Math.Clamp(days, 1, 90);
        var today = (now ?? DateTime.UtcNow).ToUniversalTime().Date;
        var firstDay = today.AddDays(-(boundedDays - 1));
        return Aggregate(
            scanner.ScanAllJobs().SelectMany(task => timeline.ReadAll(task.FolderPath)),
            firstDay,
            today);
    }

    internal static IReadOnlyList<ProviderRejectionDailyCountDto> Aggregate(
        IEnumerable<TimelineEvent> events,
        DateTime firstDayUtc,
        DateTime lastDayUtc)
        => events
            .Where(entry => entry.Kind == TimelineEventKinds.AgentRunFinished
                            && entry.Details?.GetValueOrDefault("typedOutcome")
                            == ExecutionOutcomeKind.ProviderRejectedRequest.ToString())
            .Select(entry => new
            {
                Day = entry.Ts.ToUniversalTime().Date,
                Model = entry.Details?.GetValueOrDefault("providerRejectionModel")?.Trim(),
                Code = entry.Details?.GetValueOrDefault("providerRejectionCode")?.Trim(),
                Parameter = entry.Details?.GetValueOrDefault("providerRejectionParam")?.Trim(),
            })
            .Where(item => item.Day >= firstDayUtc.Date
                           && item.Day <= lastDayUtc.Date
                           && !string.IsNullOrWhiteSpace(item.Model))
            .GroupBy(item => new { item.Day, Model = item.Model! })
            .Select(group => new ProviderRejectionDailyCountDto(
                group.Key.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                group.Key.Model,
                group.Count(),
                group.Select(item => string.Join(
                        " ",
                        new[] { item.Code, item.Parameter }.Where(value => !string.IsNullOrWhiteSpace(value))))
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(item => item.Day, StringComparer.Ordinal)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
