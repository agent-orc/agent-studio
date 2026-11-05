using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer;

public sealed record TaskServerOperatorLogWrite(string Project, string Kind, string Text,
    IReadOnlyList<string>? CardKeys, IReadOnlyList<string>? AuditIds);
public sealed record TaskServerOperatorLogEntry(long Cursor, string Actor, DateTime TimeUtc, string Project,
    string Kind, string Text, IReadOnlyList<string> CardKeys, IReadOnlyList<string> AuditIds);

public sealed partial class TaskServerStore
{
    private readonly object _operatorLogGate = new();
    private static readonly Regex OperatorTokenPattern = new(
        @"(?<![A-Za-z0-9])(?:api|sat|oas|inv|rnr|enr|ssn|otl)\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]{8,})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private string OperatorLogPath => Path.Combine(DataDirectory, "operator", "operator-log.jsonl");

    public async Task<TaskServerOperatorLogEntry> AppendOperatorLogAsync(
        TaskServerOperatorLogWrite write, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(write.Project))
            throw new ArgumentException("project is required.");
        if (write.Project != "*") _ = await RequireProjectAsync(write.Project, ct);
        var kind = write.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("observed" or "decided" or "did" or "handoff")
            || string.IsNullOrWhiteSpace(write.Text) || write.Text.Length > 16_000)
            throw new ArgumentException("A valid kind and text up to 16000 characters are required.");
        lock (_operatorLogGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OperatorLogPath)!);
            var cursor = File.Exists(OperatorLogPath) ? File.ReadLines(OperatorLogPath).LongCount() + 1 : 1;
            string Redact(string value) => OperatorTokenPattern.Replace(value.Trim(), "[redacted]");
            var entry = new TaskServerOperatorLogEntry(cursor, actor, UtcNow, write.Project.Trim(), kind,
                Redact(write.Text), (write.CardKeys ?? []).Select(Redact).Take(50).ToArray(),
                (write.AuditIds ?? []).Select(Redact).Take(50).ToArray());
            File.AppendAllText(OperatorLogPath, JsonSerializer.Serialize(entry) + "\n");
            return entry;
        }
    }

    public IReadOnlyList<TaskServerOperatorLogEntry> ReadOperatorLog(string? project, long after)
    {
        lock (_operatorLogGate)
        {
            if (!File.Exists(OperatorLogPath)) return [];
            return File.ReadLines(OperatorLogPath)
                .Select(line => JsonSerializer.Deserialize<TaskServerOperatorLogEntry>(line))
                .Where(entry => entry is not null && entry.Cursor > after
                    && (project is null || entry.Project == "*"
                        || string.Equals(entry.Project, project, StringComparison.OrdinalIgnoreCase)))
                .Take(1000).Cast<TaskServerOperatorLogEntry>().ToArray();
        }
    }
}
