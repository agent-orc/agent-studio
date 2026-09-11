using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Shared;

namespace AgentStudio.Watcher;

/// <summary>
/// Silence and Drift detectors (§10.2) wired to the CLI quota probe cache -
/// the real signal behind the AGT-2705/AGT-2706 "quota probes stale for 8h /
/// 30h" findings of §10.1. One probe covers both classes because they share
/// the same source: <see cref="QuotaSnapshot.ProbeFailedAt"/> for silence
/// (an expected refresh stopped arriving) and
/// <see cref="QuotaSnapshot.CliVersion"/> for drift (the tool changed and the
/// probe kept failing afterward). CLI quota is workspace-wide, not
/// project-scoped, so observations use the <see cref="WorkspaceProject"/>
/// sentinel rather than a registered project name.
/// </summary>
public sealed class QuotaProbeSignalProbe : IWatcherSignalProbe
{
    /// <summary>Sentinel project for workspace-wide (not per-project) findings, matching the dossier's "project: null" workspace health events.</summary>
    public const string WorkspaceProject = "_workspace";

    public string Name => "quota-probe-silence-drift";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly QuotaService _quota;
    private readonly IConfiguration _configuration;

    public QuotaProbeSignalProbe(QuotaService quota, IConfiguration configuration)
    {
        _quota = quota;
        _configuration = configuration;
    }

    public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc)
    {
        var staleHours = _configuration.GetValue("Watcher:QuotaProbeStaleHours", 1.0);
        var report = _quota.GetCached();
        var prior = ReadPriorState(workspaceRoot);
        var next = new Dictionary<string, PriorProbeState>(StringComparer.OrdinalIgnoreCase);
        var results = new List<WatcherSignalObservation>();

        foreach (var snapshot in report.Snapshots)
        {
            next[snapshot.CliType] = new PriorProbeState(snapshot.CliVersion, snapshot.Error);
            prior.TryGetValue(snapshot.CliType, out var previous);

            if (snapshot.ProbeFailedAt is { } failedAt && (nowUtc - failedAt).TotalHours >= staleHours)
            {
                results.Add(new WatcherSignalObservation
                {
                    DetectorClass = WatcherDetectorClasses.Silence,
                    Project = WorkspaceProject,
                    FingerprintKey = $"quota-probe:{snapshot.CliType}",
                    Summary = $"Quota probe for '{snapshot.CliType}' has been failing since {failedAt:u} ({(nowUtc - failedAt).TotalHours:F1}h): {snapshot.Error}",
                    ObservedAtUtc = nowUtc,
                    Details = new Dictionary<string, string>
                    {
                        ["cliType"] = snapshot.CliType,
                        ["probeFailedAtUtc"] = failedAt.ToString("O"),
                        ["error"] = snapshot.Error ?? "(no error text)",
                    },
                });

                if (previous != null
                    && !string.Equals(previous.CliVersion, snapshot.CliVersion, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(previous.CliVersion))
                {
                    results.Add(new WatcherSignalObservation
                    {
                        DetectorClass = WatcherDetectorClasses.Drift,
                        Project = WorkspaceProject,
                        FingerprintKey = $"quota-probe-drift:{snapshot.CliType}",
                        Summary = $"'{snapshot.CliType}' CLI version changed from {previous.CliVersion} to {snapshot.CliVersion}, and its quota probe has failed ever since.",
                        ObservedAtUtc = nowUtc,
                        Details = new Dictionary<string, string>
                        {
                            ["cliType"] = snapshot.CliType,
                            ["previousVersion"] = previous.CliVersion ?? "(unknown)",
                            ["currentVersion"] = snapshot.CliVersion ?? "(unknown)",
                            ["error"] = snapshot.Error ?? "(no error text)",
                        },
                    });
                }
            }
        }

        WritePriorState(workspaceRoot, next);
        return results;
    }

    private sealed record PriorProbeState(string? CliVersion, string? Error);

    private static string StatePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "logs", "watcher", "quota-probe-state.json");

    private static Dictionary<string, PriorProbeState> ReadPriorState(string workspaceRoot)
    {
        var path = StatePath(workspaceRoot);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, PriorProbeState>>(File.ReadAllText(path), JsonOpts)
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "QuotaProbeSignalProbe: unreadable prior drift-baseline state, starting fresh");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WritePriorState(string workspaceRoot, Dictionary<string, PriorProbeState> state)
    {
        try
        {
            var path = StatePath(workspaceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "QuotaProbeSignalProbe: best-effort drift-baseline persist failed");
        }
    }
}
