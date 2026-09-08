using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Folds finalized Task Server authority with Runner observations. This type
/// deliberately has no outcome classifier, lease writer, or task mutation.
/// </summary>
public sealed class RemoteRunResultCollector(TimeProvider clock)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] RequiredPhases = ["Claim", "Run", "Gate", "Review"];

    public RemoteRunResult Collect(
        TaskServerRemoteRunEvidence server,
        RunnerRemoteRunEvidence runner)
    {
        if (!string.Equals(server.RunId, runner.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Task Server and Runner run ids do not match.");

        var phases = MergePhases(server.Phases, runner.Phases);
        var utcDuration = DurationMs(server.StartedAt, server.FinishedAt);
        var duration = runner.MonotonicWallDurationMs ?? utcDuration;
        EnsureDurationConsistent("scenario", duration, utcDuration);

        var result = new RemoteRunResult(
            RemoteRunResultProtocol.V1,
            server.ScenarioId,
            server.RunId,
            server.Seed,
            server.StartedAt,
            server.FinishedAt,
            duration,
            runner.Components,
            runner.Host,
            runner.Runner,
            phases,
            runner.Tokens,
            runner.Incident,
            server.Outcome,
            server.Assertions,
            server.ChronicleLinks,
            server.Task,
            server.Attempts,
            server.Artifacts.Concat(runner.Artifacts)
                .GroupBy(item => item.EvidenceId, StringComparer.Ordinal)
                .Select(group => group.Single())
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray(),
            server.EvidenceAuthority,
            clock.GetUtcNow().UtcDateTime,
            string.Empty);
        result = result with { ContentSha256 = ComputeDigest(result) };
        RemoteRunResultValidator.Validate(result);
        return result;
    }

    public static string ComputeDigest(RemoteRunResult result)
    {
        var node = JsonSerializer.SerializeToNode(result, Json)?.AsObject()
            ?? throw new InvalidDataException("Could not serialize remote run result.");
        node.Remove("contentSha256");
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node, Json)));
    }

    private static IReadOnlyList<RemoteRunPhaseTiming> MergePhases(
        IReadOnlyList<RemoteRunPhaseTiming> server,
        IReadOnlyList<RemoteRunPhaseTiming> runner)
    {
        var all = server.Concat(runner).ToArray();
        var result = new List<RemoteRunPhaseTiming>();
        foreach (var phaseName in RequiredPhases.Concat(["Integration"]))
        {
            var candidates = all.Where(item => item.Phase == phaseName).ToArray();
            if (candidates.Length == 0)
            {
                if (phaseName == "Integration") continue;
                throw new InvalidDataException($"Required {phaseName} phase evidence is missing.");
            }

            var selected = candidates
                .OrderByDescending(item => item.DurationSource == "Monotonic")
                .ThenByDescending(item => item.FinishedAt)
                .First();
            foreach (var candidate in candidates)
            {
                if (candidate.Status != selected.Status)
                    throw new InvalidDataException($"Conflicting {phaseName} phase status across evidence sources.");
            }
            result.Add(selected);
        }
        return result;
    }

    internal static long DurationMs(DateTime start, DateTime finish)
    {
        if (start.Kind != DateTimeKind.Utc || finish.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("Remote run timestamps must be UTC.");
        if (finish < start) throw new InvalidDataException("Finish timestamp precedes start timestamp.");
        return checked((long)(finish - start).TotalMilliseconds);
    }

    internal static void EnsureDurationConsistent(string name, long measured, long utc)
    {
        if (measured < 0) throw new InvalidDataException($"{name} duration cannot be negative.");
        var tolerance = Math.Max(1000L, (long)Math.Ceiling(utc * 0.05));
        if (Math.Abs(measured - utc) > tolerance)
            throw new InvalidDataException(
                $"{name} monotonic duration {measured}ms is inconsistent with UTC duration {utc}ms.");
    }
}

public static class RemoteRunResultValidator
{
    private static readonly HashSet<string> PhaseNames =
        new(["Claim", "Run", "Gate", "Review", "Integration"], StringComparer.Ordinal);
    private static readonly HashSet<string> PhaseStatuses =
        new(["Executed", "Skipped", "Failed"], StringComparer.Ordinal);
    private static readonly HashSet<string> ActualOutcomes =
        new(["expected-healed", "recovered", "lost"], StringComparer.Ordinal);
    private static readonly HashSet<string> DurationSources =
        new(["Monotonic", "UtcWallClock"], StringComparer.Ordinal);

    public static void Validate(RemoteRunResult result)
    {
        Required(result.SchemaVersion == RemoteRunResultProtocol.V1, "Unsupported remote run result schema.");
        Required(!string.IsNullOrWhiteSpace(result.ScenarioId), "Scenario id is required.");
        Required(!string.IsNullOrWhiteSpace(result.RunId), "Run id is required.");
        Required(result.Seed >= 0, "Seed cannot be negative.");

        var utcDuration = RemoteRunResultCollector.DurationMs(result.StartedAt, result.FinishedAt);
        RemoteRunResultCollector.EnsureDurationConsistent(
            "scenario", result.WallClockDurationMs, utcDuration);

        Required(result.Components.Count > 0, "At least one component version is required.");
        foreach (var component in result.Components)
            Required(!string.IsNullOrWhiteSpace(component.Name)
                     && (!string.IsNullOrWhiteSpace(component.Image)
                         || !string.IsNullOrWhiteSpace(component.Commit)),
                "Each component requires a name and an image or commit version.");

        Required(!string.IsNullOrWhiteSpace(result.Host.Id)
                 && !string.IsNullOrWhiteSpace(result.Runner.Id),
            "Host and runner identities are required.");
        ValidatePhases(result.Phases);
        ValidateTokens(result.Tokens);

        Required(!string.IsNullOrWhiteSpace(result.Incident.Id), "Injected incident id is required.");
        Required(result.Incident.FaultSchedule.All(item =>
                !string.IsNullOrWhiteSpace(item.FaultId)
                && PhaseNames.Contains(item.AtPhase)
                && item.OffsetMs >= 0
                && !string.IsNullOrWhiteSpace(item.Action)),
            "Fault schedule evidence is invalid.");
        Required(ActualOutcomes.Contains(result.Outcome.Actual), "Actual outcome is invalid.");
        Required(!string.IsNullOrWhiteSpace(result.Outcome.Expected), "Expected outcome is required.");
        Required(result.Assertions.Count > 0, "Assertion evidence is required.");
        Required(result.Assertions.All(item => !string.IsNullOrWhiteSpace(item.AssertionId)
                                               && item.EvidenceRefs.Count > 0),
            "Every assertion requires evidence references.");
        Required(result.ChronicleLinks.Count > 0
                 && result.ChronicleLinks.All(link =>
                     link.StartsWith(
                         "docs/operations/haertung-verteilte-ausfuehrung/historie.html#",
                         StringComparison.Ordinal)),
            "A canonical hardening chronicle link is required.");

        Required(!string.IsNullOrWhiteSpace(result.Task.TaskKey)
                 && ValidSha(result.Task.BaseSha)
                 && ValidSha(result.Task.ResultSha)
                 && ValidSha(result.Task.ReviewedSha)
                 && !string.IsNullOrWhiteSpace(result.Task.FinalLane),
            "Task identity, SHAs, and final lane are required.");
        Required(result.Attempts.Count > 0, "At least one authoritative attempt is required.");
        Required(result.Attempts.All(item => item.LeaseFence >= 0
                                             && item.AuthorityEpoch >= 0
                                             && !string.IsNullOrWhiteSpace(item.AttemptId)
                                             && item.Kind is "Run" or "Review"),
            "Attempt authority is invalid.");
        Required(result.EvidenceAuthority.AuthorityEpoch
                 == result.Attempts.Max(item => item.AuthorityEpoch),
            "Evidence authority epoch does not match the attempts.");
        Required(result.EvidenceAuthority.MaxLeaseFence
                 == result.Attempts.Max(item => item.LeaseFence),
            "Evidence max lease fence does not match the attempts.");

        Required(result.Artifacts.Count > 0, "Referenced raw artifacts are required.");
        Required(result.Artifacts.All(item =>
                !string.IsNullOrWhiteSpace(item.EvidenceId)
                && !string.IsNullOrWhiteSpace(item.Uri)
                && item.Source is "TaskServer" or "Runner"),
            "Artifact identity or source is invalid.");
        var artifactIds = result.Artifacts.Select(item => item.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        Required(result.Assertions.SelectMany(item => item.EvidenceRefs).All(artifactIds.Contains),
            "Assertion evidence references an unknown artifact.");
        Required(result.Artifacts.All(item => item.Sha256.Length == 64
                                              && item.Sha256.All(Uri.IsHexDigit)),
            "Artifact digests must be SHA-256.");
        Required(result.CollectedAt.Kind == DateTimeKind.Utc, "Collector timestamp must be UTC.");
        Required(string.Equals(
                result.ContentSha256,
                RemoteRunResultCollector.ComputeDigest(result),
                StringComparison.Ordinal),
            "Remote run result content digest is invalid.");
    }

    private static void ValidatePhases(IReadOnlyList<RemoteRunPhaseTiming> phases)
    {
        foreach (var required in new[] { "Claim", "Run", "Gate", "Review" })
            Required(phases.Count(item => item.Phase == required) == 1,
                $"Exactly one {required} timing record is required.");
        Required(phases.Count(item => item.Phase == "Integration") <= 1,
            "Integration phase may occur at most once.");

        foreach (var phase in phases)
        {
            Required(PhaseNames.Contains(phase.Phase), "Unknown phase.");
            Required(PhaseStatuses.Contains(phase.Status), "Unknown phase status.");
            Required(DurationSources.Contains(phase.DurationSource), "Unknown phase duration source.");
            Required(phase.QueueDurationMs >= 0 && phase.ExecutionDurationMs >= 0,
                "Phase durations cannot be negative.");
            var queueFinish = phase.StartedAt ?? phase.FinishedAt;
            var utcQueue = RemoteRunResultCollector.DurationMs(phase.QueuedAt, queueFinish);
            RemoteRunResultCollector.EnsureDurationConsistent(
                $"{phase.Phase} queue", phase.QueueDurationMs, utcQueue);
            var utcExecution = phase.StartedAt is null
                ? 0
                : RemoteRunResultCollector.DurationMs(phase.StartedAt.Value, phase.FinishedAt);
            RemoteRunResultCollector.EnsureDurationConsistent(
                $"{phase.Phase} execution", phase.ExecutionDurationMs, utcExecution);
            if (phase.Status == "Skipped")
            {
                Required(phase.StartedAt is null && phase.ExecutionDurationMs == 0,
                    "Skipped phases cannot have execution time.");
                Required(!string.IsNullOrWhiteSpace(phase.Reason),
                    "Skipped phases require a reason.");
            }
        }
    }

    private static void ValidateTokens(RemoteRunTokenTelemetry telemetry)
    {
        foreach (var token in new[] { telemetry.Input, telemetry.Output, telemetry.Cached, telemetry.Total }
                     .Concat(telemetry.ByPhase.SelectMany(item =>
                         new[] { item.Input, item.Output, item.Cached, item.Total })))
        {
            if (token.Status == "Available")
                Required(token.Value >= 0 && token.UnavailableReason is null,
                    "Available token telemetry requires a non-negative value only.");
            else if (token.Status == "Unavailable")
                Required(token.Value is null && !string.IsNullOrWhiteSpace(token.UnavailableReason),
                    "Unavailable token telemetry requires an explicit reason and no value.");
            else
                throw new InvalidDataException("Token telemetry status is invalid.");
        }
        Required(telemetry.ByPhase.Select(item => item.Phase).Distinct(StringComparer.Ordinal).Count()
                 == telemetry.ByPhase.Count,
            "Token phase attribution contains duplicates.");
        Required(telemetry.ByPhase.All(item => PhaseNames.Contains(item.Phase)),
            "Token telemetry references an unknown phase.");
    }

    private static bool ValidSha(string value) =>
        value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);

    private static void Required(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

/// <summary>
/// Create-once result storage. Exact duplicates are idempotent. No writer,
/// including one with a later timestamp, can replace a finalized result.
/// </summary>
public sealed class RemoteRunResultStore(string root)
{
    public async Task<RemoteRunResultWriteStatus> WriteAsync(
        RemoteRunResult result,
        CancellationToken cancellationToken = default)
    {
        RemoteRunResultValidator.Validate(result);
        var directory = Path.Combine(root, SafeSegment(result.ScenarioId));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{SafeSegment(result.RunId)}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, RemoteRunResultCollector.Json);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        try
        {
            try
            {
                File.Move(temporary, path, overwrite: false);
                return RemoteRunResultWriteStatus.Created;
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<RemoteRunResult>(
                    await File.ReadAllBytesAsync(path, cancellationToken),
                    RemoteRunResultCollector.Json)
                    ?? throw new InvalidDataException("Stored remote run result is unreadable.");
                RemoteRunResultValidator.Validate(existing);
                if (string.Equals(existing.ContentSha256, result.ContentSha256, StringComparison.Ordinal))
                    return RemoteRunResultWriteStatus.IdempotentReplay;

                var stale = result.EvidenceAuthority.AuthorityEpoch < existing.EvidenceAuthority.AuthorityEpoch
                            || (result.EvidenceAuthority.AuthorityEpoch
                                == existing.EvidenceAuthority.AuthorityEpoch
                                && result.EvidenceAuthority.MaxLeaseFence
                                < existing.EvidenceAuthority.MaxLeaseFence);
                throw new InvalidOperationException(stale
                    ? "A stale remote run result writer cannot overwrite the immutable result."
                    : "A different result already exists for this scenario run.");
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Scenario and run ids must be safe path segments.");
        return value;
    }
}

public static class RemoteRunResultMigration
{
    public static RemoteRunResult ReadAndMigrate(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("Remote run result JSON must be an object.");
        var version = node["schemaVersion"]?.GetValue<string>();
        if (version == RemoteRunResultProtocol.V0)
        {
            node["schemaVersion"] = RemoteRunResultProtocol.V1;
            node["wallClockDurationMs"] = node["durationMs"]?.DeepClone()
                ?? throw new InvalidDataException("v0 result has no durationMs.");
            node.Remove("durationMs");
            node["tokens"] = UnavailableTelemetryNode("legacy-v0-did-not-record-token-telemetry");
            node["contentSha256"] = string.Empty;
        }
        else if (version != RemoteRunResultProtocol.V1)
        {
            throw new InvalidDataException($"Unsupported remote run result schema '{version}'.");
        }

        var candidate = node.Deserialize<RemoteRunResult>(RemoteRunResultCollector.Json)
            ?? throw new InvalidDataException("Remote run result could not be deserialized.");
        if (version == RemoteRunResultProtocol.V0)
            candidate = candidate with
            {
                ContentSha256 = RemoteRunResultCollector.ComputeDigest(candidate),
            };
        RemoteRunResultValidator.Validate(candidate);
        return candidate;
    }

    private static JsonObject UnavailableTelemetryNode(string reason)
    {
        JsonObject Missing() => new()
        {
            ["status"] = "Unavailable",
            ["unavailableReason"] = reason,
        };
        return new JsonObject
        {
            ["input"] = Missing(),
            ["output"] = Missing(),
            ["cached"] = Missing(),
            ["total"] = Missing(),
            ["byPhase"] = new JsonArray(),
        };
    }
}
