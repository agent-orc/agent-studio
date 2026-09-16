using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Persists <see cref="IntegrationBranchGateRecord"/> rows per project under
/// <c>&lt;workspaceRoot&gt;/logs/integration-gate-health/&lt;project&gt;.json</c>
/// (AGT-2819). Static and path-based, like the review decision journal, so tests
/// address it with a temp directory instead of a container.
/// <para>
/// The file exists only to answer "was this gate already red the last time we
/// looked". Losing it costs one repeated alert, never a missed one.
/// </para>
/// </summary>
public static class IntegrationBranchGateHealthStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string Directory(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, "logs", "integration-gate-health");
    }

    public static string File(string workspaceRoot, string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return Path.Combine(Directory(workspaceRoot), Sanitize(project) + ".json");
    }

    public static IReadOnlyList<IntegrationBranchGateRecord> Read(string workspaceRoot, string project)
    {
        var path = File(workspaceRoot, project);
        lock (PathLocks.GetOrAdd(path, static _ => new object()))
        {
            return ReadLocked(path);
        }
    }

    /// <summary>
    /// Applies one observation and returns the policy's decision. Reading and
    /// writing happen under the same per-file lock so two reviews settling at
    /// once cannot both report the same red transition.
    /// </summary>
    public static IntegrationBranchGateDecision Apply(
        string workspaceRoot,
        string project,
        IntegrationBranchGateObservation observation)
    {
        var path = File(workspaceRoot, project);
        lock (PathLocks.GetOrAdd(path, static _ => new object()))
        {
            var rows = ReadLocked(path).ToList();
            var index = rows.FindIndex(row => Matches(row, observation));
            var decision = IntegrationBranchGateHealthPolicy.Decide(
                index < 0 ? null : rows[index],
                observation);
            if (index < 0) rows.Add(decision.Next);
            else rows[index] = decision.Next;
            WriteLocked(path, rows);
            return decision;
        }
    }

    private static bool Matches(
        IntegrationBranchGateRecord row,
        IntegrationBranchGateObservation observation)
        => string.Equals(row.Branch, observation.Branch, StringComparison.OrdinalIgnoreCase)
           && string.Equals(row.StepId, observation.StepId, StringComparison.Ordinal);

    private static IReadOnlyList<IntegrationBranchGateRecord> ReadLocked(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return [];
            var content = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<IntegrationBranchGateRecord>>(content, Json) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable health file must never block a review from settling.
            // Treating it as empty costs one repeated alert.
            SilentCatch.Note(exception, "IntegrationBranchGateHealthStore: read");
            return [];
        }
    }

    private static void WriteLocked(string path, IReadOnlyList<IntegrationBranchGateRecord> rows)
    {
        var directory = Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(rows, Json),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(exception, "IntegrationBranchGateHealthStore: write");
        }
        finally
        {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
    }

    private static string Sanitize(string project)
        => new(project.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray());
}
