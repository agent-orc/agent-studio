using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public sealed record DecisionRuleVersionV1(string VersionId, string WorkspaceId, string? ProjectId,
    string? ParentVersionId, string Actor, string Reason, string Diff, DateTime CreatedAt,
    IReadOnlyList<DecisionRuleV1> Rules);

public sealed record DecisionRuleActivationV1(string WorkspaceId, string? ProjectId, string VersionId,
    string Actor, string Reason, DateTime ActivatedAt, long Sequence = 0);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DecisionRuleDraftRequest(string? ParentVersionId, string Actor, string Reason,
    IReadOnlyList<DecisionRuleV1> Rules);

public sealed record DecisionRuleActivateRequest(string Actor, string Reason);

public sealed record DecisionRuleViewV1(string WorkspaceId, string? ProjectId, string? ActiveVersionId,
    IReadOnlyList<DecisionRuleVersionV1> Versions, IReadOnlyList<DecisionRuleActivationV1> Activations,
    IReadOnlyList<DecisionAuthorityFloorV1> HardFloors);

public sealed record DecisionRuleReplayRowV1(string ReceiptId, string OccurrenceId, string Trigger,
    IReadOnlyList<string> OldSignals, IReadOnlyList<string> NewSignals,
    IReadOnlyList<string> OldCandidates, IReadOnlyList<string> NewCandidates,
    string ObservedOutcome, bool MissingEvidence, long ObservedInputTokens,
    long ObservedOutputTokens, decimal? ObservedUsd, string ModelResultLabel);

public sealed record DecisionRuleDryRunV1(string CandidateVersionId, int Replayed,
    IReadOnlyList<DecisionRuleReplayRowV1> Occurrences, IReadOnlyList<string> FloorConflicts,
    string ModelResultLabel = "counterfactual-model-result");

/// <summary>
/// API-owned immutable rule versions and append-only activation records. Project
/// overlays may change soft signals, but hard authority floors stay in code.
/// </summary>
public sealed class DecisionRuleVersionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly TimeProvider _time;
    private readonly List<DecisionRuleVersionV1> _versions = [];
    private readonly List<DecisionRuleActivationV1> _activations = [];

    public DecisionRuleVersionStore(IConfiguration config, TimeProvider time)
        : this(string.IsNullOrWhiteSpace(config["TaskRepository"]) ? null
            : Path.Combine(config["TaskRepository"]!, ".metadata", "orchestrator-rules"), time) { }

    public DecisionRuleVersionStore(string? root, TimeProvider time)
    {
        _root = root;
        _time = time;
        if (root is null || !Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            if (Path.GetFileName(path).StartsWith("activation-", StringComparison.Ordinal))
            {
                var item = JsonSerializer.Deserialize<DecisionRuleActivationV1>(File.ReadAllText(path), Json);
                if (item is not null) _activations.Add(item);
            }
            else if (Path.GetFileName(path).StartsWith("version-", StringComparison.Ordinal))
            {
                var item = JsonSerializer.Deserialize<DecisionRuleVersionV1>(File.ReadAllText(path), Json);
                if (item is not null) _versions.Add(item);
            }
        }
        _activations.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
    }

    public DecisionRuleViewV1 View(string workspaceId, string? projectId = null)
    {
        lock (_gate) return new(workspaceId, projectId, ActiveId(workspaceId, projectId),
            _versions.Where(v => SameScope(v, workspaceId, projectId)).OrderBy(v => v.CreatedAt).ToArray(),
            _activations.Where(a => a.WorkspaceId == workspaceId && a.ProjectId == projectId)
                .OrderBy(a => a.Sequence).ToArray(), DecisionRulePolicy.HardFloors);
    }

    public DecisionRuleVersionV1 Draft(string workspaceId, string? projectId, DecisionRuleDraftRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500) throw new ArgumentException("Actor and bounded reason are required.");
        var validation = DecisionRulePolicy.Validate(request.Rules, projectId is not null);
        if (!validation.Accepted) throw new ArgumentException(string.Join(" ", validation.Refusals));
        lock (_gate)
        {
            var active = ActiveId(workspaceId, projectId);
            if (request.ParentVersionId != active)
                throw new InvalidOperationException("Rule draft parent differs from the active version.");
            var previous = _versions.FirstOrDefault(v => v.VersionId == active)?.Rules ?? [];
            var changed = request.Rules.Select(rule => rule.Id).Union(previous.Select(rule => rule.Id))
                .Where(id => !Equals(request.Rules.FirstOrDefault(r => r.Id == id),
                    previous.FirstOrDefault(r => r.Id == id))).OrderBy(id => id).ToArray();
            var version = new DecisionRuleVersionV1("drv_" + Guid.NewGuid().ToString("N"),
                workspaceId, projectId, active, request.Actor.Trim(), request.Reason.Trim(),
                string.Join(", ", changed), _time.GetUtcNow().UtcDateTime, request.Rules.ToArray());
            Save("version-" + version.VersionId, version);
            _versions.Add(version);
            return version;
        }
    }

    public DecisionRuleActivationV1 Activate(string workspaceId, string? projectId, string versionId,
        DecisionRuleActivateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500) throw new ArgumentException("Actor and bounded reason are required.");
        lock (_gate)
        {
            var version = _versions.FirstOrDefault(v => v.VersionId == versionId
                && SameScope(v, workspaceId, projectId)) ?? throw new KeyNotFoundException("Rule version not found.");
            if (ActiveId(workspaceId, projectId) != version.ParentVersionId)
                throw new InvalidOperationException("Rule version parent is stale; draft a new version for rollback.");
            var validation = DecisionRulePolicy.Validate(version.Rules, projectId is not null);
            if (!validation.Accepted) throw new InvalidOperationException(string.Join(" ", validation.Refusals));
            var activation = new DecisionRuleActivationV1(workspaceId, projectId, versionId,
                request.Actor.Trim(), request.Reason.Trim(), _time.GetUtcNow().UtcDateTime,
                _activations.Count == 0 ? 1 : _activations[^1].Sequence + 1);
            Save("activation-" + Guid.NewGuid().ToString("N"), activation);
            _activations.Add(activation);
            return activation;
        }
    }

    public DecisionRuleVersionV1 Rollback(string workspaceId, string? projectId, string targetVersionId,
        DecisionRuleActivateRequest request)
    {
        lock (_gate)
        {
            var target = _versions.FirstOrDefault(v => v.VersionId == targetVersionId
                && SameScope(v, workspaceId, projectId)) ?? throw new KeyNotFoundException("Rule version not found.");
            var version = Draft(workspaceId, projectId,
                new(ActiveId(workspaceId, projectId), request.Actor, request.Reason, target.Rules));
            Activate(workspaceId, projectId, version.VersionId, request);
            return version;
        }
    }

    public string? ActiveVersionId(string workspaceId, string? projectId = null)
    {
        lock (_gate) return ActiveId(workspaceId, projectId);
    }

    public DecisionRuleVersionV1? Effective(string workspaceId, string? projectId = null)
    {
        lock (_gate)
        {
            var id = ActiveId(workspaceId, projectId) ?? ActiveId(workspaceId, null);
            return _versions.FirstOrDefault(v => v.VersionId == id);
        }
    }

    public IReadOnlyList<DecisionRuleV1> EffectiveRules(string workspaceId, string? projectId = null)
    {
        lock (_gate)
        {
            var workspace = _versions.FirstOrDefault(v => v.VersionId == ActiveId(workspaceId, null))?.Rules ?? [];
            if (projectId is null) return workspace.ToArray();
            var overlay = _versions.FirstOrDefault(v => v.VersionId == ActiveId(workspaceId, projectId))?.Rules ?? [];
            var result = workspace.Where(rule => overlay.All(other => other.Id != rule.Id)).Concat(overlay).ToArray();
            return result;
        }
    }

    public DecisionRuleDryRunV1 DryRun(string workspaceId, string? projectId, string versionId,
        IReadOnlyList<OrchestratorDecisionReceiptV1> receipts)
    {
        lock (_gate)
        {
            var candidate = _versions.FirstOrDefault(v => v.VersionId == versionId
                && SameScope(v, workspaceId, projectId)) ?? throw new KeyNotFoundException("Rule version not found.");
            var current = EffectiveRules(workspaceId, projectId);
            var candidateRules = projectId is null ? candidate.Rules
                : EffectiveRules(workspaceId).Where(rule => candidate.Rules.All(other => other.Id != rule.Id))
                    .Concat(candidate.Rules).ToArray();
            var recent = receipts.Where(r => r.EndedAt >= _time.GetUtcNow().UtcDateTime.AddDays(-30))
                .OrderByDescending(r => r.EndedAt).Take(100).ToArray();
            var rows = recent.Select(receipt =>
            {
                var oldRules = DecisionRulePolicy.Replay(current, receipt);
                var newRules = DecisionRulePolicy.Replay(candidateRules, receipt);
                return new DecisionRuleReplayRowV1(receipt.ReceiptId, receipt.OccurrenceId,
                    receipt.Trigger.Kind, oldRules.Signals, newRules.Signals,
                    oldRules.Candidates, newRules.Candidates, receipt.DispatchOutcome,
                    newRules.MissingEvidence, receipt.Call.Tokens.UncachedInput
                    + receipt.Call.Tokens.CachedInput + receipt.Call.Tokens.CacheWrite,
                    receipt.Call.Tokens.Output, receipt.Call.Price.EstimatedUsd,
                    "counterfactual-model-result");
            }).ToArray();
            return new(versionId, rows.Length, rows,
                DecisionRulePolicy.Validate(candidate.Rules, projectId is not null).Refusals);
        }
    }

    private string? ActiveId(string workspaceId, string? projectId)
        => _activations.LastOrDefault(a => a.WorkspaceId == workspaceId && a.ProjectId == projectId)?.VersionId;

    private static bool SameScope(DecisionRuleVersionV1 version, string workspaceId, string? projectId)
        => version.WorkspaceId == workspaceId && version.ProjectId == projectId;

    private void Save<T>(string name, T value)
    {
        if (_root is null) return;
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, name + ".json");
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, target);
    }
}
