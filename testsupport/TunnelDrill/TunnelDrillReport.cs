using System.Text;
using System.Text.Json;

namespace AgentStudio.TestSupport.TunnelDrill;

/// <summary>
/// One correlated row of the AGT-2937 tunnel-loss drill (Dossier AGT-W65 D9).
/// Every time in a row is synthetic drill time from a controlled clock; the
/// report never claims to reproduce the operator-reported 2026-09-24/25
/// incident, for which no dated log exists in the repository.
/// </summary>
public sealed record TunnelDrillAttempt(
    string Scenario,
    string Kind,
    string AttemptId,
    long Fence,
    long AuthorityEpoch,
    DateTime? GrantedExpiryUtc,
    DateTime? StopBeforeUtc,
    DateTime? WorkerTeardownUtc,
    string? QuarantineRef,
    string? QuarantineSha,
    string? QuarantinePushStatus,
    string? OutboxIdempotencyKey,
    string FinalReceipt,
    string TerminalState);

public sealed record TunnelDrillOutage(
    string Scenario,
    DateTime InterruptedAtUtc,
    DateTime? RestoredAtUtc,
    string Route);

/// <summary>
/// Collects drill rows and raw evidence lines and writes one correlated report
/// when <c>TUNNEL_DRILL_REPORT_DIR</c> is set, re-rendering the
/// Markdown report after each row. Without the variable the drill
/// still asserts every acceptance rule; it only skips the report files.
/// </summary>
public static class TunnelDrillReport
{
    public const string DirectoryVariable = "TUNNEL_DRILL_REPORT_DIR";
    public const string TimeLabel = "synthetic-drill-time";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Record(
        string suite,
        TunnelDrillOutage outage,
        IReadOnlyList<TunnelDrillAttempt> attempts,
        IReadOnlyList<string> rawEvidence)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            var entry = new
            {
                suite,
                timeLabel = TimeLabel,
                outage,
                attempts,
                rawEvidence,
            };
            var line = JsonSerializer.Serialize(entry, Json);
            File.AppendAllText(Path.Combine(directory, "tunnel-drill.jsonl"), line + "\n", Utf8);
            File.WriteAllText(Path.Combine(directory, "tunnel-drill-report.md"), Render(directory), Utf8);
        }
    }

    /// <summary>Renders the JSON-lines evidence as one Markdown report next to it.</summary>
    public static string Render(string directory)
    {
        var source = Path.Combine(directory, "tunnel-drill.jsonl");
        var builder = new StringBuilder()
            .AppendLine("# Tunnel-loss drill report (AGT-2937, Dossier AGT-W65 D9)")
            .AppendLine()
            .AppendLine($"All times are `{TimeLabel}` from controlled clocks. They are not historical incident data.")
            .AppendLine()
            .AppendLine("| Suite | Scenario | Outage | Kind | Attempt | Fence/Epoch | Granted expiry | Stop-before | Teardown | Quarantine ref / SHA / push | Outbox key | Receipt | Terminal |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        if (!File.Exists(source)) return builder.AppendLine().AppendLine("No drill rows were recorded.").ToString();
        foreach (var line in File.ReadLines(source).Where(item => item.Length > 0))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var outage = root.GetProperty("outage");
            var window = $"{Text(outage, "interruptedAtUtc")} .. {Text(outage, "restoredAtUtc")}";
            foreach (var attempt in root.GetProperty("attempts").EnumerateArray())
            {
                builder.AppendLine(string.Join(" | ",
                    "",
                    Text(root, "suite"),
                    Text(attempt, "scenario"),
                    window,
                    Text(attempt, "kind"),
                    Text(attempt, "attemptId"),
                    $"{Text(attempt, "fence")}/{Text(attempt, "authorityEpoch")}",
                    Text(attempt, "grantedExpiryUtc"),
                    Text(attempt, "stopBeforeUtc"),
                    Text(attempt, "workerTeardownUtc"),
                    $"{Text(attempt, "quarantineRef")} / {Text(attempt, "quarantineSha")} / {Text(attempt, "quarantinePushStatus")}",
                    Text(attempt, "outboxIdempotencyKey"),
                    Text(attempt, "finalReceipt"),
                    Text(attempt, "terminalState"),
                    "").Trim());
            }
        }
        builder.AppendLine().AppendLine("Raw evidence per scenario is retained in `tunnel-drill.jsonl`.");
        return builder.ToString();
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value.ToString().Replace("|", "\\|", StringComparison.Ordinal)
            : "-";
}
