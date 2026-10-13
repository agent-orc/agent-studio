using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Allowlisted Task Server step adapters. Each reads authoritative metadata (registry,
/// capability advertisements, I3 health circuits, I4 renewal receipts). The only write
/// is the idempotent I4 renewal reservation; browser consent and host installation stay
/// with the existing sign-in adapters. Host-owned steps have no adapter here and wait
/// for an explicit receipt.
/// </summary>
public sealed partial class TaskServerStore
{
    private IReadOnlyDictionary<string, ICredentialRunbookStepAdapter> RunbookAdapters(
        CredentialRunbookOperationDto operation)
    {
        var adapters = new Dictionary<string, ICredentialRunbookStepAdapter>(StringComparer.Ordinal)
        {
            ["discover_effective_source"] = new RunbookAdapter(DiscoverEffectiveSourceAsync),
            ["classify_provider_access"] = new RunbookAdapter(ClassifyProviderAccessAsync),
            ["verify_real_request"] = new RunbookAdapter(VerifyRealRequestAsync),
            ["correlate_incident"] = new RunbookAdapter(CorrelateIncidentAsync),
            ["pause_affected_claims"] = new RunbookAdapter(ConfirmIncidentHoldAsync),
            ["schedule_bounded_retry"] = new RunbookAdapter(ConfirmBoundedRetryAsync),
            ["verify_recovery_canary"] = new RunbookAdapter(VerifyRecoveryCanaryAsync),
            ["advertise_generation"] = new RunbookAdapter(VerifyAdvertisementAsync),
            ["discover_repository_binding"] = new RunbookAdapter(DiscoverRepositoryBindingAsync),
        };
        // Provider logins reuse the I4 durable renewal; other kinds report host receipts.
        if (RunbookRenewalMethod(operation) is not null)
        {
            adapters["begin_host_login"] = new RunbookAdapter(BeginLinkedHostLoginAsync);
            foreach (var step in new[] { "stage_local_secret", "install_generation", "reconcile_units_safely",
                         "retire_old_generation" })
                adapters[step] = new RunbookAdapter(ObserveLinkedRenewalAsync);
        }
        return adapters;
    }

    private static string? RunbookRenewalMethod(CredentialRunbookOperationDto operation)
        => operation.Facts.CredentialKind is { } kind &&
           CredentialRegistryProtocol.RenewalMethods.TryGetValue(kind, out var method) &&
           ProviderRenewalProtocol.Methods.Contains(method) ? method : null;

    private async Task<CredentialRunbookStepResult> DiscoverEffectiveSourceAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        if (record is null) return CredentialRunbookStepResult.Failed("credential-not-registered");
        if (record.Generation != context.Operation.ExpectedGeneration)
            return CredentialRunbookStepResult.Failed("stale-credential-generation");
        return record.Locator.EffectiveSource == "active"
            ? CredentialRunbookStepResult.Succeeded(record.Generation,
                RunbookEvidenceRef("registry", record.CredentialId))
            : CredentialRunbookStepResult.Failed("effective-source-" + record.Locator.EffectiveSource);
    }

    private async Task<CredentialRunbookStepResult> ClassifyProviderAccessAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        var target = context.Runbook.TargetOutcome;
        var capability = (await ReadRunbookCapabilitiesAsync(context.Operation, record, ct)).FirstOrDefault(item =>
            item.Outcome == target && item.Generation == context.Operation.ExpectedGeneration);
        if (capability is not null)
            return CredentialRunbookStepResult.Succeeded(context.Operation.ExpectedGeneration,
                RunbookEvidenceRef("capability", capability.EvidenceId ?? capability.RunnerId));
        return record?.LastOutcome == target && record.Generation == context.Operation.ExpectedGeneration
            ? CredentialRunbookStepResult.Succeeded(record.Generation, RunbookEvidenceRef("registry-outcome", record.CredentialId))
            : CredentialRunbookStepResult.Failed("not-classified-" + target);
    }

    /// <summary>Before installation this records the failing real request; afterwards it is the new-generation proof.</summary>
    private async Task<CredentialRunbookStepResult> VerifyRealRequestAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        if (context.CustodyChanged)
        {
            var renewal = await FindLinkedRenewalAsync(context.Operation, ct);
            if (renewal is null) return CredentialRunbookStepResult.Waiting("host-real-request-pending");
            if (renewal.Step is "cancelled" or "failed") return LinkedRenewalEnded(context.Operation, renewal);
            return renewal.RealRequestSucceeded && renewal.ObservedGeneration is { } generation &&
                   generation != context.Operation.ExpectedGeneration && BothRenewalUnits(renewal.VerifiedUnits)
                ? CredentialRunbookStepResult.Succeeded(generation, RunbookEvidenceRef("provider-renewal", renewal.OperationId))
                : CredentialRunbookStepResult.Waiting("awaiting-two-unit-real-request");
        }
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        var observed = (await ReadRunbookCapabilitiesAsync(context.Operation, record, ct)).FirstOrDefault(item =>
            item.Generation == context.Operation.ExpectedGeneration &&
            (item.EvidenceExcerpt?.StartsWith("unauthorized", StringComparison.Ordinal) == true ||
             item.Outcome is "provider_incident" or "credential_invalid"));
        return observed is null
            ? CredentialRunbookStepResult.Failed("real-request-evidence-missing")
            : CredentialRunbookStepResult.Succeeded(context.Operation.ExpectedGeneration,
                RunbookEvidenceRef("capability", observed.EvidenceId ?? observed.RunnerId));
    }

    private async Task<CredentialRunbookStepResult> CorrelateIncidentAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        var incident = (await ReadRunbookCircuitsAsync(context.Operation, record, ct))
            .FirstOrDefault(circuit => circuit.ServiceWide && circuit.Outcome == "provider_incident");
        if (incident is null) return CredentialRunbookStepResult.Failed("incident-not-corroborated");
        // I2 sets an official incident identifier; an independent known-good comparison has none.
        var reference = "incident:" + record!.Provider + ":" +
            (incident.EvidenceId is { } id && RunbookEvidenceValue.IsMatch(id) && IsRunbookSafe(id)
                ? id : "known-good-comparison");
        return new(CredentialRunbookStepOutcomes.Succeeded, context.Operation.ExpectedGeneration,
            [RunbookEvidenceRef("circuit", "service-" + record.Provider)], IncidentRef: reference);
    }

    private async Task<CredentialRunbookStepResult> ConfirmIncidentHoldAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        return (await ReadRunbookCircuitsAsync(context.Operation, record, ct)).Any(circuit => circuit.ServiceWide)
            ? CredentialRunbookStepResult.Succeeded(context.Operation.ExpectedGeneration,
                RunbookEvidenceRef("hold", "service-" + record!.Provider))
            : CredentialRunbookStepResult.Failed("incident-hold-not-found");
    }

    private async Task<CredentialRunbookStepResult> ConfirmBoundedRetryAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        var circuit = (await ReadRunbookCircuitsAsync(context.Operation, record, ct))
            .FirstOrDefault(item => item.ServiceWide);
        if (circuit is null)
            return CredentialRunbookStepResult.Succeeded(context.Operation.ExpectedGeneration,
                RunbookEvidenceRef("hold-released", "service-" + record!.Provider));
        var bounded = circuit.CanaryUntil > UtcNow ||
                      circuit.NextRetryAt is { } retry && retry - UtcNow <= TimeSpan.FromSeconds(305);
        return bounded
            ? CredentialRunbookStepResult.Succeeded(context.Operation.ExpectedGeneration,
                RunbookEvidenceRef("retry", "service-" + record!.Provider))
            : CredentialRunbookStepResult.Failed("retry-not-bounded");
    }

    private async Task<CredentialRunbookStepResult> VerifyRecoveryCanaryAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        var circuits = await ReadRunbookCircuitsAsync(context.Operation, record, ct);
        if (circuits.Count > 0)
            return CredentialRunbookStepResult.Waiting("retry-scheduled");
        var canary = (await ReadRunbookCapabilitiesAsync(context.Operation, record, ct)).FirstOrDefault(item =>
            item.Outcome == "healthy" && item.Generation == context.Operation.ExpectedGeneration &&
            item.LastRealSuccessAt > context.Operation.CreatedAt);
        return canary is null
            ? CredentialRunbookStepResult.Waiting("awaiting-recovery-canary")
            : CredentialRunbookStepResult.Succeeded(canary.Generation,
                RunbookEvidenceRef("capability", canary.EvidenceId ?? canary.RunnerId));
    }

    private async Task<CredentialRunbookStepResult> VerifyAdvertisementAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        var generation = context.ProvenGeneration ?? context.Operation.ExpectedGeneration;
        var advertised = (await ReadRunbookCapabilitiesAsync(context.Operation, record, ct)).FirstOrDefault(item =>
            item.Outcome == "healthy" && item.Generation == generation && item.FreshUntil > UtcNow &&
            item.LastRealSuccessAt >= context.Operation.CreatedAt);
        return advertised is null
            ? CredentialRunbookStepResult.Waiting("awaiting-fresh-advertisement")
            : CredentialRunbookStepResult.Succeeded(generation,
                RunbookEvidenceRef("capability", advertised.EvidenceId ?? advertised.RunnerId));
    }

    private async Task<CredentialRunbookStepResult> DiscoverRepositoryBindingAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var record = await ReadRunbookRecordAsync(context.Operation, ct);
        if (record is null) return CredentialRunbookStepResult.Failed("credential-not-registered");
        return record.RepositoryPurpose == context.Operation.Facts.RepositoryPurpose && record.RepositoryPurpose is not null
            ? CredentialRunbookStepResult.Succeeded(record.Generation, RunbookEvidenceRef("registry", record.CredentialId))
            : CredentialRunbookStepResult.Failed("repository-purpose-not-registered");
    }

    /// <summary>
    /// Reserves the I4 renewal under a key derived from this operation, so a retry
    /// returns the same reservation. Consent completes only in the existing sign-in flow.
    /// </summary>
    private async Task<CredentialRunbookStepResult> BeginLinkedHostLoginAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var operation = context.Operation;
        var key = LinkedRenewalKey(operation);
        var renewal = await FindLinkedRenewalAsync(operation, ct);
        if (renewal is null)
        {
            try
            {
                renewal = await BeginProviderRenewalAsync(new(operation.InstallationId, operation.HostId,
                    operation.CredentialId, operation.ExpectedGeneration, RunbookRenewalMethod(operation)!, key,
                    UtcNow.AddMinutes(15)), operation.ActorId, ct);
            }
            catch (TaskServerConflictException conflict)
            {
                return CredentialRunbookStepResult.Failed(conflict.Code);
            }
        }
        if (renewal.Step is "cancelled" or "failed")
            return LinkedRenewalEnded(operation, renewal) with
            {
                LinkedRenewalKey = key, LinkedRenewalOperationId = renewal.OperationId,
            };
        var evidence = new[] { RunbookEvidenceRef("provider-renewal", renewal.OperationId) };
        var outcome = renewal.Step switch
        {
            "requested" or "preflight" or "awaiting-human" => CredentialRunbookStepOutcomes.AwaitingHuman,
            "recovery-required" => CredentialRunbookStepResult.Pending,
            _ => CredentialRunbookStepOutcomes.Succeeded,
        };
        return new(outcome, null, evidence,
            outcome == CredentialRunbookStepResult.Pending ? "provider-renewal-recovery-required"
                : CredentialRunbookHumanBoundaries.BrowserConsent,
            LinkedRenewalKey: key, LinkedRenewalOperationId: renewal.OperationId);
    }

    private async Task<CredentialRunbookStepResult> ObserveLinkedRenewalAsync(
        CredentialRunbookStepContext context, CancellationToken ct)
    {
        var renewal = await FindLinkedRenewalAsync(context.Operation, ct);
        if (renewal is null) return CredentialRunbookStepResult.Failed("provider-renewal-missing");
        if (renewal.Step is "cancelled" or "failed") return LinkedRenewalEnded(context.Operation, renewal);
        var index = Array.IndexOf(ProviderRenewalProtocol.Steps, renewal.Step);
        var reached = context.StepId switch
        {
            "stage_local_secret" => index >= Array.IndexOf(ProviderRenewalProtocol.Steps, "staged"),
            "install_generation" => index >= Array.IndexOf(ProviderRenewalProtocol.Steps, "installed"),
            "reconcile_units_safely" => BothRenewalUnits(renewal.VerifiedUnits) && renewal.ObservedGeneration is not null,
            "retire_old_generation" => index >= Array.IndexOf(ProviderRenewalProtocol.Steps, "retired"),
            _ => false,
        };
        if (!reached)
            return CredentialRunbookStepResult.Waiting(renewal.Step == "recovery-required"
                ? "provider-renewal-recovery-required" : "awaiting-provider-renewal");
        return CredentialRunbookStepResult.Succeeded(
            context.StepId is "stage_local_secret" or "install_generation" ? null : renewal.ObservedGeneration,
            RunbookEvidenceRef("provider-renewal", renewal.OperationId));
    }

    /// <summary>I4 cancels a recovery only with both units and a real request on the prior generation.</summary>
    private static CredentialRunbookStepResult LinkedRenewalEnded(
        CredentialRunbookOperationDto operation, ProviderRenewalReceiptDto renewal)
        => renewal.Step == "cancelled" && renewal.ObservedGeneration == operation.ExpectedGeneration &&
           renewal.RealRequestSucceeded && BothRenewalUnits(renewal.VerifiedUnits)
            ? new(CredentialRunbookStepOutcomes.Failed, renewal.ObservedGeneration,
                [RunbookEvidenceRef("provider-renewal", renewal.OperationId)], "prior-generation-restored",
                PriorGenerationRestored: true)
            : new(CredentialRunbookStepOutcomes.Failed, null,
                [RunbookEvidenceRef("provider-renewal", renewal.OperationId)], "provider-renewal-" + renewal.Step);

    private static string LinkedRenewalKey(CredentialRunbookOperationDto operation)
        => operation.OperationId + ".login";

    private async Task<ProviderRenewalReceiptDto?> FindLinkedRenewalAsync(
        CredentialRunbookOperationDto operation, CancellationToken ct)
    {
        string? renewalId;
        await using (var connection = await OpenReadyAsync(ct))
            renewalId = await ScalarAsync(connection, """
                SELECT operation_id FROM provider_renewal_operations
                 WHERE installation_id = $installation AND host_id = $host
                   AND credential_id = $credential AND idempotency_key = $key;
                """, ct, ("$installation", operation.InstallationId), ("$host", operation.HostId),
                ("$credential", operation.CredentialId), ("$key", LinkedRenewalKey(operation))) as string;
        return renewalId is null ? null : await GetProviderRenewalAsync(renewalId, ct);
    }

    private async Task<CredentialRegistryRecordDto?> ReadRunbookRecordAsync(
        CredentialRunbookOperationDto operation, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadRunbookCredentialAsync(connection, null, operation.InstallationId, operation.HostId,
            operation.CredentialId, ct);
    }

    private sealed record RunbookCapability(string RunnerId, string? Outcome, string? Generation,
        DateTime? LastRealSuccessAt, string? EvidenceId, string? EvidenceExcerpt, DateTime FreshUntil);

    private async Task<IReadOnlyList<RunbookCapability>> ReadRunbookCapabilitiesAsync(
        CredentialRunbookOperationDto operation, CredentialRegistryRecordDto? record, CancellationToken ct)
    {
        if (record is null) return [];
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT c.runner_id, c.health_outcome, c.credential_generation, c.last_real_success_at,
                   c.evidence_id, c.evidence_excerpt, c.fresh_until
              FROM runner_capabilities c JOIN runners r ON r.id = c.runner_id
             WHERE r.host_id = $host AND c.capability_key = $key
             ORDER BY c.advertised_at DESC;
            """, ("$host", operation.HostId), ("$key", CapabilityProtocol.ProviderAuthentication(record.Provider)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<RunbookCapability>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                Parse(reader.GetString(6))));
        return result;
    }

    private sealed record RunbookCircuit(bool ServiceWide, string Outcome, string? EvidenceId,
        DateTime? NextRetryAt, DateTime? CanaryUntil);

    private async Task<IReadOnlyList<RunbookCircuit>> ReadRunbookCircuitsAsync(
        CredentialRunbookOperationDto operation, CredentialRegistryRecordDto? record, CancellationToken ct)
    {
        if (record is null) return [];
        var service = ServiceCohort(record.Provider);
        var binding = $"binding:{operation.HostId}:";
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT cohort_key, outcome, evidence_id, next_retry_at, canary_until
              FROM provider_health_circuits
             WHERE provider = $provider
               AND (cohort_key = $service OR substr(cohort_key, 1, length($binding)) = $binding);
            """, ("$provider", record.Provider), ("$service", service), ("$binding", binding));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<RunbookCircuit>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0) == service, reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4))));
        return result;
    }

    private sealed class RunbookAdapter(
        Func<CredentialRunbookStepContext, CancellationToken, Task<CredentialRunbookStepResult>> execute)
        : ICredentialRunbookStepAdapter
    {
        public Task<CredentialRunbookStepResult> ExecuteAsync(CredentialRunbookStepContext context, CancellationToken ct)
            => execute(context, ct);
    }
}
