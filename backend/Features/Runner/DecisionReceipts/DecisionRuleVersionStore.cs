using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary><paramref name="Degraded"/> is true when a stored record was unreadable and quarantined.</summary>
public sealed record DecisionRuleViewV1(string WorkspaceId, string? ProjectId, string? ActiveVersionId,
    IReadOnlyList<DecisionRuleVersionV1> Versions, IReadOnlyList<DecisionRuleActivationV1> Activations,
    IReadOnlyList<DecisionAuthorityFloorV1> HardFloors, bool Degraded = false,
    IReadOnlyList<string>? Quarantined = null);

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
    public const string QuarantineFolder = "quarantine";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly TimeProvider _time;
    private readonly DecisionRuleModelCatalogue _models;
    private readonly ILogger _logger;
    private readonly List<DecisionRuleVersionV1> _versions = [];
    private readonly List<DecisionRuleActivationV1> _activations = [];
    private readonly List<string> _quarantined = [];

    public DecisionRuleVersionStore(IConfiguration config, TimeProvider time, ILogger<DecisionRuleVersionStore> logger)
        : this(string.IsNullOrWhiteSpace(config["TaskRepository"]) ? null
            : Path.Combine(config["TaskRepository"]!, ".metadata", "orchestrator-rules"), time,
            DecisionRuleModelCatalogueSource.For(config), logger) { }

    /// <summary>
    /// Never throws for stored content: the runner completion path reads these
    /// rules, so an unreadable file is quarantined and the view reports degraded.
    /// </summary>
    public DecisionRuleVersionStore(string? root, TimeProvider time, DecisionRuleModelCatalogue? models = null,
        ILogger? logger = null)
    {
        _root = root;
        _time = time;
        _models = models ?? DecisionRuleModelCatalogueSource.For("codex");
        _logger = logger ?? NullLogger.Instance;
        if (root is null) return;
        string[] files;
        try { files = Directory.Exists(root) ? Directory.GetFiles(root, "*.json") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Decision rule records under {Root} could not be listed; rules load degraded.", root);
            _quarantined.Add(root);
            return;
        }
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            try
            {
                if (name.StartsWith("activation-", StringComparison.Ordinal))
                {
                    var item = JsonSerializer.Deserialize<DecisionRuleActivationV1>(File.ReadAllText(path), Json);
                    if (item is null || string.IsNullOrWhiteSpace(item.WorkspaceId)
                        || string.IsNullOrWhiteSpace(item.VersionId))
                        throw new InvalidDataException("Activation record is empty or incomplete.");
                    _activations.Add(item);
                }
                else if (name.StartsWith("version-", StringComparison.Ordinal))
                {
                    var item = JsonSerializer.Deserialize<DecisionRuleVersionV1>(File.ReadAllText(path), Json);
                    if (item is null || string.IsNullOrWhiteSpace(item.VersionId)
                        || string.IsNullOrWhiteSpace(item.WorkspaceId) || item.Rules is null
                        || item.Rules.Any(rule => rule is null))
                        throw new InvalidDataException("Rule version record is empty or incomplete.");
                    _versions.Add(item);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                Quarantine(path, e);
            }
        }
        _activations.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
    }

    /// <summary>The model catalogue projected for the configured decision CLI.</summary>
    public DecisionRuleModelCatalogue Models => _models;

    public DecisionRuleViewV1 View(string workspaceId, string? projectId = null)
    {
        lock (_gate) return new(workspaceId, projectId, ActiveId(workspaceId, projectId),
            _versions.Where(v => SameScope(v, workspaceId, projectId)).OrderBy(v => v.CreatedAt).ToArray(),
            _activations.Where(a => a.WorkspaceId == workspaceId && a.ProjectId == projectId)
                .OrderBy(a => a.Sequence).ToArray(), DecisionRulePolicy.HardFloors,
            _quarantined.Count > 0, _quarantined.Select(Path.GetFileName).OfType<string>().ToArray());
    }

    public DecisionRuleVersionV1 Draft(string workspaceId, string? projectId, DecisionRuleDraftRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500) throw new ArgumentException("Actor and bounded reason are required.");
        var validation = MonolithTransitionAdapter.DecisionRulePolicyValidate(request.Rules, projectId is not null, _models);
        if (!validation.Accepted) throw new ArgumentException(string.Join(" ", validation.Refusals));
        lock (_gate)
        {
            if (MergedCardRefusal(workspaceId, projectId, request.Rules) is { } merged)
                throw new ArgumentException(merged);
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
            var validation = MonolithTransitionAdapter.DecisionRulePolicyValidate(version.Rules, projectId is not null, _models);
            if (!validation.Accepted) throw new InvalidOperationException(string.Join(" ", validation.Refusals));
            if (MergedCardRefusal(workspaceId, projectId, version.Rules) is { } merged)
                throw new InvalidOperationException(merged);
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
            var workspace = RulesOf(ActiveId(workspaceId, null));
            return projectId is null ? workspace.ToArray() : Overlay(workspace, RulesOf(ActiveId(workspaceId, projectId)));
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
                : Overlay(EffectiveRules(workspaceId), candidate.Rules);
            var recent = receipts.Where(r => r.EndedAt >= _time.GetUtcNow().UtcDateTime.AddDays(-30))
                .OrderByDescending(r => r.EndedAt).Take(100).ToArray();
            var rows = recent.Select(receipt =>
            {
                var oldRules = MonolithTransitionAdapter.DecisionRulePolicyReplay(current, receipt);
                var newRules = MonolithTransitionAdapter.DecisionRulePolicyReplay(candidateRules, receipt);
                return new DecisionRuleReplayRowV1(receipt.ReceiptId, receipt.OccurrenceId,
                    receipt.Trigger.Kind, oldRules.Signals, newRules.Signals,
                    oldRules.Candidates, newRules.Candidates, receipt.DispatchOutcome,
                    newRules.MissingEvidence, receipt.Call.Tokens.UncachedInput
                    + receipt.Call.Tokens.CachedInput + receipt.Call.Tokens.CacheWrite,
                    receipt.Call.Tokens.Output, receipt.Call.Price.EstimatedUsd,
                    "counterfactual-model-result");
            }).ToArray();
            var conflicts = MonolithTransitionAdapter.DecisionRulePolicyValidate(candidate.Rules,
                projectId is not null, _models).Refusals.ToList();
            if (MergedCardRefusal(workspaceId, projectId, candidate.Rules) is { } merged) conflicts.Add(merged);
            return new(versionId, rows.Length, rows, conflicts);
        }
    }

    private string? ActiveId(string workspaceId, string? projectId)
        => _activations.LastOrDefault(a => a.WorkspaceId == workspaceId && a.ProjectId == projectId)?.VersionId;

    private IReadOnlyList<DecisionRuleV1> RulesOf(string? versionId)
        => _versions.FirstOrDefault(v => v.VersionId == versionId)?.Rules ?? [];

    private static IReadOnlyList<DecisionRuleV1> Overlay(IReadOnlyList<DecisionRuleV1> workspace,
        IReadOnlyList<DecisionRuleV1> overlay)
        => workspace.Where(rule => overlay.All(other => other.Id != rule.Id)).Concat(overlay).ToArray();

    /// <summary>
    /// Card rules govern every card of the workspace through the merged rule
    /// set. A workspace version is checked with each active project overlay.
    /// </summary>
    private string? MergedCardRefusal(string workspaceId, string? projectId, IReadOnlyList<DecisionRuleV1> rules)
    {
        if (projectId is not null)
            return MonolithTransitionAdapter.DecisionRulePolicyCardAlternativeRefusal(
                Overlay(RulesOf(ActiveId(workspaceId, null)), rules)) is { } refusal
                ? $"Project overlay {projectId}: {refusal}" : null;
        foreach (var project in _activations.Where(a => a.WorkspaceId == workspaceId && a.ProjectId is not null)
                     .Select(a => a.ProjectId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (MonolithTransitionAdapter.DecisionRulePolicyCardAlternativeRefusal(
                    Overlay(rules, RulesOf(ActiveId(workspaceId, project)))) is { } refusal)
                return $"With project overlay {project}: {refusal}";
        return null;
    }

    private static bool SameScope(DecisionRuleVersionV1 version, string workspaceId, string? projectId)
        => version.WorkspaceId == workspaceId && version.ProjectId == projectId;

    private void Quarantine(string path, Exception cause)
    {
        _quarantined.Add(path);
        _logger.LogError(cause, "Decision rule record {Path} is unreadable; it is quarantined and rules load degraded.", path);
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(path)!, QuarantineFolder);
            Directory.CreateDirectory(folder);
            File.Move(path, Path.Combine(folder, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Decision rule record {Path} could not be moved to quarantine.", path);
        }
    }

    /// <summary>Same durability as the decision receipt store: write through, flush, then publish.</summary>
    private void Save<T>(string name, T value)
    {
        if (_root is null) return;
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, name + ".json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
