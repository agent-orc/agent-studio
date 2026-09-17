namespace AgentTaskboard.UpdateService;

/// <summary>
/// How hard a failed precondition argues against starting the update.
/// </summary>
public enum PreconditionSeverity
{
    /// <summary>
    /// The running instance proves the step can never pass: its endpoint is
    /// absent or closed by configuration. Restarting cannot change that, so
    /// the preflight refuses.
    /// </summary>
    Blocking,

    /// <summary>
    /// The step did not answer the way phase 6 wants, but the update itself
    /// may be the cure (a 5xx, a timeout, a drained-but-slow board) and the
    /// post-restart matrix retries with its own budget. Reported, never
    /// refused.
    /// </summary>
    Advisory
}

/// <summary>
/// What one post-restart verification step needs from the running instance
/// before an update may start. One spec per step in the ADR-0031 phase-6
/// matrix, plus the frontend port check that AGT-2862 added after it.
/// </summary>
public sealed record VerificationPreconditionSpec(
    string Step,
    string Probe,
    string Requirement,
    string Remedy);

/// <summary>
/// What the probe actually saw. <c>Status</c> is an HTTP status code;
/// <see cref="VerificationPreconditionPolicy.NoResponse"/> means the probe got
/// nothing back and <see cref="VerificationPreconditionPolicy.NotEvaluated"/>
/// means the probe was never run because the instance is not serving.
/// </summary>
public sealed record VerificationPreconditionObservation(
    string Step,
    int Status,
    bool Satisfied,
    string Observed);

/// <summary>
/// Per-step verdict on the wire (<c>GET /update/preflight</c>) and in the run
/// folder's <c>release-preflight.json</c>.
/// </summary>
public sealed record VerificationPreconditionResult(
    string Step,
    bool Ok,
    PreconditionSeverity Severity,
    string Probe,
    string Requirement,
    string Observed,
    string? Error);

/// <summary>
/// Pure decision layer for AGT-2865: does the *running* instance already
/// satisfy what the post-restart verification matrix will demand?
///
/// The incident this answers: the db-touch sentinel was gated off on Stable,
/// so phase 6 could not pass there under any circumstances, and the run
/// discovered it only after stopping the stack, restoring, rebuilding and
/// restarting - at which point the failure took the 0.6.0 rollback path under
/// an already-running 0.7.0 backend. The preflight had reported
/// <c>allowed=true</c> because it compared release identities and never asked
/// whether the checks it was going to run could answer at all.
///
/// The severity split is deliberate and load-bearing:
///
/// <list type="bullet">
///   <item>A <see cref="ConfigurationStatuses"/> answer (401/403/404) is proof
///   of a durable configuration problem: the route is absent or the gate is
///   closed. A restart reproduces it exactly, so it blocks.</item>
///   <item>Everything else - no response, 5xx, an unexpected shape - is
///   advisory. An operator updating a backend that is currently broken must
///   not be locked out by the very breakage the update is meant to fix, and
///   phase 6 retries those cases with a real budget (healthz 5x, jobs-grouped
///   up to ~120 s).</item>
/// </list>
///
/// A step is only evaluated while the instance answers <c>/healthz</c>. When
/// it does not, every other step is reported as "not evaluated" rather than
/// guessed at, because an unreachable instance says nothing about the
/// configuration the restarted one will have.
/// </summary>
public static class VerificationPreconditionPolicy
{
    /// <summary>The probe got no answer at all (transport failure or timeout).</summary>
    public const int NoResponse = 0;

    /// <summary>The probe was not run, because the instance is not serving.</summary>
    public const int NotEvaluated = -1;

    /// <summary>
    /// Statuses that prove the endpoint is absent or refused by configuration
    /// rather than momentarily unhappy. These, and only these, refuse a run.
    /// </summary>
    public static readonly IReadOnlyList<int> ConfigurationStatuses = new[] { 401, 403, 404 };

    /// <summary>The frontend port check, which is not part of the six-step matrix.</summary>
    public const string FrontendListeningStep = "frontend-listening";

    /// <summary>
    /// One spec per post-restart step, in the order phase 6 runs them. The
    /// frontend check comes last because it runs after the matrix.
    /// </summary>
    public static readonly IReadOnlyList<VerificationPreconditionSpec> Steps = new[]
    {
        new VerificationPreconditionSpec(
            "healthz-stable",
            "GET /healthz",
            "http=200 with body \"ok\"",
            "healthz-stable needs the Stable backend to be serving; start it with start-stable.sh before triggering an update."),
        new VerificationPreconditionSpec(
            "runner-status",
            "GET /api/runner/status",
            "http=200 with a `projects` map",
            "runner-status needs /api/runner/status to be routable on the running backend."),
        new VerificationPreconditionSpec(
            "jobs-grouped",
            "GET /api/tasks/grouped",
            "http=200 and parseable JSON",
            "jobs-grouped needs /api/tasks/grouped to be routable on the running backend."),
        new VerificationPreconditionSpec(
            "clients",
            "GET /api/clients",
            "http=200 with at least one client",
            "clients needs /api/clients to be routable and the local-default client to exist."),
        new VerificationPreconditionSpec(
            "cli-quota",
            "GET /api/cli/quota",
            "http=200 (a degraded payload is accepted)",
            "cli-quota needs /api/cli/quota to be routable on the running backend."),
        new VerificationPreconditionSpec(
            "db-touch",
            "POST /api/_internal/probe",
            "http=200 and the sentinel echoed back",
            "db-touch needs UpdateService:ProbeEnabled=true in the Stable appsettings.Local.json "
            + "(Environment:IsDev=true and DevTools:UpdateStableEnabled=true also open it); "
            + "the sentinel is open by default once the update contract is installed, "
            + "i.e. once <workspace>/.metadata/stable-approved-tag exists."),
        new VerificationPreconditionSpec(
            FrontendListeningStep,
            "TCP connect to the configured frontend origin",
            "the frontend dev server port accepts a connection",
            "frontend-listening needs the frontend dev server on the configured FrontendUrl; "
            + "a frontend that is down ends a run as `degraded`, not `failed`, so this never refuses a run."),
    };

    /// <summary>Spec for one step, synthesising a neutral one for an unknown step.</summary>
    public static VerificationPreconditionSpec SpecFor(string step) =>
        Steps.FirstOrDefault(s => string.Equals(s.Step, step, StringComparison.Ordinal))
        ?? new VerificationPreconditionSpec(step, "(unknown probe)", "(no declared precondition)", "");

    /// <summary>Turns raw observations into per-step verdicts.</summary>
    public static IReadOnlyList<VerificationPreconditionResult> Evaluate(
        IReadOnlyList<VerificationPreconditionObservation> observations)
        => observations.Select(Evaluate).ToArray();

    /// <summary>The whole decision, for one step.</summary>
    public static VerificationPreconditionResult Evaluate(VerificationPreconditionObservation observation)
    {
        var spec = SpecFor(observation.Step);
        if (observation.Satisfied)
            return new VerificationPreconditionResult(
                spec.Step, true, PreconditionSeverity.Advisory,
                spec.Probe, spec.Requirement, observation.Observed, null);

        var severity = ConfigurationStatuses.Contains(observation.Status)
            ? PreconditionSeverity.Blocking
            : PreconditionSeverity.Advisory;

        var error = $"{spec.Step} precondition failed: {spec.Probe} -> {observation.Observed}."
            + (string.IsNullOrEmpty(spec.Remedy) ? "" : $" {spec.Remedy}");

        return new VerificationPreconditionResult(
            spec.Step, false, severity, spec.Probe, spec.Requirement, observation.Observed, error);
    }

    /// <summary>
    /// The errors that make <c>allowed=false</c>. Advisory failures stay in
    /// the per-step list so the operator still sees them.
    /// </summary>
    public static IReadOnlyList<string> BlockingErrors(
        IReadOnlyList<VerificationPreconditionResult> results)
        => results
            .Where(r => !r.Ok && r.Severity == PreconditionSeverity.Blocking && r.Error is not null)
            .Select(r => r.Error!)
            .ToArray();

    /// <summary>Observation for a step the probe never ran.</summary>
    public static VerificationPreconditionObservation NotEvaluatedObservation(string step, string why) =>
        new(step, NotEvaluated, false, $"not evaluated ({why})");

    /// <summary>Operator-readable rendering of an HTTP outcome.</summary>
    public static string DescribeStatus(int status) => status switch
    {
        NotEvaluated => "not evaluated",
        NoResponse => "no response (timed out or unreachable)",
        _ => $"http={status}",
    };
}
