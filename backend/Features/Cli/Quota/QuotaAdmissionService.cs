using AgentStudio.Projects;

namespace AgentStudio.Cli;

/// <summary>Input shared by coding, review, pipeline, one-shot, and chat admission.</summary>
public sealed record QuotaAdmissionRequest(
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    string? ProjectName,
    int OccupiedSlots,
    QuotaExpectedCostClass ExpectedCost,
    string ExecutionPath);

/// <summary>
/// Canonical application boundary around <see cref="QuotaAdmissionPlanner"/>.
/// Every execution path reads the same cached provider snapshots, cap policy,
/// equivalence catalogue, and project wait policy without starting a quota
/// probe on the launch path.
/// </summary>
public sealed class QuotaAdmissionService
{
    private readonly QuotaService _quota;
    private readonly CliQuotaCapsService _caps;
    private readonly CliQuotaFallbackService _fallback;
    private readonly CliQuotaWaitPolicyService _waitPolicy;
    private readonly ProjectSettingsService _settings;

    public QuotaAdmissionService(
        QuotaService quota,
        CliQuotaCapsService caps,
        CliQuotaFallbackService fallback,
        CliQuotaWaitPolicyService waitPolicy,
        ProjectSettingsService settings)
    {
        _quota = quota;
        _caps = caps;
        _fallback = fallback;
        _waitPolicy = waitPolicy;
        _settings = settings;
    }

    public QuotaAdmissionPlan Plan(QuotaAdmissionRequest request)
    {
        var project = string.IsNullOrWhiteSpace(request.ProjectName)
            ? null
            : _settings.Get(request.ProjectName);
        return QuotaAdmissionPlanner.Plan(
            request.CliType,
            request.Model,
            request.ThinkingLevel,
            _fallback,
            _caps,
            cli => string.IsNullOrWhiteSpace(cli) ? null : _quota.GetCachedFor(cli),
            DateTime.UtcNow,
            Math.Max(0, request.OccupiedSlots),
            _waitPolicy.Resolve(project),
            request.ExpectedCost,
            request.ExecutionPath);
    }
}
