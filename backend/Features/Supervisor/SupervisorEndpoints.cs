using System.Text.Json;
using AgentStudio.Registry;
using Microsoft.AspNetCore.Mvc;

namespace AgentStudio.Supervisor;

/// <summary>
/// HTTP surface for the per-project supervisor. Read-only in this first cut:
/// only the observation primitive is wired. Emergency primitives and
/// advisories land in subsequent supervisor tasks.
/// </summary>
public static class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/supervisor");

        group.MapGet("/{project}/observation", async (
            string project,
            ProjectObservationService obs,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var observation = await obs.ObserveAsync(project, ct);
            return Results.Ok(observation);
        });

        group.MapPost("/{project}/intervene/cancel-run", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            TaskScannerService scanner,
            ProjectRegistry registry,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.JobId)) return Results.BadRequest(new { error = "jobId required" });
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            if (!JobInProject(scanner, registry, project, body.JobId!)) return JobNotInProject();
            await svc.CancelRunAsync(project, body.JobId!, body.Reason!, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{project}/intervene/pause-pickup", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            var ttl = body.TtlSeconds.HasValue ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : (TimeSpan?)null;
            await svc.PausePickupAsync(project, body.Reason!, ttl, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{project}/intervene/force-fail", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            TaskScannerService scanner,
            ProjectRegistry registry,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.JobId)) return Results.BadRequest(new { error = "jobId required" });
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            if (!JobInProject(scanner, registry, project, body.JobId!)) return JobNotInProject();
            await svc.ForceFailAsync(project, body.JobId!, body.Reason!, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{project}/intervene/resume", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            await svc.ResumeAsync(project, body.Reason!, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        group.MapGet("/{project}/meta-cycle", (
            string project,
            IConfiguration config,
            int? max) =>
        {
            if (string.IsNullOrWhiteSpace(project)) return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            var enabled = config.GetValue("Supervisor:MetaCycleEnabled", false);
            var defaults = MetaCycleConfig.FromConfiguration(config);
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return Results.Ok(new
                {
                    enabled,
                    config = defaults,
                    reports = Array.Empty<MetaCycleReport>(),
                });
            }
            var dir = SupervisorLogPaths.MetaCycleDir(workspace!, project);
            var cap = Math.Clamp(max ?? 8, 1, 50);
            var reports = ReadMetaCycleReports(dir, cap);
            return Results.Ok(new
            {
                enabled,
                config = defaults,
                reports,
            });
        });

        group.MapGet("/{project}/recent-events", (
            string project,
            IConfiguration config,
            int? max) =>
        {
            if (string.IsNullOrWhiteSpace(project)) return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace)) return Results.Ok(new { advisories = Array.Empty<SupervisorAdvisory>(), interventions = Array.Empty<SupervisorIntervention>() });
            var cap = Math.Clamp(max ?? 50, 1, 500);
            var advisories = ReadJsonl<SupervisorAdvisory>(SupervisorLogPaths.ObservationsFile(workspace!, project), cap);
            var interventions = ReadJsonl<SupervisorIntervention>(SupervisorLogPaths.InterventionsFile(workspace!, project), cap);
            return Results.Ok(new { advisories, interventions });
        });
    }

    /// <summary>
    /// The intervention service stops the run its job id names anywhere in the
    /// workspace, while the route authorizes only its project. A job outside
    /// that project, or an unknown one, is refused before any intervention
    /// record is written.
    /// </summary>
    private static bool JobInProject(TaskScannerService scanner, ProjectRegistry registry, string project, string jobId)
        => scanner.FindJob(jobId) is { } job
           && !string.IsNullOrWhiteSpace(job.ProjectName)
           && ProjectAccessAuthorization.SameProject(job.ProjectName, project, registry);

    private static IResult JobNotInProject()
        => Results.NotFound(new { error = "job-not-in-project", message = "The job is not a card of this project." });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions MetaCycleJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static List<MetaCycleReport> ReadMetaCycleReports(string dir, int max)
    {
        var list = new List<MetaCycleReport>();
        if (!Directory.Exists(dir)) return list;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.json").OrderByDescending(p => p, StringComparer.Ordinal).Take(max);
        }
        catch { return list; }
        foreach (var path in files)
        {
            try
            {
                var raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var report = JsonSerializer.Deserialize<MetaCycleReport>(raw, MetaCycleJsonOptions);
                if (report != null) list.Add(report);
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "SupervisorEndpoints: skip malformed"); /* skip malformed */ }
        }
        return list;
    }

    private static List<T> ReadJsonl<T>(string path, int max)
    {
        if (!File.Exists(path)) return new List<T>();
        var lines = File.ReadAllLines(path);
        var start = Math.Max(0, lines.Length - max);
        var list = new List<T>(lines.Length - start);
        for (int i = start; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(raw, JsonOptions);
                if (item != null) list.Add(item);
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "SupervisorEndpoints: skip malformed line"); /* skip malformed line */ }
        }
        return list;
    }
}

public sealed record InterventionRequest(string? Reason, string? JobId, int? TtlSeconds);
