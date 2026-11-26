using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public sealed record StartTestQuarantineRequest(string Test, string Card, string Kind, string IncidentFingerprint,
    IReadOnlyList<string>? CoveringFolders = null, string? Reason = null, string? RunId = null, string? SuiteId = null)
{
    public string? AttributedRevertCardId { get; init; }
    public IReadOnlyList<string> OwnerExemptCardIds { get; init; } = [];
}
public sealed record LiftTestQuarantineRequest(IReadOnlyList<string>? EvidenceRunIds = null);

public static class TestQuarantineEndpoints
{
    public static void MapTestQuarantineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{name}/test-quarantines", (string name, ProjectRegistry registry,
            TestQuarantineStore store, PostMergeIncidentService incidents) =>
        {
            var project = Find(registry, name);
            if (project is null) return Results.NotFound();
            return Results.Ok(store.Read(project.Id).Entries
                .Concat(incidents.ActiveQuarantines(project).Where(entry => entry.Source == "repository")).ToArray());
        }).RequirePermission(IamPermissions.ProjectRead);
        app.MapPost("/api/projects/{name}/test-quarantines/start", (string name, StartTestQuarantineRequest request,
            HttpContext context, ProjectRegistry registry, TestQuarantineStore store, PostMergeRunStore runs,
            PostMergeIncidentService incidents, TaskScannerService scanner, TimeProvider clock) =>
        {
            var project = Find(registry, name);
            if (project is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Test) || request.Test.Length > 256
                || string.IsNullOrWhiteSpace(request.Card) || request.Card.Length > 128
                || string.IsNullOrWhiteSpace(request.IncidentFingerprint) || request.IncidentFingerprint.Length > 64
                || request.CoveringFolders?.Count > 64 || request.CoveringFolders?.Any(path => path.Length > 512) == true
                || request.Reason?.Length > 280 || request.OwnerExemptCardIds.Count > 64
                || request.OwnerExemptCardIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
                || request.AttributedRevertCardId?.Length > 128)
                return Results.BadRequest(new { error = "Invalid quarantine entry." });
            var state = runs.Read(project.Id);
            var receipt = state.Runs.LastOrDefault(run => run.Id == request.RunId);
            if (!Owner(context))
            {
                // System requests apply the persisted receipt's rules. A client cannot
                // select a class, invent coverage or turn a base failure into a hold.
                if (receipt?.TestVerdicts?.Any(test => test.Test == request.Test) != true)
                    return Results.BadRequest(new { error = "A persisted per-test receipt is required." });
                incidents.Apply(project, receipt);
                var applied = store.Read(project.Id).Entries.LastOrDefault(entry => entry.Test == request.Test
                    && entry.IncidentFingerprint == request.IncidentFingerprint);
                return applied is null ? Results.Conflict(new { error = "The triage decision did not start a quarantine." })
                    : Results.Ok(applied);
            }
            if (scanner.FindJob(request.Card, project.StorageLocation) is null)
                return Results.BadRequest(new { error = "An owning card in this project is required." });
            var baseTest = state.Runs.SelectMany(run => run.TestVerdicts ?? []).Any(test => test.Test == request.Test
                && test.SuiteCategory == "base");
            var now = clock.GetUtcNow();
            var expires = now + (request.Kind == "held" ? AgentStudio.OrchestrationDecisions.RedTestTriageBounds.HeldDuration
                : AgentStudio.OrchestrationDecisions.RedTestTriageBounds.ReportOnlyDuration);
            return Mutate(() => store.Start(project.Id, incidents.PrepareQuarantine(project, new(Guid.NewGuid().ToString("N"), request.Test, request.Card,
                DateOnly.FromDateTime(expires.UtcDateTime), null, request.Reason, request.Kind,
                Actor(context), now, expires, request.IncidentFingerprint,
                request.RunId is null ? [] : [request.RunId], request.CoveringFolders ?? []) { SuiteId = request.SuiteId, AttributedRevertCardId = request.AttributedRevertCardId,
                    OwnerExemptCardIds = request.OwnerExemptCardIds }),
                incidents.ActiveQuarantines(project), baseTest, owner: true), store, project.Id, "Start", now);
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
        app.MapPost("/api/projects/{name}/test-quarantines/{id}/renew", (string name, string id,
            HttpContext context, ProjectRegistry registry, TestQuarantineStore store,
            TaskScannerService scanner, TimeProvider clock) =>
        {
            var project = Find(registry, name);
            if (project is null) return Results.NotFound();
            var entry = store.Read(project.Id).Entries.FirstOrDefault(entry => entry.Id == id);
            if (entry is null) return Results.NotFound();
            var lane = scanner.FindJob(entry.Card, project.StorageLocation)?.State;
            return Mutate(() => store.Renew(project.Id, id, Actor(context),
                Owner(context), lane, clock.GetUtcNow()), store, project.Id, "Renew", clock.GetUtcNow());
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
        app.MapPost("/api/projects/{name}/test-quarantines/{id}/lift", (string name, string id,
            LiftTestQuarantineRequest request, HttpContext context, ProjectRegistry registry,
            TestQuarantineStore store, PostMergeIncidentService incidents, TimeProvider clock) =>
        {
            var project = Find(registry, name);
            if (project is null) return Results.NotFound();
            var entry = store.Read(project.Id).Entries.FirstOrDefault(entry => entry.Id == id);
            if (entry is null) return Results.NotFound();
            var covering = incidents.CoveringRuns(project, entry).TakeLast(2).ToArray();
            var evidence = covering.Select(item => item.RunId).ToArray();
            if (!Owner(context) && (request.EvidenceRunIds is null || !request.EvidenceRunIds.SequenceEqual(evidence)))
                return Results.Conflict(new { error = "Evidence must name the last two covering receipts." });
            return Mutate(() =>
            {
                var lifted = store.Lift(project.Id, id, Actor(context),
                    Owner(context), covering.Select(item => item.Fact).ToArray(), evidence, clock.GetUtcNow());
                incidents.CloseLifted(project, lifted);
                return lifted;
            }, store, project.Id, "Lift", clock.GetUtcNow());
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
    }

    private static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal runner
            ? "system:post-merge/" + runner.RunnerId : AccessSecurityMiddleware.ActorId(context, "owner");

    internal static bool Owner(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            ? human.User.Role == StudioRoles.Owner
            : context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is null
                && context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is null;

    private static ProjectRecord? Find(ProjectRegistry registry, string name)
        => registry.FindByIdOrDisplayName(name) ?? registry.FindByShortCode(name);
    private static IResult Mutate(Func<ProjectTestQuarantine> action, TestQuarantineStore store,
        string projectId, string operation, DateTimeOffset now)
    {
        try
        {
            var entry = action();
            store.Log(projectId, new($"api:{entry.Id}:{operation}:{entry.Renewals}", "TestQuarantineLifecyclePolicy", "rt-4.v1",
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entry))), "Owner", null, [operation], [],
                "Two consecutive covering passes containing the repair, or owner lift.",
                $"Owner requested quarantine {operation.ToLowerInvariant()}.", "api", "", entry.Test,
                entry.IncidentFingerprint, operation == "Lift" ? entry.LiftedBy! : store.Read(projectId).Audit.Last().Actor, now));
            return Results.Ok(entry);
        }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}
