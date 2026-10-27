using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Project-scoped T3 authority. Writes are atomic and run IDs fence late completions.</summary>
public sealed class PostMergeRunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly object _gate = new();

    public PostMergeRunStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "agentstudio", "post-merge");
    }

    public PostMergeProjectState Read(string projectId)
    {
        lock (_gate) return ReadLocked(projectId);
    }

    public PostMergeProjectState? Record(string projectId, PostMergeRunRecord run)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            if (state.Runs.FirstOrDefault(item => item.Id == run.Id) is { } recorded)
                return recorded.TipSha == run.TipSha && recorded.Kind == run.Kind
                    && recorded.Status == run.Status ? state : null;
            var active = state.ActiveRun;
            if (run.Status == "running")
            {
                if (active?.Id == run.Id)
                    return active.TipSha == run.TipSha && active.Kind == run.Kind ? state : null;
                if (active is not null && active.StartedAtUtc.AddHours(
                        active.Kind == "nightly" ? 14 : 4) > run.StartedAtUtc)
                    return null;
                if (active is not null)
                    state = state with { Runs = [.. state.Runs.TakeLast(510), active with
                    {
                        Status = "red", FinishedAtUtc = run.StartedAtUtc,
                        Reason = "runner claim expired before a terminal receipt",
                    }] };
                state = state with { DevelopState = "running", ActiveRun = run };
            }
            else if (run.Status == "skipped")
            {
                if (active is not null
                    || !string.Equals(run.TipSha, state.LastGreenSha, StringComparison.OrdinalIgnoreCase))
                    return null;
                state = state with { DevelopState = "green",
                    Runs = [.. state.Runs.TakeLast(511), run] };
            }
            else if (run.Status is "green" or "red")
            {
                if (active is null || active.Id != run.Id || active.TipSha != run.TipSha
                    || active.Kind != run.Kind || run.FinishedAtUtc is null
                    || run.FinishedAtUtc < run.StartedAtUtc
                    || run.Verdicts.Count == 0
                    || run.Status == "green" && (run.Verdicts.Count < run.Commands.Count
                        || run.Verdicts.Any(verdict => verdict.ExitCode != 0)))
                    return null;
                state = state with
                {
                    DevelopState = run.Status,
                    AttributionEscalatedTaskId = run.Status == "green"
                        ? null : state.AttributionEscalatedTaskId,
                    LastGreenSha = run.Status == "green" ? run.TipSha : state.LastGreenSha,
                    LastWindowTipSha = run.Kind == "window" || run.Status == "green"
                        ? run.TipSha : state.LastWindowTipSha,
                    ActiveRun = null,
                    Runs = [.. state.Runs.TakeLast(511), run],
                };
            }
            else return null;
            WriteLocked(projectId, state);
            return state;
        }
    }

    /// <summary>Records the repair card after its escalation move succeeds.</summary>
    public void RecordAttributionEscalation(string projectId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("An escalated repair card ID is required.", nameof(taskId));
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            WriteLocked(projectId, state with { AttributionEscalatedTaskId = taskId });
        }
    }

    private PostMergeProjectState ReadLocked(string projectId)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path)) return PostMergeProjectState.Empty(projectId);
        return JsonSerializer.Deserialize<PostMergeProjectState>(File.ReadAllText(path), Json)
            ?? throw new IOException($"Post-merge state for {projectId} is empty.");
    }

    private void WriteLocked(string projectId, PostMergeProjectState state)
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
