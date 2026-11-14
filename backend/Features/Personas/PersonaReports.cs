using System.Text.Json;

namespace AgentStudio.Personas;

public sealed record PersonaReportItemV1(string ItemId, string Title, int Importance);
public sealed record PersonaReportRecordV1(string Id, string PersonaId, string RoleId,
    string OccurrenceKey, string Template, string State, string Reason, DateTime PublishedAtUtc,
    IReadOnlyList<PersonaReportItemV1> OpenItems,
    IReadOnlyList<string> SourcesRead, IReadOnlyList<string> SourcesMissing,
    long TokensUsed, int SessionsUsed);

/// <summary>Application-owned, immutable reports keyed by occurrence.</summary>
public sealed class PersonaReportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly Dictionary<string, List<PersonaReportRecordV1>> _states = new(StringComparer.Ordinal);

    public PersonaReportStore(IConfiguration configuration) : this(
        string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null
            : Path.Combine(configuration["TaskRepository"]!, ".metadata", "persona-reports")) { }
    public PersonaReportStore(string? root) => _root = root;

    public IReadOnlyList<PersonaReportRecordV1> List(string workspaceId)
    {
        lock (_gate) return [.. Load(workspaceId)];
    }

    public PersonaReportRecordV1 SaveStub(string workspaceId, PersonaOccurrenceReceipt receipt,
        PersonaDescriptorV1 descriptor, IReadOnlyList<PersonaItemV1> items,
        IReadOnlyList<PersonaOccurrenceReceipt> receipts, DateTime now)
    {
        lock (_gate)
        {
            var reports = Load(workspaceId);
            var existing = reports.FirstOrDefault(r => r.OccurrenceKey == receipt.Key);
            if (existing is not null) return existing;
            var start = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var week = receipts.Where(r => r.PersonaId == receipt.PersonaId
                && r.CreatedAtUtc >= start && r.CreatedAtUtc < start.AddDays(7)).ToArray();
            var open = items.Where(i => i.PersonaId == receipt.PersonaId && i.State is "open" or "draft")
                .OrderByDescending(i => i.Importance).ThenBy(i => i.CreatedAtUtc)
                .Take(descriptor.Report.MaxItems)
                .Select(i => new PersonaReportItemV1(i.Id, i.Title, i.Importance)).ToArray();
            var report = new PersonaReportRecordV1("prp_" + Guid.CreateVersion7().ToString("N"),
                receipt.PersonaId, receipt.RoleId, receipt.Key, descriptor.Report.Template,
                "stub", receipt.Reason ?? "persona-admission-refused", now, open, [],
                receipt.TriggerRefs, week.Sum(r => r.Tokens),
                week.Count(r => r.State != PersonaOccurrenceStates.Waiting));
            reports.Add(report);
            if (PathFor(workspaceId) is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(reports, Json));
                File.Move(temp, path, true);
            }
            return report;
        }
    }

    private List<PersonaReportRecordV1> Load(string workspaceId)
    {
        if (_states.TryGetValue(workspaceId, out var reports)) return reports;
        var path = PathFor(workspaceId);
        reports = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<List<PersonaReportRecordV1>>(File.ReadAllText(path), Json) ?? [] : [];
        return _states[workspaceId] = reports;
    }

    private string? PathFor(string workspaceId)
    {
        if (_root is null) return null;
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId))
            throw new ArgumentException("Invalid workspace id.");
        return Path.Combine(_root, workspaceId + ".json");
    }
}
