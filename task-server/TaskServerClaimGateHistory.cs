using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private const string ClaimGateClosedAction = "runner.claim-load-gate.closed";
    private const string ClaimGateOpenedAction = "runner.claim-load-gate.opened";

    /// <summary>
    /// Claim gate transitions recorded when a runner's advertised gate state
    /// changes, newest first. They are the same transitions the runner reports
    /// as <c>claim-stopped</c> and <c>claim-resumed</c> invariant reports, but
    /// the audit row carries load, threshold and duration as typed fields.
    /// </summary>
    public async Task<IReadOnlyList<ClaimGateEventDto>> ListClaimGateEventsAsync(
        int days,
        CancellationToken ct)
    {
        var cutoff = UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT sequence, occurred_at, action, target_id, detail_json
              FROM audit
             WHERE action IN ($closed, $opened)
               AND occurred_at >= $cutoff
             ORDER BY sequence DESC
             LIMIT 200;
            """,
            ("$closed", ClaimGateClosedAction),
            ("$opened", ClaimGateOpenedAction),
            ("$cutoff", Iso(cutoff)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ClaimGateEventDto>();
        while (await reader.ReadAsync(ct))
        {
            var stopped = reader.GetString(2) == ClaimGateClosedAction;
            using var detail = JsonDocument.Parse(reader.GetString(4));
            var root = detail.RootElement;
            result.Add(new ClaimGateEventDto(
                reader.GetInt64(0),
                Parse(reader.GetString(1)),
                ClaimGateString(root, "runnerId") ?? reader.GetString(3),
                ClaimGateString(root, "hostId"),
                stopped ? "claim-stopped" : "claim-resumed",
                ClaimGateNumber(root, "loadPerCore"),
                ClaimGateNumber(root, "threshold"),
                (long)ClaimGateNumber(root, stopped ? "sustainedSeconds" : "closedSeconds"),
                (int)ClaimGateNumber(root, "activeSlots")));
        }
        return result;
    }

    // The transition detail is an anonymous object, so its member names mix
    // camel and Pascal case; read them case-insensitively.
    private static JsonElement? ClaimGateProperty(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static string? ClaimGateString(JsonElement root, string name)
        => ClaimGateProperty(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double ClaimGateNumber(JsonElement root, string name)
        => ClaimGateProperty(root, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : 0;
}
