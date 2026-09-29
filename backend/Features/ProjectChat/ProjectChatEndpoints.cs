using System.Globalization;

namespace AgentStudio.ProjectChat;

/// <summary>
/// Slice D's read surface for project chat: search, single-turn fetch,
/// and pagination by ts cursor. Writes still go through the existing
/// <see cref="RunnerEndpoints"/> chat surface (the migration mirrors
/// legacy turns into the new tree, so reads here see the full history).
/// </summary>
public static class ProjectChatEndpoints
{
    public static void MapProjectChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{project}/chat");

        group.MapGet("/search",
            (string project, string? q, int? limit,
             TaskScannerService scanner,
             ProjectChatIndex index) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == project);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{project}'" });
                if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { project, results = Array.Empty<object>() });

                var n = limit is > 0 ? Math.Min(limit.Value, 100) : 20;
                index.EnsureFresh(entry.Path);
                var hits = index.Search(entry.Path, q!, n);

                return Results.Ok(new
                {
                    project,
                    results = hits.Select(h => new
                    {
                        turnId = h.TurnId,
                        author = h.Author,
                        kind = h.Kind,
                        ts = h.Ts.ToString("o", CultureInfo.InvariantCulture),
                        snippet = h.Snippet,
                        score = h.Score
                    })
                });
            });

        group.MapGet("/turn/{turnId}",
            (string project, string turnId,
             TaskScannerService scanner,
             ProjectChatStore store) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == project);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{project}'" });
                var turn = store.FindById(entry.Path, turnId);
                if (turn == null) return Results.NotFound(new { error = $"Unknown turn '{turnId}'" });
                return Results.Ok(new
                {
                    project,
                    turn = new
                    {
                        turnId = turn.TurnId,
                        author = turn.Author,
                        kind = turn.Kind,
                        ts = turn.Ts.ToString("o", CultureInfo.InvariantCulture),
                        refs = turn.Refs,
                        body = turn.Body,
                        metadata = turn.Metadata
                    }
                });
            });

        group.MapGet("/stats",
            (string project,
             TaskScannerService scanner,
             ProjectChatStore store) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == project);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{project}'" });
                var (total, oldest, newest) = store.Stats(entry.Path);
                return Results.Ok(new
                {
                    project,
                    totalCount = total,
                    oldestTs = oldest?.ToString("o", CultureInfo.InvariantCulture),
                    newestTs = newest?.ToString("o", CultureInfo.InvariantCulture)
                });
            });

        group.MapGet("/scroll",
            (string project, string? before, string? after, int? limit,
             TaskScannerService scanner,
             ProjectChatStore store) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == project);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{project}'" });

                DateTime? beforeTs = ParseCursor(before);
                DateTime? afterTs = ParseCursor(after);
                var n = limit is > 0 ? Math.Min(limit.Value, 200) : 50;

                var page = store.ReadScroll(entry.Path, beforeTs, afterTs, n);
                return Results.Ok(new
                {
                    project,
                    direction = beforeTs.HasValue ? "before" : (afterTs.HasValue ? "after" : "tail"),
                    turns = page.Select(t => new
                    {
                        turnId = t.TurnId,
                        author = t.Author,
                        kind = t.Kind,
                        ts = t.Ts.ToString("o", CultureInfo.InvariantCulture),
                        refs = t.Refs,
                        body = t.Body,
                        metadata = t.Metadata
                    })
                });
            });

        static DateTime? ParseCursor(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return null;
        }
    }
}
