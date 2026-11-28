using System.Text.Json;
using AgentStudio.Persistence;
using AgentStudio.Security;

namespace AgentStudio.Deploy;

public sealed class DeployRecordStore(IConfiguration configuration, IAtomicJsonFileWriter writer,
    ControlPlaneAuditLog audit, InstanceDeployService instance, GitService git, TimeProvider clock)
{
    private readonly string _path = Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required."), "deployments", "deploy-records.json");
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IgnoreReadOnlyProperties = true };

    public DeployRecordPage Page(int offset, int limit)
    {
        var records = Read();
        var now = clock.GetUtcNow();
        var items = records.OrderByDescending(row => row.StartedAtUtc).ThenBy(row => row.Id, StringComparer.Ordinal)
            .Skip(offset).Take(limit).Select(row => row.At(now)).ToArray();
        return new(items, records.Count, offset, limit);
    }

    public DeployOutcome<DeployRecord> Upsert(DeployRecordReport report, string actor)
    {
        if (report.Validate() is { } error) return DeployOutcome<DeployRecord>.Invalid(error);
        lock (_gate)
        {
            var records = Read();
            var index = records.FindIndex(item => item.Id == report.Id);
            if (records.Any(item => item.Id != report.Id && Identity(item) == Identity(report)))
                return DeployOutcome<DeployRecord>.Conflict("Another id already reports this target commit and start time.");
            var previous = index < 0 ? null : records[index];
            if (previous?.EndedAtUtc is not null && report.EndedAtUtc is null)
                return DeployOutcome<DeployRecord>.Conflict("A closed deploy cannot be reopened.");

            var row = Observe(report, previous);
            if (index < 0) records.Add(row);
            else records[index] = row;
            var request = new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, actor, "deploy-record.upsert",
                "requested", row.Id, Details: new { row.StartedAtUtc, row.ToCommit, row.Outcome });
            audit.Append(request);
            writer.WriteJson(_path, records, Json);
            audit.Append(request with { Outcome = "reported" });
            return DeployOutcome<DeployRecord>.Ok(row.At(clock.GetUtcNow()));
        }
    }

    public DeployOutcome<DeployRecordImport> Import(DeployRecordReport[] reports, string actor)
    {
        foreach (var report in reports)
        {
            if (report is null) return DeployOutcome<DeployRecordImport>.Invalid("Each import row must be a deploy record.");
            if (report.Validate() is { } error)
                return DeployOutcome<DeployRecordImport>.Invalid($"Invalid record '{report.Id}': {error}");
        }
        lock (_gate)
        {
            var records = Read();
            var identities = records.Select(Identity).ToHashSet();
            var ids = records.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            var added = new List<DeployRecordReport>();
            foreach (var report in reports)
            {
                if (!identities.Add(Identity(report))) continue;
                if (!ids.Add(report.Id))
                    return DeployOutcome<DeployRecordImport>.Conflict($"Id '{report.Id}' already identifies another deploy.");
                added.Add(report);
            }
            records.AddRange(added.Select(row => Observe(row, null)));
            var result = new DeployRecordImport(added.Count, reports.Length - added.Count);
            var idsToImport = added.Select(row => row.Id).ToArray();
            var request = new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, actor, "deploy-record.import",
                "requested", "deploy-records", Details: new { Ids = idsToImport });
            if (added.Count > 0)
            {
                audit.Append(request);
                writer.WriteJson(_path, records, Json);
            }
            audit.Append(request with { Outcome = "reported", Details = new { result.Imported, result.Skipped, Ids = idsToImport } });
            return DeployOutcome<DeployRecordImport>.Ok(result);
        }
    }

    private static (string, DateTimeOffset) Identity(DeployRecordReport row)
        => (row.ToCommit.ToLowerInvariant(), row.StartedAtUtc);

    // Readers can finish using the previous snapshot while an import computes ranges
    // and atomically replaces it. FileShare.Delete permits replacement on Windows.
    private List<DeployRecord> Read()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<List<DeployRecord>>(file, Json)
                ?? throw new JsonException("Expected an array of deploy records.");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private DeployRecord Observe(DeployRecordReport report, DeployRecord? previous)
    {
        var row = new DeployRecord(report)
        {
            FromCommit = report.FromCommit?.ToLowerInvariant(), ToCommit = report.ToCommit.ToLowerInvariant(),
            StartedAtUtc = report.StartedAtUtc.ToUniversalTime(), EndedAtUtc = report.EndedAtUtc?.ToUniversalTime(),
        };
        if (row.FromCommit is null) return row with { CardRangeDetail = "Report fromCommit to identify the card range." };
        if (previous?.CardsInRange is { } known && previous.FromCommit == row.FromCommit && previous.ToCommit == row.ToCommit)
            return row with { CardsInRange = known };
        if (instance.SourceProject is not { } project || git.ResolveRepoRootForProject(project) is not { } repo)
            return row with { CardRangeDetail = "Configure the Studio source project and its repository, then repost this record." };
        return Directory.Exists(repo) && git.DeployRangeCardKeys(repo, row.FromCommit, row.ToCommit) is { } keys
            ? row with { CardsInRange = keys }
            : row with { CardRangeDetail = "Make both commits and their full history available in the Studio source repository, then repost this record." };
    }
}
