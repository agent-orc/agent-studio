using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Runs the <see cref="VerificationPreconditionPolicy"/> specs against the
/// *running* instance, so a post-restart verification step that cannot pass
/// is found before the stack is stopped rather than after it is back up
/// (AGT-2865).
///
/// The probes are the same requests phase 6 will make, at their cheapest
/// shape: one attempt per step, no retry loops. Retrying here would only
/// duplicate the budgets the verifier already owns, and a transient failure
/// is advisory anyway.
/// </summary>
public sealed class VerificationPreconditionService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The board endpoint walks every job folder, so it gets a wider slice
    /// than the other reads. This is not the post-restart drain budget (that
    /// is the verifier's ~120 s); it only keeps a busy-but-healthy instance
    /// from being reported as unreachable here.
    /// </summary>
    private static readonly TimeSpan JobsGroupedTimeout = TimeSpan.FromSeconds(20);

    private readonly IBackendProbe _backend;
    private readonly UpdateServiceOptions _options;

    public VerificationPreconditionService(IBackendProbe backend, UpdateServiceOptions options)
    {
        _backend = backend;
        _options = options;
    }

    public async Task<IReadOnlyList<VerificationPreconditionResult>> EvaluateAsync(CancellationToken ct)
        => VerificationPreconditionPolicy.Evaluate(await ObserveAsync(ct));

    private async Task<IReadOnlyList<VerificationPreconditionObservation>> ObserveAsync(CancellationToken ct)
    {
        var observations = new List<VerificationPreconditionObservation> { await ObserveHealthzAsync(ct) };
        if (!observations[0].Satisfied)
        {
            // Nothing else can be read honestly: an instance that is not
            // serving says nothing about the configuration the restarted one
            // will have. The matrix owns that question after the restart.
            const string why = "the running backend did not answer /healthz";
            foreach (var spec in VerificationPreconditionPolicy.Steps.Skip(1))
                observations.Add(VerificationPreconditionPolicy.NotEvaluatedObservation(spec.Step, why));
            return observations;
        }

        observations.Add(await ObserveRunnerStatusAsync(ct));
        observations.Add(await ObserveJobsGroupedAsync(ct));
        observations.Add(await ObserveClientsAsync(ct));
        observations.Add(await ObserveCliQuotaAsync(ct));
        observations.Add(await ObserveDbTouchAsync(ct));
        observations.Add(await ObserveFrontendAsync(ct));
        return observations;
    }

    private async Task<VerificationPreconditionObservation> ObserveHealthzAsync(CancellationToken ct)
    {
        var result = await _backend.ProbeHealthzAsync(ct);
        var bodyOk = result.Body != null && result.Body.Trim('"').Trim() == "ok";
        return new VerificationPreconditionObservation(
            "healthz-stable",
            result.HttpStatus,
            result.Ok && bodyOk,
            result.Ok && !bodyOk
                ? $"http={result.HttpStatus} body={Truncate(result.Body)}"
                : VerificationPreconditionPolicy.DescribeStatus(result.HttpStatus));
    }

    private async Task<VerificationPreconditionObservation> ObserveRunnerStatusAsync(CancellationToken ct)
    {
        var (status, body) = await _backend.GetAsync("/api/runner/status", ProbeTimeout, ct);
        if (status != 200) return Http("runner-status", status);
        return HasProperty(body, "projects")
            ? Satisfied("runner-status", "http=200 with `projects`")
            : new VerificationPreconditionObservation("runner-status", status, false, "http=200 without a `projects` key");
    }

    private async Task<VerificationPreconditionObservation> ObserveJobsGroupedAsync(CancellationToken ct)
    {
        var (status, body) = await _backend.GetAsync("/api/tasks/grouped", JobsGroupedTimeout, ct);
        if (status != 200) return Http("jobs-grouped", status);
        return Parses(body)
            ? Satisfied("jobs-grouped", "http=200 and parses")
            : new VerificationPreconditionObservation("jobs-grouped", status, false, "http=200 with a body that does not parse");
    }

    private async Task<VerificationPreconditionObservation> ObserveClientsAsync(CancellationToken ct)
    {
        var (status, body) = await _backend.GetAsync("/api/clients", ProbeTimeout, ct);
        if (status != 200) return Http("clients", status);
        var count = CountClients(body);
        return count >= 1
            ? Satisfied("clients", $"http=200 count={count}")
            : new VerificationPreconditionObservation("clients", status, false,
                count == 0 ? "http=200 count=0" : "http=200 with an unrecognised shape");
    }

    private async Task<VerificationPreconditionObservation> ObserveCliQuotaAsync(CancellationToken ct)
    {
        var (status, _) = await _backend.GetAsync("/api/cli/quota", ProbeTimeout, ct);
        return status == 200 ? Satisfied("cli-quota", "http=200") : Http("cli-quota", status);
    }

    private async Task<VerificationPreconditionObservation> ObserveDbTouchAsync(CancellationToken ct)
    {
        // Same request phase 6 makes. The endpoint is a body echo, so running
        // it against the live instance changes nothing.
        var sentinel = $"preflight-{Guid.NewGuid():N}";
        var (status, body) = await _backend.PostJsonAsync(
            "/api/_internal/probe",
            new { sentinel, preflight = true, ts = DateTime.UtcNow },
            ProbeTimeout, ct);
        if (status != 200) return Http("db-touch", status);
        return !string.IsNullOrEmpty(body) && body.Contains(sentinel, StringComparison.Ordinal)
            ? Satisfied("db-touch", "http=200, sentinel round-tripped")
            : new VerificationPreconditionObservation("db-touch", status, false,
                $"http=200 without the sentinel in the body: {Truncate(body)}");
    }

    private async Task<VerificationPreconditionObservation> ObserveFrontendAsync(CancellationToken ct)
    {
        // One sweep over every loopback spelling (AGT-2862), no waiting: the
        // frontend is either up right now or it is not, and either way this
        // never refuses a run.
        var probe = await FrontendProbe.WaitForAsync(_options.FrontendUrl, TimeSpan.Zero, ct);
        var step = VerificationPreconditionPolicy.FrontendListeningStep;
        return probe.Up
            ? new VerificationPreconditionObservation(step, 200, true,
                probe.ReachedUrl is null ? "not configured; frontend check skipped" : $"reachable at {probe.ReachedUrl}")
            : new VerificationPreconditionObservation(step, VerificationPreconditionPolicy.NoResponse, false,
                $"no loopback origin answered on port {probe.Port}");
    }

    private static VerificationPreconditionObservation Satisfied(string step, string observed) =>
        new(step, 200, true, observed);

    private static VerificationPreconditionObservation Http(string step, int status) =>
        new(step, status, false, VerificationPreconditionPolicy.DescribeStatus(status));

    private static bool HasProperty(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out _);
        }
        catch (JsonException) { return false; }
    }

    private static bool Parses(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Client count, or -1 when the payload is not a shape we know.</summary>
    private static int CountClients(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc.RootElement.GetArrayLength();
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("clients", out var clients)
                && clients.ValueKind == JsonValueKind.Array) return clients.GetArrayLength();
            return -1;
        }
        catch (JsonException) { return -1; }
    }

    private static string Truncate(string? value, int max = 80)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }
}
