using System.Text.Json;
using AgentStudio.Shared;

namespace AgentStudio.Tags;

public static class AutoTagCallPolicy
{
    public const int DefaultHourlyLimit = 60;
    public static readonly TimeSpan FailureDelay = TimeSpan.FromHours(24);

    public static string ContentFingerprint(TagMaintenanceItem item) =>
        TagMaintenancePolicy.Fingerprint(TagMaintenancePolicy.Encode(new { item.Kind, item.Id, item.Title, item.Text }));

    public static TagClassificationPrediction? Parse(string response, TagMaintenanceItem item,
        IReadOnlySet<string> allowed, IReadOnlySet<string> areas)
    {
        // A model may add prose or a Markdown fence. Scan object starts and let the
        // JSON reader find the matching end, including braces inside strings.
        for (var start = response.IndexOf('{'); start >= 0; start = response.IndexOf('{', start + 1))
        {
            try
            {
                var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(response[start..]));
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var properties = doc.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 4
                    || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 4
                    || !doc.RootElement.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                    || !doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !doc.RootElement.TryGetProperty("confidence", out var confidence)
                        || confidence.ValueKind != JsonValueKind.Number
                    || !doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array
                    || tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String)) continue;
                var prediction = doc.RootElement.Deserialize<TagClassificationPrediction>(TagMaintenancePolicy.Json);
                if (prediction != null && AutoTaggingPolicy.IsValid(prediction, item, allowed, areas))
                    return prediction;
            }
            catch (JsonException) { continue; }
        }
        return null;
    }
}

public sealed record AutoTagFailure(string Fingerprint, DateTimeOffset At,
    AutoTagOutcome Outcome = AutoTagOutcome.TransientFailure, AutoTagRoute? RequestedRoute = null,
    AutoTagRoute? EffectiveRoute = null, OrchestratorTokenUsage? Usage = null);

public sealed record AutoTagCallState(List<DateTimeOffset> Calls, Dictionary<string, AutoTagFailure> Failures)
{
    public Dictionary<string, AutoTagFailure> Quarantines { get; init; } = new();
}

public enum AutoTagDenial { Backoff, Quarantined, HourlyCap }

public sealed class AutoTagAdmissionDeniedException(AutoTagDenial reason) : Exception(reason.ToString())
{
    public AutoTagDenial Reason { get; } = reason;
}

/// <summary>Durable admission for all auto-tag classification calls on this host.</summary>
public sealed class AutoTagCallBudget(IConfiguration configuration, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private AutoTagCallState? _unsavedFailure;
    private string PathForState => System.IO.Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required for auto-tag calls."),
        "auto-tag", "call-state.json");

    private AutoTagCallState Read()
    {
        // A failed outcome write must be retried before this process can admit more work.
        if (_unsavedFailure is { } pending)
        {
            Save(pending);
            _unsavedFailure = null;
        }
        return File.Exists(PathForState)
            ? JsonSerializer.Deserialize<AutoTagCallState>(File.ReadAllText(PathForState), TagMaintenancePolicy.Json)
                ?? new([], [])
            : new([], []);
    }

    private void Save(AutoTagCallState state) =>
        new AtomicJsonFileWriter().Write(PathForState, TagMaintenancePolicy.Encode(state));

    private static string Key(string project, TagMaintenanceItem item) => project + ":" + item.Kind + ":" + item.Id;

    private static string QuarantineKey(string key, string fingerprint, AutoTagRoute route) =>
        TagMaintenancePolicy.Fingerprint(TagMaintenancePolicy.Encode(new { key, fingerprint, route }));

    private static bool Deterministic(AutoTagOutcome outcome) => outcome is AutoTagOutcome.PromptLimit
        or AutoTagOutcome.OutputTokenLimit or AutoTagOutcome.TurnLimit
        or AutoTagOutcome.OutputSizeLimit or AutoTagOutcome.InvalidClassification;

    private static bool SameRequest(AutoTagFailure failure, AutoTagAttempt attempt) =>
        failure.Fingerprint == attempt.Fingerprint
        && failure.RequestedRoute?.Cli == attempt.RequestedRoute.Cli
        && failure.RequestedRoute?.Model == attempt.RequestedRoute.Model;

    public IReadOnlySet<string> DueFailedKeys(string project, IEnumerable<TagMaintenanceItem> items,
        Func<TagMaintenanceItem, AutoTagAttempt?> prepare)
    {
        Dictionary<string, AutoTagFailure> failures;
        lock (_gate) failures = Read().Failures;
        return items.Where(item =>
            failures.TryGetValue(Key(project, item), out var failure)
            && (!Deterministic(failure.Outcome) && _clock.GetUtcNow() - failure.At >= AutoTagCallPolicy.FailureDelay
                || (failure.RequestedRoute == null
                    ? failure.Fingerprint != AutoTagCallPolicy.ContentFingerprint(item)
                    : prepare(item) is { } attempt && !SameRequest(failure, attempt))))
            .Select(item => item.Kind + ":" + item.Id).ToHashSet(StringComparer.Ordinal);
    }

    public bool HasQuarantinedEscalation(string project, TagMaintenanceItem item, AutoTagAttempt attempt)
    {
        lock (_gate)
        {
            var failure = Read().Failures.GetValueOrDefault(Key(project, item));
            return failure?.RequestedRoute?.ThinkingLevel == "high"
                && Deterministic(failure.Outcome) && SameRequest(failure, attempt);
        }
    }

    public void CheckBackoff(string project, TagMaintenanceItem item, AutoTagAttempt? attempt)
    {
        lock (_gate)
        {
            var failure = Read().Failures.GetValueOrDefault(Key(project, item));
            if (failure == null) return;
            if (failure.Outcome == AutoTagOutcome.PromptLimit && attempt != null && SameRequest(failure, attempt))
                throw new AutoTagAdmissionDeniedException(AutoTagDenial.Quarantined);
            if (!Deterministic(failure.Outcome)
                && (failure.RequestedRoute == null
                    ? failure.Fingerprint == AutoTagCallPolicy.ContentFingerprint(item)
                    : attempt != null && SameRequest(failure, attempt))
                && _clock.GetUtcNow() - failure.At < AutoTagCallPolicy.FailureDelay)
                throw new AutoTagAdmissionDeniedException(AutoTagDenial.Backoff);
        }
    }

    public void Admit(string project, TagMaintenanceItem item, AutoTagAttempt attempt, AutoTagRoute effectiveRoute)
    {
        lock (_gate)
        {
            var state = Read();
            var now = _clock.GetUtcNow();
            var key = Key(project, item);
            if (state.Quarantines.ContainsKey(QuarantineKey(key, attempt.Fingerprint, effectiveRoute)))
                throw new AutoTagAdmissionDeniedException(AutoTagDenial.Quarantined);
            state.Calls.RemoveAll(at => now - at >= TimeSpan.FromHours(1));
            var limit = Math.Clamp(configuration.GetValue("AutoTag:HourlyCallLimit", AutoTagCallPolicy.DefaultHourlyLimit), 1, 1000);
            if (state.Calls.Count >= limit)
                throw new AutoTagAdmissionDeniedException(AutoTagDenial.HourlyCap);
            state.Calls.Add(now);
            Save(state);
        }
    }

    public void Failed(string project, TagMaintenanceItem item, AutoTagAttempt? attempt,
        AutoTagRoute? effectiveRoute, AutoTagValidation validation)
    {
        lock (_gate)
        {
            var state = Read();
            var key = Key(project, item);
            var fingerprint = attempt?.Fingerprint ?? AutoTagCallPolicy.ContentFingerprint(item);
            var failure = new AutoTagFailure(fingerprint, _clock.GetUtcNow(), validation.Outcome,
                attempt?.RequestedRoute, effectiveRoute, validation.Usage);
            state.Failures[key] = failure;
            if (effectiveRoute != null && Deterministic(failure.Outcome))
                state.Quarantines[QuarantineKey(key, fingerprint, effectiveRoute)] = failure;
            _unsavedFailure = state;
            Save(state);
            _unsavedFailure = null;
        }
    }

    public void Succeeded(string project, TagMaintenanceItem item)
    {
        lock (_gate)
        {
            var state = Read();
            if (state.Failures.Remove(Key(project, item))) Save(state);
        }
    }
}
