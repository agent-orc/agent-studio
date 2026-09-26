using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

public sealed record TaskCoreLookup(TaskCoreRecord? Record, bool Warming);

/// <summary>
/// Immutable per-task facts, materialized by the task index or a durable writer.
/// No sidecar is opened by the core endpoint.
/// </summary>
public sealed record TaskCoreRecord
{
    public string Id { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public string? Key { get; init; }
    public string WatchPath { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string? ArchiveState { get; init; }
    public DateTime EnteredLaneAt { get; init; }
    public string? Phase { get; init; }
    public DateTime? PhaseEnteredAt { get; init; }
    public int Order { get; init; }
    public string Mode { get; init; } = "coding";
    public string Kind { get; init; } = "task";
    public string TaskType { get; init; } = "chore";
    public bool Released { get; init; }
    public bool PendingIntent { get; init; }
    public string? Model { get; init; }
    public bool ModelExplicit { get; init; }
    public string? ThinkingLevel { get; init; }
    public bool ThinkingLevelExplicit { get; init; }
    public string? CliType { get; init; }
    public string? ContextMode { get; init; }
    public bool? UseOwnSession { get; init; }
    public bool AllowWebAccess { get; init; }
    public bool NoBranchExpected { get; init; }
    public string? BlockerType { get; init; }
    public string? BlockerCondition { get; init; }
    public string? BlockerStatus { get; init; }
    public string? BlockerDescription { get; init; }
    public TaskCoreOutcomeIssue? OutcomeIssue { get; init; }
    public string? NeedsInput { get; init; }
    public bool DependencyBlocked { get; init; }
    public string DependencyState { get; init; } = "ready";
    public IReadOnlyList<TaskCoreDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> BlockedBy { get; init; } = [];
    public TaskCoreText Status { get; init; } = TaskCoreText.Missing;
    public TaskCoreText Prompt { get; init; } = TaskCoreText.Missing;
    public TaskCoreTimeline Timeline { get; init; } = TaskCoreTimeline.Missing;
    internal TaskCoreSourceStamp StatusStamp { get; init; }
    internal TaskCoreSourceStamp PromptStamp { get; init; }
    internal TaskCoreSourceStamp TimelineStamp { get; init; }
    public long Version { get; init; }

    public static TaskCoreRecord Create(TaskInfo info, WaitsOnStatus? waitsOn = null,
        TaskCoreRecord? previous = null, bool forceSidecars = false)
    {
        var dir = info.FolderPath;
        var statusPath = Path.Combine(dir, "status.md");
        var promptPath = Path.Combine(dir, "prompt.md");
        var timelinePath = TaskPaths.TimelineLog(dir);
        var statusStamp = TaskCoreSourceStamp.Read(statusPath);
        var promptStamp = TaskCoreSourceStamp.Read(promptPath);
        var timelineStamp = TaskCoreSourceStamp.Read(timelinePath);
        var reuse = !forceSidecars && previous?.FolderPath == dir;
        var status = reuse && previous!.Status.State != "stale" && statusStamp == previous.StatusStamp
            ? previous.Status : TaskCoreText.Read(statusPath, 1024);
        var prompt = reuse && previous!.Prompt.State != "stale" && promptStamp == previous.PromptStamp
            ? previous.Prompt : TaskCoreText.Read(promptPath, 2048);
        var timeline = reuse && previous!.Timeline.State != "stale" && timelineStamp == previous.TimelineStamp
            ? previous.Timeline : TaskCoreTimeline.Read(timelinePath);
        var record = new TaskCoreRecord
        {
            Id = Limit(info.Id, 128)!, TaskKey = Limit(info.TaskKey, 256)!, Key = Limit(info.Key, 128),
            WatchPath = info.WatchPath, FolderPath = info.FolderPath,
            ProjectName = Limit(info.ProjectName, 128)!,
            Title = Limit(info.Title, 1024)!, State = info.State,
            ArchiveState = info.ArchiveState, EnteredLaneAt = info.EnteredLaneAt,
            Phase = Limit(info.Phase, 80), PhaseEnteredAt = info.PhaseEnteredAt,
            Order = info.Order, Mode = info.Mode, Kind = info.Kind, TaskType = info.TaskType,
            Released = info.Released, PendingIntent = info.PendingIntent is not null,
            Model = Limit(info.Model, 128), ModelExplicit = info.ModelExplicit,
            ThinkingLevel = Limit(info.ThinkingLevel, 64), ThinkingLevelExplicit = info.ThinkingLevelExplicit,
            CliType = Limit(info.CliType, 64), ContextMode = Limit(info.ContextMode, 64),
            UseOwnSession = info.UseOwnSession, AllowWebAccess = info.AllowWebAccess,
            NoBranchExpected = info.NoBranchExpected,
            BlockerType = Limit(info.ParkedBlocker?.BlockerType, 128),
            BlockerCondition = Limit(info.ParkedBlocker?.ConditionKind, 128),
            BlockerStatus = Limit(info.ParkedBlocker?.RecallStatus, 128),
            BlockerDescription = Limit(info.ParkedBlocker?.ConditionDescription, 256),
            OutcomeIssue = info.OutcomeIssue is { } issue
                ? new TaskCoreOutcomeIssue(Limit(issue.Kind, 80)!,
                    Limit(issue.Severity, 40)!, Limit(issue.Label, 80)!,
                    Limit(issue.Summary, 256)!) : null,
            NeedsInput = Limit(info.NeedsInput?.FirstLine, 256),
            DependencyBlocked = waitsOn?.Blocked == true,
            DependencyState = info.References.DependsOn.Count > 0 && waitsOn is null ? "warming" : "ready",
            Dependencies = waitsOn?.Items.Take(16).Select(item => new TaskCoreDependency(
                Limit(item.Key, 80)!, item.Resolved, item.Fulfilled,
                item.ReleaseGate, item.WaitingForRelease, item.Unsatisfiable)).ToArray() ?? [],
            DependsOn = info.References.DependsOn.Take(16).Select(edge => Limit(edge.Key, 80)!).ToArray(),
            BlockedBy = info.References.BlockedBy.Take(16).Select(key => Limit(key, 80)!).ToArray(),
            Status = status, Prompt = prompt, Timeline = timeline,
            StatusStamp = statusStamp, PromptStamp = promptStamp, TimelineStamp = timelineStamp,
        };
        // Hash every published field, including dependency flags and display
        // heads. Identical task facts keep the same validator across a sweep.
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record));
        return record with { Version = BitConverter.ToInt64(hash, 0) };
    }

    internal static string? Limit(string? value, int maxBytes)
    {
        if (value is null) return null;
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}

internal readonly record struct TaskCoreSourceStamp(long Bytes, long WriteTicks)
{
    public static TaskCoreSourceStamp Read(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new TaskCoreSourceStamp(file.Length, file.LastWriteTimeUtc.Ticks) : new(-1, 0);
    }
}

public sealed record TaskCoreDependency(string Key, bool Resolved, bool Fulfilled,
    bool ReleaseGate, bool WaitingForRelease, bool Unsatisfiable);

public sealed record TaskCoreOutcomeIssue(string Kind, string Severity, string Label, string Summary);

public sealed record TaskCoreText(string State, string? Text, long OriginalBytes, string? Hash, string? Cursor)
{
    public static readonly TaskCoreText Missing = new("missing", null, 0, null, null);

    public static TaskCoreText Read(string path, int limit)
    {
        try
        {
            if (!File.Exists(path)) return Missing;
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var prefix = new MemoryStream(limit + 4);
            var buffer = new byte[8192];
            long originalBytes = 0;
            int read;
            while ((read = file.Read(buffer)) > 0)
            {
                digest.AppendData(buffer.AsSpan(0, read));
                if (prefix.Length < limit + 4)
                    prefix.Write(buffer, 0, Math.Min(read, limit + 4 - (int)prefix.Length));
                originalBytes += read;
            }
            var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var text = Encoding.UTF8.GetString(prefix.ToArray());
            var head = TaskCoreRecord.Limit(text, limit)!;
            return new TaskCoreText("ready", head, originalBytes, hash,
                originalBytes > Encoding.UTF8.GetByteCount(head) ? Encoding.UTF8.GetByteCount(head).ToString() : null);
        }
        catch (IOException) { return new TaskCoreText("stale", null, 0, null, null); }
        catch (UnauthorizedAccessException) { return new TaskCoreText("stale", null, 0, null, null); }
    }
}

public sealed record TaskCoreEvent(long Sequence, DateTime Ts, string Kind, string Actor, string? RunId, string Summary);

public sealed record TaskCoreTimeline(string State, IReadOnlyList<TaskCoreEvent> Events,
    long OriginalBytes, string? Hash, string? Cursor)
{
    public static readonly TaskCoreTimeline Missing = new("missing", [], 0, null, null);

    public static TaskCoreTimeline Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return Missing;
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var originalBytes = file.Length;
            var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            var recent = new Queue<TaskCoreEvent>();
            long sequence = 0;
            foreach (var line in File.ReadLines(path))
            {
                sequence++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<TimelineEvent>(line, new JsonSerializerOptions
                    { PropertyNameCaseInsensitive = true });
                    if (evt is null) continue;
                    recent.Enqueue(new TaskCoreEvent(sequence, evt.Ts,
                        TaskCoreRecord.Limit(evt.Kind, 32)!, TaskCoreRecord.Limit(evt.Actor, 32)!,
                        TaskCoreRecord.Limit(evt.RunId, 48), TaskCoreRecord.Limit(evt.Summary, 96)!));
                    if (recent.Count > 5) recent.Dequeue();
                }
                catch (JsonException ex) { SilentCatch.Note(ex, "Task core: skip torn timeline row."); }
            }
            var events = recent.ToArray();
            // Preserve all five events. Shorten their display summaries before
            // sacrificing history, leaving room for the cursor and link in the
            // 2 KiB timeline envelope.
            var summaryLimit = 96;
            while (JsonSerializer.SerializeToUtf8Bytes(events).Length > 1250 && summaryLimit > 0)
            {
                summaryLimit /= 2;
                events = events.Select(evt => evt with
                { Summary = TaskCoreRecord.Limit(evt.Summary, summaryLimit)! }).ToArray();
            }
            if (JsonSerializer.SerializeToUtf8Bytes(events).Length > 1250)
                events = events.Select(evt => evt with
                {
                    Kind = TaskCoreRecord.Limit(evt.Kind, 8)!,
                    Actor = TaskCoreRecord.Limit(evt.Actor, 8)!,
                    RunId = TaskCoreRecord.Limit(evt.RunId, 8),
                }).ToArray();
            if (JsonSerializer.SerializeToUtf8Bytes(events).Length > 1250)
                events = events.Select(evt => evt with
                {
                    Kind = TaskCoreRecord.Limit(evt.Kind, 4)!,
                    Actor = TaskCoreRecord.Limit(evt.Actor, 4)!,
                    RunId = null,
                }).ToArray();
            return new TaskCoreTimeline("ready", events, originalBytes, hash,
                events.Length > 0 && events[0].Sequence > 1 ? (events[0].Sequence - 1).ToString() : null);
        }
        catch (IOException) { return new TaskCoreTimeline("stale", [], 0, null, null); }
        catch (UnauthorizedAccessException) { return new TaskCoreTimeline("stale", [], 0, null, null); }
    }
}
