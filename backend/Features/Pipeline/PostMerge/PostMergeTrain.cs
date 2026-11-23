using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

/// <summary>One reviewed delivery waiting for integration, in queue order.</summary>
public sealed record PostMergeTrainMember(string JobId, string JobFolderPath, string DeliverySha);

/// <summary>What the train decided for one member.</summary>
public static class PostMergeTrainStatuses
{
    /// <summary>A green compile of a tree that contains this merge covers it; its own gate reuses that verdict.</summary>
    public const string Covered = "covered";
    /// <summary>Adding this card turned the compile red; its own compile-only gate runs and reports it.</summary>
    public const string CompileRed = "compile-red";
    /// <summary>The virtual merge conflicted; the card keeps the normal conflict path.</summary>
    public const string Conflict = "conflict";
    /// <summary>The delivery or its merge could not be laid out; the card compiles on its own.</summary>
    public const string Unresolved = "unresolved";
}

/// <summary>One compile the train ran, on the tip that holds <see cref="JobIds"/>.</summary>
public sealed record PostMergeTrainCompile(string Sha, IReadOnlyList<string> JobIds, bool Green, string Reason);

/// <summary>The train's decision for one member, stored beside the card.</summary>
public sealed record PostMergeTrainEntry
{
    public required string TrainId { get; init; }
    public required string JobId { get; init; }
    public required string DeliverySha { get; init; }
    public required string Status { get; init; }
    public required int Position { get; init; }
    public required IReadOnlyList<string> Members { get; init; }
    public required string BaseSha { get; init; }
    /// <summary>The tree this card's own integration merge must reproduce to reuse <see cref="CompiledSha"/>.</summary>
    public string? MergedTreeSha { get; init; }
    /// <summary>The train tip whose green compile covers this card.</summary>
    public string? CompiledSha { get; init; }
    public string Reason { get; init; } = "";
    public int CompileCount { get; init; }
    public DateTimeOffset PlannedAtUtc { get; init; }
}

/// <summary>The outcome of one train: an entry per member and every compile it ran.</summary>
public sealed record PostMergeTrainPlan(string TrainId, string BaseSha,
    IReadOnlyList<PostMergeTrainEntry> Entries, IReadOnlyList<PostMergeTrainCompile> Compiles);

/// <summary>
/// AGT-4020: the combined compile of every reviewed delivery that waits for
/// integration in a fast project. The members are merged in queue order, each
/// with its own merge commit, and the tip is compiled once. When that compile
/// is red the members are added one at a time, each compile on the last green
/// tip, until the card that turns the tree red is found; it is left out and
/// the search continues with the rest. No test command runs for any of it.
/// </summary>
public static class PostMergeTrainSearch
{
    public delegate (string? Sha, string? TreeSha, bool Conflict, string? Error) MergeStep(
        string baseSha, PostMergeTrainMember member);

    public delegate Task<(bool Green, string Reason)> CompileStep(string sha, IReadOnlyList<string> jobIds);

    public static async Task<PostMergeTrainPlan> RunAsync(
        string trainId,
        string baseSha,
        IReadOnlyList<PostMergeTrainMember> members,
        MergeStep merge,
        CompileStep compile,
        DateTimeOffset plannedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(members);
        var ids = members.Select(member => member.JobId).ToArray();
        var decided = new Dictionary<string, (string Status, string? Tree, string? Compiled, string Reason)>(StringComparer.Ordinal);
        var compiles = new List<PostMergeTrainCompile>();

        // Combined tree: every member in order on the current integration tip.
        var tip = baseSha;
        var chain = new List<(PostMergeTrainMember Member, string Tree)>();
        foreach (var member in members)
        {
            var step = merge(tip, member);
            if (step.Sha is null || step.TreeSha is null)
            {
                decided[member.JobId] = step.Conflict
                    ? (PostMergeTrainStatuses.Conflict, null, null, "the delivery conflicts with the cards in front of it")
                    : (PostMergeTrainStatuses.Unresolved, null, null, step.Error ?? "the merge could not be laid out");
                continue;
            }
            chain.Add((member, step.TreeSha));
            tip = step.Sha;
        }

        if (chain.Count > 0)
        {
            var chainIds = chain.Select(item => item.Member.JobId).ToArray();
            var combined = await compile(tip, chainIds).ConfigureAwait(false);
            compiles.Add(new PostMergeTrainCompile(tip, chainIds, combined.Green, combined.Reason));
            if (combined.Green)
            {
                foreach (var (member, tree) in chain)
                    decided[member.JobId] = (PostMergeTrainStatuses.Covered, tree, tip,
                        $"compiled with {chainIds.Length} card(s) in one pass");
            }
            else
            {
                // One at a time: the first card whose addition turns the
                // compile red is the culprit; it is skipped and the rest are
                // added on the last green tip.
                var greenTip = baseSha;
                var greenIds = new List<string>();
                foreach (var (member, _) in chain)
                {
                    var step = merge(greenTip, member);
                    if (step.Sha is null || step.TreeSha is null)
                    {
                        decided[member.JobId] = step.Conflict
                            ? (PostMergeTrainStatuses.Conflict, null, null, "the delivery conflicts once a red card is left out")
                            : (PostMergeTrainStatuses.Unresolved, null, null, step.Error ?? "the merge could not be laid out");
                        continue;
                    }
                    var withMember = greenIds.Append(member.JobId).ToArray();
                    var single = await compile(step.Sha, withMember).ConfigureAwait(false);
                    compiles.Add(new PostMergeTrainCompile(step.Sha, withMember, single.Green, single.Reason));
                    if (single.Green)
                    {
                        decided[member.JobId] = (PostMergeTrainStatuses.Covered, step.TreeSha, step.Sha,
                            $"compiled green when added one at a time after a red combined compile");
                        greenTip = step.Sha;
                        greenIds.Add(member.JobId);
                    }
                    else
                    {
                        decided[member.JobId] = (PostMergeTrainStatuses.CompileRed, step.TreeSha, null,
                            $"the compile turned red when this card was added: {single.Reason}");
                    }
                }
            }
        }

        var entries = members.Select((member, index) =>
        {
            var decision = decided[member.JobId];
            return new PostMergeTrainEntry
            {
                TrainId = trainId,
                JobId = member.JobId,
                DeliverySha = member.DeliverySha,
                Status = decision.Status,
                Position = index + 1,
                Members = ids,
                BaseSha = baseSha,
                MergedTreeSha = decision.Tree,
                CompiledSha = decision.Compiled,
                Reason = decision.Reason,
                CompileCount = compiles.Count,
                PlannedAtUtc = plannedAtUtc,
            };
        }).ToArray();
        return new PostMergeTrainPlan(trainId, baseSha, entries, compiles);
    }
}

/// <summary>The train entry beside a card (<c>post-merge-train.json</c>).</summary>
public static class PostMergeTrainEntryStore
{
    public const string FileName = "post-merge-train.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string jobFolderPath, PostMergeTrainEntry entry)
    {
        try
        {
            if (!Directory.Exists(jobFolderPath)) return;
            var path = Path.Combine(jobFolderPath, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "PostMergeTrainEntryStore: a missing train entry only costs the card its own compile");
        }
    }

    public static PostMergeTrainEntry? Read(string jobFolderPath)
    {
        try
        {
            var path = Path.Combine(jobFolderPath, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<PostMergeTrainEntry>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "PostMergeTrainEntryStore: an unreadable train entry only costs the card its own compile");
            return null;
        }
    }
}

/// <summary>
/// Which T3 merge-window run covers an integrated card (<c>post-merge-window-coverage.json</c>).
/// At integration time the run does not exist yet; the record names the window
/// it falls in: the first window run after <see cref="WindowFromSha"/>, the
/// last completed window tip when the card merged.
/// </summary>
public sealed record PostMergeWindowCoverage
{
    public required string JobId { get; init; }
    public required string MergedSha { get; init; }
    public required string Strategy { get; init; }
    /// <summary><c>affected</c> (fast) or <c>routine</c> (balanced).</summary>
    public required string WindowSuites { get; init; }
    /// <summary>Last completed window tip when the card merged; null before the first window run.</summary>
    public string? WindowFromSha { get; init; }
    /// <summary>The pre-merge level the integration gate ran: compile-only or work-package.</summary>
    public required string PreMergeLevel { get; init; }
    public string? TrainId { get; init; }
    public string? CompiledSha { get; init; }
    public required string CoveredBy { get; init; }
    public DateTimeOffset RecordedAtUtc { get; init; }

    public const string FileName = "post-merge-window-coverage.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Describe(string? windowFromSha, string windowSuites)
        => windowFromSha is null
            ? $"the first merge-window run on this branch ({windowSuites} suites after the base suite)"
            : $"the next merge-window run after {windowFromSha[..Math.Min(12, windowFromSha.Length)]} ({windowSuites} suites after the base suite)";

    /// <summary>
    /// The run that tested the card, once one exists: the earliest window run
    /// whose tip contains <see cref="MergedSha"/>. <paramref name="contains"/>
    /// answers ancestry (merged sha, run tip).
    /// </summary>
    public AgentStudio.TaskServer.Contracts.PostMergeRunRecord? CoveringRun(
        AgentStudio.TaskServer.Contracts.PostMergeProjectState state, Func<string, string, bool> contains)
        => state.Runs
            .Where(run => string.Equals(run.Kind, "window", StringComparison.OrdinalIgnoreCase)
                && run.Status is "green" or "red" or "running")
            .OrderBy(run => run.StartedAtUtc)
            .FirstOrDefault(run => contains(MergedSha, run.TipSha));

    public static void Write(string jobFolderPath, PostMergeWindowCoverage coverage)
    {
        try
        {
            if (!Directory.Exists(jobFolderPath)) return;
            File.WriteAllText(Path.Combine(jobFolderPath, FileName), JsonSerializer.Serialize(coverage, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "PostMergeWindowCoverage: the coverage note is evidence, never an authority");
        }
    }

    public static PostMergeWindowCoverage? Read(string jobFolderPath)
    {
        try
        {
            var path = Path.Combine(jobFolderPath, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<PostMergeWindowCoverage>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "PostMergeWindowCoverage: an unreadable coverage note reads as absent");
            return null;
        }
    }
}
