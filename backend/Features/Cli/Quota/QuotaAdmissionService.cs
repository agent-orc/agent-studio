using AgentStudio.Projects;

namespace AgentStudio.Cli;

/// <summary>
/// Input for the application-wide quota admission boundary. Execution paths
/// supply their requested route and identity; the service owns the shared
/// quota snapshot, cap, fallback-catalogue, and wait-policy inputs.
/// </summary>
public sealed record QuotaAdmissionRequest(
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    string? ProjectName,
    int OccupiedSlots,
    string ExecutionPath);

/// <summary>
/// Canonical application boundary around <see cref="QuotaAdmissionPlanner"/>.
/// Every CLI launch path uses the same cached quota facts and policy inputs,
/// without probing a provider or mutating the configured route.
/// </summary>
public sealed class QuotaAdmissionService
{
    private readonly QuotaService _quota;
    private readonly CliQuotaCapsService _caps;
    private readonly CliQuotaFallbackService _fallback;
    private readonly CliQuotaWaitPolicyService _waitPolicy;
    private readonly ProjectSettingsService _projectSettings;
    private readonly TimeProvider _timeProvider;
    private readonly AgentStudio.Pipeline.BetterModelCandidateService? _betterCandidates;

    public QuotaAdmissionService(
        QuotaService quota,
        CliQuotaCapsService caps,
        CliQuotaFallbackService fallback,
        CliQuotaWaitPolicyService waitPolicy,
        ProjectSettingsService projectSettings,
        TimeProvider? timeProvider = null,
        AgentStudio.Pipeline.BetterModelCandidateService? betterCandidates = null)
    {
        _quota = quota;
        _caps = caps;
        _fallback = fallback;
        _waitPolicy = waitPolicy;
        _projectSettings = projectSettings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _betterCandidates = betterCandidates;
    }

    public QuotaAdmissionPlan Plan(QuotaAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = string.IsNullOrWhiteSpace(request.ProjectName)
            ? null
            : _projectSettings.Get(request.ProjectName);
        var plan = QuotaAdmissionPlanner.Plan(
            request.CliType,
            request.Model,
            request.ThinkingLevel,
            _fallback,
            _caps,
            cli => string.IsNullOrWhiteSpace(cli) ? null : _quota.GetCachedFor(cli),
            _timeProvider.GetUtcNow().UtcDateTime,
            request.OccupiedSlots,
            _waitPolicy.Resolve(project));
        var candidates = _betterCandidates?.Find(
            plan.Model,
            plan.ThinkingLevel,
            project?.BenchmarkCapabilityClass ?? "CodingAgent",
            _timeProvider.GetUtcNow().UtcDateTime) ?? [];
        return plan with { BetterCandidates = candidates };
    }
}
