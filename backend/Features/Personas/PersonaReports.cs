using System.Text.Json;

namespace AgentStudio.Personas;

public sealed record PersonaReportItemV1(string ItemId, string Title, int Importance);
public sealed record PersonaReportRecordV1(string Id, string PersonaId, string RoleId,
    string OccurrenceKey, string Template, string State, string Reason, DateTime PublishedAtUtc,
    IReadOnlyList<PersonaReportItemV1> OpenItems,
    IReadOnlyList<string> SourcesRead, IReadOnlyList<string> SourcesMissing,
    long TokensUsed, int SessionsUsed)
{
    public IReadOnlyList<string> Coverage { get; init; } = [];
}

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

    public PersonaReportRecordV1 SaveCompleted(string workspaceId, PersonaOccurrenceReceipt receipt,
        PersonaActivationV1 activation, PersonaGroundsV1 grounds,
        IReadOnlyList<PersonaItemV1> items, DateTime now)
    {
        lock (_gate)
        {
            var reports = Load(workspaceId);
            var existing = reports.FirstOrDefault(r => r.OccurrenceKey == receipt.Key);
            if (existing is not null) return existing;
            var template = PersonaReportTemplates.Find(activation.Descriptor.Report.Template);
            var kinds = grounds.Sources.Select(s => s.Kind).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var coverage = template?.Anchors.Select(anchor => anchor + ":" +
                (grounds.Sources.Any(s => s.Summary.Contains(anchor.Replace('-', ' '), StringComparison.OrdinalIgnoreCase)
                    || s.Summary.Contains(anchor, StringComparison.OrdinalIgnoreCase))
                    ? "observed" : "no-evidence")).ToArray() ?? [];
            var report = new PersonaReportRecordV1("prp_" + Guid.CreateVersion7().ToString("N"),
                activation.PersonaId, activation.RoleId ?? activation.Descriptor.Id, receipt.Key,
                activation.Descriptor.Report.Template,
                receipt.WindowEndUtc <= activation.ShadowUntil ? "shadow" : "published", "completed", now,
                items.Where(i => i.State is "open" or "draft")
                    .OrderByDescending(i => i.Importance).Take(activation.Descriptor.Report.MaxItems)
                    .Select(i => new PersonaReportItemV1(i.Id, i.Title, i.Importance)).ToArray(),
                grounds.Sources.Select(s => s.Ref).ToArray(),
                template?.SourceKinds.Where(kind => !kinds.Contains(kind)).ToArray() ?? [],
                receipt.Tokens, 1) { Coverage = coverage };
            reports.Add(report);
            Persist(workspaceId, reports);
            return report;
        }
    }

    private void Persist(string workspaceId, List<PersonaReportRecordV1> reports)
    {
        if (PathFor(workspaceId) is not { } path) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(reports, Json));
        File.Move(temp, path, true);
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
