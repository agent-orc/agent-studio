using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public sealed record TestQuarantineProjectStore(
    IReadOnlyList<ProjectTestQuarantine> Entries,
    IReadOnlyList<TestQuarantineAudit> Audit,
    IReadOnlyList<PostMergeIncidentLogEntry> IncidentLog);

/// <summary>API-owned quarantine authority beside the post-merge store. Resolved entries and audit rows are retained.</summary>
public sealed class TestQuarantineStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _root;
    public TestQuarantineStore(string? root = null)
        => _root = root ?? LocalStateRoot.Combine("agentstudio", "test-quarantines");

    public TestQuarantineProjectStore Read(string projectId)
    {
        lock (_gate) return ReadLocked(projectId);
    }

    public IReadOnlyList<ProjectTestQuarantine> Active(string projectId, string? repositoryPath, DateTimeOffset now)
    {
        var entries = Read(projectId).Entries.Where(entry => MonolithTransitionAdapter.QuarantineActive(entry, now));
        var file = repositoryPath is null ? [] : TestQuarantineFile.Read(repositoryPath).Entries;
        // File expiry remains inclusive of the entire UTC date, as in AGT-W57.
        return entries.Concat(file.Where(entry => TestQuarantinePolicy.Evaluate(entry,
                DateOnly.FromDateTime(now.UtcDateTime)) == TestQuarantineStatus.Active)
            .Select(entry => new ProjectTestQuarantine("file:" + entry.Test, entry.Test, entry.Card,
                entry.ExpiresOn, entry.ResolvedOn, entry.Reason, "report-only", "owner", DateTimeOffset.MinValue,
                entry.ExpiresOn == DateOnly.MaxValue ? DateTimeOffset.MaxValue
                    : new DateTimeOffset(entry.ExpiresOn.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                "file:" + entry.Test, [], [], Source: "repository"))).ToArray();
    }

    public ProjectTestQuarantine Start(string projectId, ProjectTestQuarantine entry,
        IReadOnlyList<ProjectTestQuarantine> active, bool baseTest = false, bool owner = false)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            var existing = state.Entries.FirstOrDefault(item => item.Test == entry.Test
                && item.IncidentFingerprint == entry.IncidentFingerprint && item.LiftedAtUtc is null);
            if (existing?.Kind == entry.Kind)
            {
                if (!owner || existing.AttributedRevertCardId == entry.AttributedRevertCardId
                    && existing.OwnerExemptCardIds.SequenceEqual(entry.OwnerExemptCardIds)) return existing;
                var exempted = existing with { AttributedRevertCardId = entry.AttributedRevertCardId,
                    OwnerExemptCardIds = entry.OwnerExemptCardIds };
                SaveChange(projectId, state, existing, exempted, "exemptions", entry.StartedBy, entry.StartedAtUtc);
                return exempted;
            }
            // Refresh store facts under the write lock; callers supply only the repository-file portion.
            var union = state.Entries.Where(item => item.Id != existing?.Id
                    && MonolithTransitionAdapter.QuarantineActive(item, entry.StartedAtUtc))
                .Concat(active.Where(item => item.Source == "repository")).ToArray();
            if (MonolithTransitionAdapter.QuarantineStartRefusal(union, entry.Kind, baseTest) is { } refusal)
                throw new InvalidOperationException(refusal);
            var after = existing is null ? entry : entry with { Id = existing.Id,
                EvidenceRunIds = existing.EvidenceRunIds.Concat(entry.EvidenceRunIds).Distinct(StringComparer.Ordinal).ToArray() };
            SaveChange(projectId, state, existing, after, existing is null ? "start" : "change-kind", entry.StartedBy, entry.StartedAtUtc);
            return after;
        }
    }

    public ProjectTestQuarantine Renew(string projectId, string id, string actor, bool owner,
        string? repairLane, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            var before = state.Entries.FirstOrDefault(entry => entry.Id == id)
                ?? throw new KeyNotFoundException("Quarantine not found.");
            if (MonolithTransitionAdapter.QuarantineRenewRefusal(before, owner, repairLane, now) is { } refusal)
                throw new InvalidOperationException(refusal);
            var expires = before.Kind == "held" ? before.ExpiresAtUtc + RedTestTriageBounds.RenewalDuration : now + RedTestTriageBounds.ReportOnlyDuration;
            var after = before with { Renewals = before.Renewals + 1, ExpiresAtUtc = expires,
                ExpiresOn = DateOnly.FromDateTime(expires.UtcDateTime) };
            SaveChange(projectId, state, before, after, "renew", actor, now);
            return after;
        }
    }

    public ProjectTestQuarantine Lift(string projectId, string id, string actor, bool owner,
        IReadOnlyList<AgentStudio.OrchestrationDecisions.RedTestLiftRun> runs,
        IReadOnlyList<string> evidence, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            var before = state.Entries.FirstOrDefault(entry => entry.Id == id)
                ?? throw new KeyNotFoundException("Quarantine not found.");
            if (before.LiftedAtUtc is not null) return before;
            if (MonolithTransitionAdapter.QuarantineLiftRefusal(owner, runs) is { } refusal)
                throw new InvalidOperationException(refusal);
            var after = before with { LiftedBy = actor, LiftedAtUtc = now,
                ResolvedOn = DateOnly.FromDateTime(now.UtcDateTime), LiftEvidence = evidence,
                EvidenceRunIds = before.EvidenceRunIds.Concat(evidence).Distinct(StringComparer.Ordinal).ToArray() };
            SaveChange(projectId, state, before, after, "lift", actor, now);
            return after;
        }
    }

    public void Log(string projectId, PostMergeIncidentLogEntry entry)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            if (state.IncidentLog.Any(item => item.DecisionId == entry.DecisionId)) return;
            WriteLocked(projectId, state with { IncidentLog = [.. state.IncidentLog, entry] });
        }
    }

    private void SaveChange(string projectId, TestQuarantineProjectStore state, ProjectTestQuarantine? before,
        ProjectTestQuarantine after, string operation, string actor, DateTimeOffset now)
        => WriteLocked(projectId, state with
        {
            Entries = before is null ? [.. state.Entries, after]
                : state.Entries.Select(entry => entry.Id == after.Id ? after : entry).ToArray(),
            Audit = [.. state.Audit, new(Guid.NewGuid().ToString("N"), after.Id, operation, actor, now, before, after)],
        });

    private TestQuarantineProjectStore ReadLocked(string projectId)
    {
        var path = PathFor(projectId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<TestQuarantineProjectStore>(File.ReadAllText(path), Json)
                ?? throw new IOException("Quarantine store is empty.")
            : new([], [], []);
    }

    private void WriteLocked(string projectId, TestQuarantineProjectStore state)
    {
        var path = PathFor(projectId);
        Directory.CreateDirectory(_root);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string projectId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectId, "^PROJ-[0-9]{3,}$"))
            throw new ArgumentException("A stable project ID is required.", nameof(projectId));
        return Path.Combine(_root, projectId + ".json");
    }
}
