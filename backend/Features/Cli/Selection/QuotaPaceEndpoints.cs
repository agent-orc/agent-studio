using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Security;

namespace AgentStudio.Cli;

public static class QuotaPaceEndpoints
{
    public sealed record PaceInput(string Mode, JsonElement UseBy = default);
    public sealed record WindowStatus(string CliType, string WindowLabel, QuotaPace Pace,
        DateTime At, double? AllowedPct, double? UsedPct, bool Suspicious);

    public static void MapQuotaPaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/model-selection/policy/pace", (ModelSelectionPolicyStore store, QuotaService quota) =>
            Results.Ok(Windows(store.Get(), quota.GetCached())));

        app.MapPut("/api/model-selection/policy/pace/{cliType}/{windowLabel}", (string cliType, string windowLabel,
            PaceInput input, ModelSelectionPolicyStore store, QuotaService quota, HttpContext context, ControlPlaneAuditLog audit) =>
        {
            if (!CliTypes.IsValid(cliType)) return Results.BadRequest(new { error = "Unknown provider." });
            var report = quota.GetCached();
            var window = Windows(store.Get(), report).FirstOrDefault(row =>
                string.Equals(row.CliType, cliType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.WindowLabel, windowLabel, StringComparison.OrdinalIgnoreCase));
            if (window is null) return Results.BadRequest(new { error = "Unknown quota window." });

            QuotaPace pace;
            if (input.Mode == "useBy" && input.UseBy.ValueKind == JsonValueKind.String
                && input.UseBy.GetString() is { } iso && iso.Contains('T')
                && (iso.EndsWith('Z') || iso.Length >= 6 && iso[^6] is '+' or '-')
                && input.UseBy.TryGetDateTimeOffset(out var deadline))
                pace = new("useBy", deadline.ToUniversalTime());
            else if (input.Mode is "steady" or "unpaced" && input.UseBy.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                pace = new(input.Mode);
            else return Results.BadRequest(new { error = "Choose steady, unpaced, or useBy with an ISO timestamp including its UTC offset." });

            var setting = new WindowPaceSetting(window.CliType, window.WindowLabel, pace);
            var updated = store.SetPace(setting);
            audit.Append(new(ControlPlaneAuditStreams.Operator, OperatorEndpoints.Actor(context),
                "quota-pace-set", "applied", $"{window.CliType}/{window.WindowLabel}", Details: setting));
            return Results.Ok(Windows(updated, report).Single(row => setting.Matches(row.CliType, row.WindowLabel)));
        });
    }

    private static IReadOnlyList<WindowStatus> Windows(ModelSelectionPolicy policy, QuotaReport report)
    {
        var result = new List<WindowStatus>();
        foreach (var cli in CliTypes.All)
        {
            var readings = report.Snapshots.Where(s => s.CliType == cli).OrderByDescending(s => s.FetchedAt)
                .SelectMany(s => s.Windows.Select(w => (Window: w, s.Suspicious)))
                .ToLookup(pair => pair.Window.Label, StringComparer.OrdinalIgnoreCase);
            var caps = policy.Caps.GetValueOrDefault(cli);
            var labels = readings.Select(group => group.Key).Concat(caps?.Keys.AsEnumerable() ?? [])
                .Concat(policy.WindowPaces.Where(p => p.CliType == cli).Select(p => p.WindowLabel))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var label in labels)
            {
                var (window, suspicious) = readings[label].FirstOrDefault();
                var cap = SelectionQuotaMath.EffectiveCap(caps?.GetValueOrDefault(label, QuotaCapDefaults.CapPct)
                    ?? QuotaCapDefaults.CapPct, policy.ShortStepReservePoints);
                var pace = policy.PaceFor(cli, label);
                var allowed = window is null ? null : SelectionQuotaMath.Project(window, report.At, cap, pace)?.AllowedPct;
                result.Add(new(cli, label, pace, report.At, allowed ?? cap, window?.UsedPct, suspicious));
            }
        }
        return result;
    }
}
