using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Watcher;

/// <summary>
/// Bus adapter for the typed triggers of dossier section 2. Every typed failure
/// the platform already publishes becomes a failure signal with a normalised
/// fingerprint, which is what gives the repetition rule its history without the
/// Watcher running a competing timer.
/// </summary>
/// <remarks>
/// <para>
/// Producer-owned thresholds stay producer-owned. This source consumes the
/// signal a producer emitted; it never re-derives one. It reads a bounded
/// window of the bus and never the whole history.
/// </para>
/// <para>
/// The Watcher's own events are excluded, so a finding can never become the
/// evidence for a second finding about itself.
/// </para>
/// </remarks>
public sealed partial class WatcherBusSignalSource : IWatcherSignalSource
{
    /// <summary>How far back one sweep reads. Bounded so a long-lived bus stays cheap.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>Upper bound on messages read per project scope in one sweep.</summary>
    public const int MessageLimit = 2_000;

    /// <summary>
    /// Advisory topics that carry a producer-owned problem rather than routine
    /// chatter. Anything outside this set is left to its own producer.
    /// </summary>
    private static readonly HashSet<string> AdvisoryTopics = new(StringComparer.Ordinal)
    {
        "accepted-not-integrated",
        "claim-starvation",
        "worktree-blocked",
        "unverified-delivery",
        "integration-failed",
        "escalation-opened",
        "managed-repo-push-failed",
        "codex-silent-completion",
    };

    private readonly AgentMessageBusStore _store;
    private readonly WatcherStore _watcherStore;
    private readonly TaskScannerService _scanner;

    public WatcherBusSignalSource(
        AgentMessageBusStore store,
        WatcherStore watcherStore,
        TaskScannerService scanner)
    {
        _store = store;
        _watcherStore = watcherStore;
        _scanner = scanner;
    }

    public string Name => "bus";

    public Task<WatcherSweepInput> CollectAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var root = _watcherStore.WorkspaceRoot;
        if (root is null) return Task.FromResult(new WatcherSweepInput { NowUtc = nowUtc });

        var scopes = new List<string?> { null };
        scopes.AddRange(_scanner.GetWatchPaths().Select(entry => (string?)entry.Name));

        var failures = new List<WatcherFailureSignal>();
        var query = new AgentMessageQuery(Since: nowUtc - Window, Limit: MessageLimit);

        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var message in _store.Query(root, scope, query, ct))
            {
                if (string.Equals(message.ParticipantId, WatcherBusPublisher.ParticipantId, StringComparison.Ordinal))
                    continue;
                var signal = ToFailure(message, scope);
                if (signal is not null) failures.Add(signal);
            }
        }

        return Task.FromResult(new WatcherSweepInput { NowUtc = nowUtc, Failures = failures });
    }

    private static WatcherFailureSignal? ToFailure(AgentMessage message, string? project)
    {
        var topic = message.Topic ?? "untopiced";
        var admitted = message.Kind switch
        {
            // Typed errors are always a producer-declared failure.
            "error" => true,
            // Advisories are noisy by design; only the problem topics count.
            "advisory" => AdvisoryTopics.Contains(topic),
            _ => false,
        };
        if (!admitted) return null;

        var summary = message.Summary ?? topic;
        return new WatcherFailureSignal(
            $"bus:{topic}",
            message.JobId ?? project ?? AgentMessageBusPaths.WorkspaceScope,
            Fingerprint(topic, summary),
            message.CreatedAt,
            summary)
        {
            Project = message.Project ?? project,
            AffectedCards = CardsFor(message),
            DependsOnTool = DependsOnTool(message),
            Evidence =
            [
                new WatcherEvidenceItem("Bus message", message.Id, $"bus:{topic}"),
                new WatcherEvidenceItem("Severity", message.Severity ?? "Info", $"bus:{topic}"),
            ],
        };
    }

    private static List<string> CardsFor(AgentMessage message) =>
        string.IsNullOrWhiteSpace(message.JobId) ? [] : [message.JobId!];

    /// <summary>
    /// Tool the failing producer declared a dependency on, when its payload
    /// names one. Without this the drift rule cannot tie a failure to a version
    /// change, so a producer that knows its toolchain should say so.
    /// </summary>
    private static string? DependsOnTool(AgentMessage message)
    {
        if (message.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object) return null;
        return payload.TryGetProperty("dependsOnTool", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Normalise a failure summary so a varying timestamp, id, path suffix, or
    /// duration does not defeat the repetition count. Two occurrences of the
    /// same fault must produce the same fingerprint.
    /// </summary>
    internal static string Fingerprint(string topic, string summary)
    {
        var normalized = HexOrDigits().Replace(summary, "#");
        normalized = Whitespace().Replace(normalized, " ").Trim().ToLowerInvariant();
        if (normalized.Length > 160) normalized = normalized[..160];
        return $"{topic}: {normalized}";
    }

    // Collapses shas, ids, counts, durations, and timestamps into one token.
    [GeneratedRegex(@"[0-9a-f]{7,}|\d+", RegexOptions.IgnoreCase)]
    private static partial Regex HexOrDigits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
