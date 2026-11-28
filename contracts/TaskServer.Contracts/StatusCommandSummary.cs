using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

public sealed record SetStatusCommandIntervalRequest(int? Seconds);

public static class StatusCommandInterval
{
    public const int DefaultSeconds = 180;
}

public sealed record StatusCommandSummary(long CommandCalls, long DelayedCalls, long RepeatedStatusCalls)
{
    public double RepeatedStatusShare => CommandCalls == 0 ? 0 : (double)RepeatedStatusCalls / CommandCalls;

    public static StatusCommandSummary Sum(IEnumerable<StatusCommandSummary> summaries)
    {
        long calls = 0, delayed = 0, repeated = 0;
        foreach (var summary in summaries)
        {
            calls += summary.CommandCalls;
            delayed += summary.DelayedCalls;
            repeated += summary.RepeatedStatusCalls;
        }
        return new(calls, delayed, repeated);
    }

    [JsonIgnore]
    public bool IsValid => CommandCalls >= RepeatedStatusCalls && RepeatedStatusCalls >= DelayedCalls && DelayedCalls >= 0;
}

public sealed record StatusCommandRunSummary(string AttemptId, StatusCommandSummary Commands)
{
    public const string LogPrefix = "[runner] status-command-summary ";
    public const string EventKind = "runner.status-command-summary";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToLogLine() => LogPrefix + JsonSerializer.Serialize(this, Json);

    public static StatusCommandRunSummary? FromLogLine(string? line)
    {
        if (line is null || line.Length > 2048 || !line.StartsWith(LogPrefix, StringComparison.Ordinal))
            return null;
        try
        {
            var summary = JsonSerializer.Deserialize<StatusCommandRunSummary>(line.AsSpan(LogPrefix.Length), Json);
            return summary is { AttemptId.Length: > 0, Commands.IsValid: true } ? summary : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
