using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// One scheduler-load-bearing <c>dependsOn</c> edge. Legacy edges are stored as
/// plain strings. An edge that also requires an explicit content release is
/// stored as <c>{ "key": "ATP-19", "releaseGate": true }</c>.
/// </summary>
[JsonConverter(typeof(TaskDependencyReferenceJsonConverter))]
public sealed record TaskDependencyReference
{
    public TaskDependencyReference(string key, bool releaseGate = false)
    {
        Key = key;
        ReleaseGate = releaseGate;
    }

    public string Key { get; init; }
    public bool ReleaseGate { get; init; }

    public static implicit operator TaskDependencyReference(string key) => new(key);
}

/// <summary>
/// Preserves the legacy string wire shape while allowing release-gated edges
/// to opt into the richer object shape.
/// </summary>
public sealed class TaskDependencyReferenceJsonConverter : JsonConverter<TaskDependencyReference>
{
    public override TaskDependencyReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new TaskDependencyReference(reader.GetString() ?? "");

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A dependsOn entry must be a task key or an object.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var key = root.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString() ?? ""
            : "";
        var releaseGate = root.TryGetProperty("releaseGate", out var gateElement)
            && gateElement.ValueKind == JsonValueKind.True;
        return new TaskDependencyReference(key, releaseGate);
    }

    public override void Write(Utf8JsonWriter writer, TaskDependencyReference value, JsonSerializerOptions options)
    {
        if (!value.ReleaseGate)
        {
            writer.WriteStringValue(value.Key);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("key", value.Key);
        writer.WriteBoolean("releaseGate", true);
        writer.WriteEndObject();
    }
}

/// <summary>
/// F34 — structured cross-references between tasks, keyed by F33 stable keys
/// (e.g. <c>ATP-19</c>). Replaces freetext "F22 depends on F19" notes in
/// prompts with a queryable, navigable, validatable field. Stored as the
/// <c>"references"</c> object in <c>job.json</c>; absent or null on disk means
/// "no references" and the scanner surfaces an empty instance.
///
/// <para>Task relations plus one typed document-reference namespace (see
/// <see cref="TaskReferenceKinds"/>):</para>
/// <list type="bullet">
/// <item><b>dependsOn</b>: the target must reach <c>6-completed</c> before this
/// task is workable. An edge with <c>releaseGate=true</c> additionally requires
/// the target's explicit <c>released</c> flag. These edges form a DAG; cycles
/// are rejected on write.</item>
/// <item><b>relatedTo</b>: thematic link, non-blocking.</item>
/// <item><b>blockedBy</b>: this task is currently blocked by the target.</item>
/// <item><b>supersedes</b>: this task replaces an obsolete target.</item>
/// <item><b>followUpOf</b>: this task was raised from the target origin.</item>
/// <item><b>raisedFollowUps</b>: this origin raised the target follow-up.</item>
/// <item><b>workbenches</b>: stable project-scoped document reference keys.</item>
/// </list>
/// </summary>
public record TaskReferences
{
    [JsonPropertyName("dependsOn")]
    public List<TaskDependencyReference> DependsOn { get; init; } = [];
    [JsonPropertyName("relatedTo")]
    public List<string> RelatedTo { get; init; } = [];
    [JsonPropertyName("blockedBy")]
    public List<string> BlockedBy { get; init; } = [];
    [JsonPropertyName("supersedes")]
    public List<string> Supersedes { get; init; } = [];
    [JsonPropertyName("followUpOf")]
    public List<string> FollowUpOf { get; init; } = [];
    [JsonPropertyName("raisedFollowUps")]
    public List<string> RaisedFollowUps { get; init; } = [];
    [JsonPropertyName("workbenches")]
    public List<string> Workbenches { get; init; } = [];

    /// <summary>True when every relation list is empty.</summary>
    public bool IsEmpty =>
        DependsOn.Count == 0 && RelatedTo.Count == 0 &&
        BlockedBy.Count == 0 && Supersedes.Count == 0 && FollowUpOf.Count == 0 &&
        RaisedFollowUps.Count == 0 && Workbenches.Count == 0;

    /// <summary>Flattens task-target lists into (kind, target) pairs, in kind order.</summary>
    public IEnumerable<(string Kind, string Target)> Enumerate()
    {
        foreach (var t in DependsOn) yield return (TaskReferenceKinds.DependsOn, t.Key);
        foreach (var t in RelatedTo) yield return (TaskReferenceKinds.RelatedTo, t);
        foreach (var t in BlockedBy) yield return (TaskReferenceKinds.BlockedBy, t);
        foreach (var t in Supersedes) yield return (TaskReferenceKinds.Supersedes, t);
        foreach (var t in FollowUpOf) yield return (TaskReferenceKinds.FollowUpOf, t);
        foreach (var t in RaisedFollowUps) yield return (TaskReferenceKinds.RaisedFollowUps, t);
    }
}

/// <summary>
/// String constants for the <see cref="TaskReferences"/> relation kinds.
/// Kept as constants (not an enum) so the JSON wire format is the literal
/// camelCase string matching the field names on disk.
/// </summary>
public static class TaskReferenceKinds
{
    public const string DependsOn = "dependsOn";
    public const string RelatedTo = "relatedTo";
    public const string BlockedBy = "blockedBy";
    public const string Supersedes = "supersedes";
    public const string FollowUpOf = "followUpOf";
    public const string RaisedFollowUps = "raisedFollowUps";
    public const string Workbenches = "workbenches";

    public static readonly string[] All = [DependsOn, RelatedTo, BlockedBy, Supersedes, FollowUpOf, RaisedFollowUps, Workbenches];
}

/// <summary>
/// Body for <c>PUT /api/tasks/{id}/references</c>. Replace-all: each supplied
/// list becomes the full set for that relation kind. A null list is treated as
/// empty so a partial body clears the omitted kinds — callers should send the
/// whole desired state. The endpoint validates the result before persisting.
/// </summary>
public record SetTaskReferencesRequest
{
    public List<TaskDependencyReference>? DependsOn { get; init; }
    public List<string>? RelatedTo { get; init; }
    public List<string>? BlockedBy { get; init; }
    public List<string>? Supersedes { get; init; }
    public List<string>? FollowUpOf { get; init; }
    public List<string>? RaisedFollowUps { get; init; }
    public List<string>? Workbenches { get; init; }

    /// <summary>Projects the request into a normalised <see cref="TaskReferences"/>.</summary>
    public TaskReferences ToReferences() => TaskReferenceValidator.Normalize(new TaskReferences
    {
        DependsOn = DependsOn ?? [],
        RelatedTo = RelatedTo ?? [],
        BlockedBy = BlockedBy ?? [],
        Supersedes = Supersedes ?? [],
        FollowUpOf = FollowUpOf ?? [],
        RaisedFollowUps = RaisedFollowUps ?? [],
        Workbenches = Workbenches ?? [],
    });
}

/// <summary>Failure category for a single rejected reference edge.</summary>
public enum TaskReferenceErrorCode
{
    /// <summary>A task referenced its own key.</summary>
    SelfReference,
    /// <summary>The target key does not match any known task.</summary>
    UnknownKey,
    /// <summary>The proposed dependsOn edge closes a cycle (dependsOn must stay a DAG).</summary>
    DependsOnCycle,
    /// <summary>The document reference key does not resolve in the owning project.</summary>
    UnknownWorkbenchKey,
}

/// <summary>One reason a <see cref="SetTaskReferencesRequest"/> was rejected.</summary>
public record TaskReferenceError(
    TaskReferenceErrorCode Code,
    string Kind,
    string Target,
    string Message);

/// <summary>
/// Outcome of <see cref="TaskReferenceValidator.Validate"/>. Splits hard
/// <see cref="Errors"/> (self-reference, dependsOn cycle, unknown document key)
/// that block the write
/// from non-blocking <see cref="Warnings"/> (AGT-2029: an unknown key is
/// allowed because the referenced task may be created later; it surfaces as an
/// open dependency chip instead of a 400).
/// </summary>
public record TaskReferenceValidationResult(
    IReadOnlyList<TaskReferenceError> Errors,
    IReadOnlyList<TaskReferenceError> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public bool HasWarnings => Warnings.Count > 0;

    public static readonly TaskReferenceValidationResult Ok =
        new(Array.Empty<TaskReferenceError>(), Array.Empty<TaskReferenceError>());
}

/// <summary>
/// Pure, dependency-free validation for F34 references. Lives in the Shared
/// library so it is unit-testable without the web host. Rules from the
/// acceptance criteria:
/// <list type="number">
/// <item>no self-reference — hard error (<see cref="TaskReferenceErrorCode.SelfReference"/>);</item>
/// <item>dependsOn stays a DAG — a new edge that closes a cycle is a hard error
/// (<see cref="TaskReferenceErrorCode.DependsOnCycle"/>);</item>
/// <item>referenced keys should exist, but an unknown key is a non-blocking
/// <b>warning</b>, not a hard failure (AGT-2029): the operator may name a
/// waits-on target that is created later. It lands in
/// <see cref="TaskReferenceValidationResult.Warnings"/> and the write still
/// persists (<see cref="TaskReferenceErrorCode.UnknownKey"/>).</item>
/// </list>
/// Cycle detection is O(V+E) DFS over the existing dependsOn graph with the
/// edited task's outgoing edges swapped for the proposed ones.
/// </summary>
public static class TaskReferenceValidator
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Trims a key; empty / whitespace becomes "".</summary>
    public static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "" : key.Trim();

    /// <summary>Trims, drops blanks, and de-duplicates (case-insensitive, first-wins) a key list.</summary>
    public static List<string> NormalizeList(IEnumerable<string>? keys)
    {
        var result = new List<string>();
        if (keys == null) return result;
        var seen = new HashSet<string>(KeyComparer);
        foreach (var k in keys)
        {
            var n = NormalizeKey(k);
            if (n.Length == 0) continue;
            if (seen.Add(n)) result.Add(n);
        }
        return result;
    }

    /// <summary>
    /// Normalises dependency keys and de-duplicates case-insensitively. If the
    /// same key appears more than once, <c>releaseGate=true</c> wins so
    /// normalisation can never silently weaken a gate.
    /// </summary>
    public static List<TaskDependencyReference> NormalizeDependencies(
        IEnumerable<TaskDependencyReference>? dependencies)
    {
        var result = new List<TaskDependencyReference>();
        if (dependencies == null) return result;
        var indexes = new Dictionary<string, int>(KeyComparer);
        foreach (var dependency in dependencies)
        {
            if (dependency == null) continue;
            var key = NormalizeKey(dependency.Key);
            if (key.Length == 0) continue;
            if (!indexes.TryGetValue(key, out var index))
            {
                indexes[key] = result.Count;
                result.Add(new TaskDependencyReference(key, dependency.ReleaseGate));
            }
            else if (dependency.ReleaseGate && !result[index].ReleaseGate)
            {
                result[index] = result[index] with { ReleaseGate = true };
            }
        }
        return result;
    }

    /// <summary>Returns a copy with every list normalised.</summary>
    public static TaskReferences Normalize(TaskReferences refs) => new()
    {
        DependsOn = NormalizeDependencies(refs.DependsOn),
        RelatedTo = NormalizeList(refs.RelatedTo),
        BlockedBy = NormalizeList(refs.BlockedBy),
        Supersedes = NormalizeList(refs.Supersedes),
        FollowUpOf = NormalizeList(refs.FollowUpOf),
        RaisedFollowUps = NormalizeList(refs.RaisedFollowUps),
        Workbenches = NormalizeList(refs.Workbenches),
    };

    /// <summary>
    /// Validates a proposed set of references for the task identified by
    /// <paramref name="selfKey"/>.
    /// </summary>
    /// <param name="selfKey">Stable key of the task being edited (its own F33 key).</param>
    /// <param name="proposed">The replace-all set the caller wants to persist.</param>
    /// <param name="knownKeys">Every valid task key the references may point at.</param>
    /// <param name="dependsOnGraph">
    /// Existing dependsOn edges for all tasks (key → its current dependsOn
    /// targets). The edited task's own entry, if present, is ignored: the
    /// proposed edges are used in its place for cycle detection.
    /// </param>
    public static TaskReferenceValidationResult Validate(
        string selfKey,
        TaskReferences proposed,
        IReadOnlySet<string> knownKeys,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph,
        IReadOnlySet<string>? knownWorkbenchKeys = null)
    {
        var self = NormalizeKey(selfKey);
        var norm = Normalize(proposed);
        var errors = new List<TaskReferenceError>();
        var warnings = new List<TaskReferenceError>();

        foreach (var (kind, target) in norm.Enumerate())
        {
            if (self.Length > 0 && KeyComparer.Equals(target, self))
                errors.Add(new TaskReferenceError(
                    TaskReferenceErrorCode.SelfReference, kind, target,
                    $"A task cannot reference itself ({target})."));
            // AGT-2029: an unknown key is a warning, not a hard failure. The
            // operator may name a waits-on target that does not exist yet (it is
            // created later); persist the edge and surface it as an open
            // dependency chip rather than rejecting the whole write.
            else if (!knownKeys.Contains(target))
                warnings.Add(new TaskReferenceError(
                    TaskReferenceErrorCode.UnknownKey, kind, target,
                    $"Referenced task '{target}' does not exist yet."));
        }

        var knownDocuments = knownWorkbenchKeys
            ?? new HashSet<string>(KeyComparer);
        foreach (var target in norm.Workbenches)
        {
            if (!knownDocuments.Contains(target))
                errors.Add(new TaskReferenceError(
                    TaskReferenceErrorCode.UnknownWorkbenchKey,
                    TaskReferenceKinds.Workbenches,
                    target,
                    $"Document reference '{target}' does not exist in this project."));
        }

        // Only run cycle detection on edges that exist and are not self-edges;
        // self-edges are already reported above and would produce a noisy
        // self->self "cycle".
        var dependsForCycle = norm.DependsOn
            .Select(t => t.Key)
            .Where(t => !KeyComparer.Equals(t, self))
            .ToList();
        var cycle = FindDependsOnCycle(self, dependsForCycle, dependsOnGraph);
        if (cycle != null)
            errors.Add(new TaskReferenceError(
                TaskReferenceErrorCode.DependsOnCycle, TaskReferenceKinds.DependsOn,
                cycle[^1],
                $"dependsOn would create a cycle: {string.Join(" → ", cycle)}."));

        return new TaskReferenceValidationResult(errors, warnings);
    }

    /// <summary>
    /// DFS for a path <c>self → … → self</c> through the dependsOn graph, where
    /// <paramref name="self"/>'s outgoing edges are <paramref name="proposedDependsOn"/>
    /// and every other node uses <paramref name="graph"/>. Returns the cycle
    /// path (starting and ending at self) or null when none exists. Pre-existing
    /// cycles that do not pass through self are skipped so traversal terminates.
    /// </summary>
    private static List<string>? FindDependsOnCycle(
        string self,
        IReadOnlyList<string> proposedDependsOn,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> graph)
    {
        if (self.Length == 0 || proposedDependsOn.Count == 0) return null;

        IEnumerable<string> Edges(string node) =>
            KeyComparer.Equals(node, self)
                ? proposedDependsOn
                : graph.TryGetValue(node, out var e) ? e : Array.Empty<string>();

        var path = new List<string>();
        var onStack = new HashSet<string>(KeyComparer);
        var done = new HashSet<string>(KeyComparer);

        bool Dfs(string node)
        {
            path.Add(node);
            onStack.Add(node);
            foreach (var next in Edges(node))
            {
                if (KeyComparer.Equals(next, self))
                {
                    path.Add(self);
                    return true;
                }
                if (onStack.Contains(next) || done.Contains(next)) continue;
                if (Dfs(next)) return true;
            }
            onStack.Remove(node);
            done.Add(node);
            path.RemoveAt(path.Count - 1);
            return false;
        }

        return Dfs(self) ? new List<string>(path) : null;
    }
}
