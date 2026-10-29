using System.Text.Json;

namespace AgentStudio.Tags;

public static class AutoTagCallPolicy
{
    public const int DefaultHourlyLimit = 60;
    public static readonly TimeSpan FailureDelay = TimeSpan.FromHours(24);

    public static string ContentFingerprint(TagMaintenanceItem item) =>
        TagMaintenancePolicy.Fingerprint(TagMaintenancePolicy.Encode(new { item.Kind, item.Id, item.Title, item.Text }));

    public static TagClassificationPrediction Parse(string response, TagMaintenanceItem item,
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
        throw new InvalidOperationException("Auto-tag response has no valid classification object.");
    }
}

public sealed record AutoTagFailure(string Fingerprint, DateTimeOffset At);
public sealed record AutoTagCallState(List<DateTimeOffset> Calls, Dictionary<string, AutoTagFailure> Failures);

/// <summary>Durable admission for all auto-tag classification calls on this host.</summary>
public sealed class AutoTagCallBudget(IConfiguration configuration, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string PathForState => System.IO.Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required for auto-tag calls."),
        "auto-tag", "call-state.json");

    private AutoTagCallState Read() => File.Exists(PathForState)
        ? JsonSerializer.Deserialize<AutoTagCallState>(File.ReadAllText(PathForState), TagMaintenancePolicy.Json)
            ?? new([], [])
        : new([], []);

    private void Save(AutoTagCallState state) =>
        new AtomicJsonFileWriter().Write(PathForState, TagMaintenancePolicy.Encode(state));

    private static string Key(string project, TagMaintenanceItem item) => project + ":" + item.Kind + ":" + item.Id;

    public bool FailureDue(string project, TagMaintenanceItem item)
    {
        lock (_gate)
        {
            var failure = Read().Failures.GetValueOrDefault(Key(project, item));
            return failure != null && (failure.Fingerprint != AutoTagCallPolicy.ContentFingerprint(item)
                || _clock.GetUtcNow() - failure.At >= AutoTagCallPolicy.FailureDelay);
        }
    }

    public string? Admit(string project, TagMaintenanceItem item)
    {
        lock (_gate)
        {
            var state = Read();
            var now = _clock.GetUtcNow();
            if (state.Failures.TryGetValue(Key(project, item), out var failure)
                && failure.Fingerprint == AutoTagCallPolicy.ContentFingerprint(item)
                && now - failure.At < AutoTagCallPolicy.FailureDelay)
                return "backoff";
            state.Calls.RemoveAll(at => now - at >= TimeSpan.FromHours(1));
            var limit = Math.Clamp(configuration.GetValue("AutoTag:HourlyCallLimit", AutoTagCallPolicy.DefaultHourlyLimit), 1, 1000);
            if (state.Calls.Count >= limit) return "hourly-cap";
            state.Calls.Add(now);
            Save(state);
            return null;
        }
    }

    public void Failed(string project, TagMaintenanceItem item)
    {
        lock (_gate)
        {
            var state = Read();
            state.Failures[Key(project, item)] = new(AutoTagCallPolicy.ContentFingerprint(item), _clock.GetUtcNow());
            Save(state);
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
