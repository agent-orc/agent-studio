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
    string ExecutionPath,
    Func<string, string?, bool>? ModelAccepted = null,
    string? ClaimingHost = null,
    Func<string, string?, bool>? FallbackModelAccepted = null,
    bool SameClassOnly = false);

/// <summary>
/// Legacy admission boundary, now a thin adapter over
/// <see cref="IModelSelectionService"/> (MS-5). Consumers that still speak
/// <see cref="QuotaAdmissionRequest"/> (review plane, card creation, model
/// rejection hold) reach the same planner and ledger; they move to the
/// selection request directly in MS-6.
/// </summary>
public sealed class QuotaAdmissionService
{
    public QuotaAdmissionService(
        QuotaService quota,
        CliQuotaCapsService caps,
        CliQuotaFallbackService fallback,
        CliQuotaWaitPolicyService waitPolicy,
        ProjectSettingsService projectSettings,
        TimeProvider? timeProvider = null,
        BetterCandidateService? betterCandidates = null,
        CliFallbackPreferenceService? preference = null)
        : this(new ModelSelectionService(
            quota, caps, fallback, waitPolicy, projectSettings, timeProvider, betterCandidates, preference))
    {
    }

    public QuotaAdmissionService(IModelSelectionService selection)
        => Selection = selection ?? throw new ArgumentNullException(nameof(selection));

    /// <summary>The selection service this adapter delegates to.</summary>
    public IModelSelectionService Selection { get; }

    public bool IsOperatorSwitchActive(string? cliType) => Selection.IsOperatorSwitchActive(cliType);

    public QuotaAdmissionPlan Plan(QuotaAdmissionRequest request)
        => Selection.Select(ModelSelectionCall.FromAdmission(request)).Admission;
}
