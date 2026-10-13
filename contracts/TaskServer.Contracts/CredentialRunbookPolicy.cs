using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Pure catalogue validation and runbook selection. Selection never starts work:
/// an unknown predicate yields diagnosis-only, and an uncorroborated 401 never
/// becomes sign-in advice.
/// </summary>
public static partial class CredentialRunbookPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly int[] IncidentRetrySeconds = [60, 120, 300];

    /// <summary>Cancellation cannot withdraw custody work already dispatched to a host or renewal.</summary>
    public static bool CanCancel(string state, string? currentStep, bool linkedRenewal, IEnumerable<string> receiptSteps)
        => !linkedRenewal &&
           !(state is CredentialRunbookStates.AwaitingHost or CredentialRunbookStates.AwaitingHuman &&
             currentStep is not null && CredentialRunbookProtocol.Steps[currentStep].ChangesCustody) &&
           !receiptSteps.Any(step => CredentialRunbookProtocol.Steps[step].ChangesCustody);

    public static CredentialRunbookCatalogueDto Parse(string json)
    {
        CredentialRunbookCatalogueDto? catalogue;
        try
        {
            catalogue = JsonSerializer.Deserialize<CredentialRunbookCatalogueDto>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Runbook catalogue is not valid versioned runbook data.", exception);
        }
        if (catalogue is null) throw new FormatException("Runbook catalogue is empty.");
        Validate(catalogue);
        return catalogue;
    }

    public static void Validate(CredentialRunbookCatalogueDto catalogue)
    {
        Require(catalogue.SchemaVersion == CredentialRunbookProtocol.CatalogueSchemaVersion &&
                catalogue.RecordClass == CredentialRunbookProtocol.CatalogueRecordClass &&
                catalogue.Runbooks is { Count: > 0 }, "catalogue", "unsupported schema or record class");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runbook in catalogue.Runbooks)
        {
            var id = runbook.Id ?? "";
            Require(RunbookId().IsMatch(id) && ids.Add(id), id, "identifier is invalid or duplicated");
            Require(runbook.Version >= 1, id, "version must be positive");
            Require(CredentialRunbookProtocol.Kinds.Contains(runbook.Kind), id, $"kind '{runbook.Kind}' is not typed");
            Require(runbook.CredentialKinds is { Count: > 0 } &&
                    runbook.CredentialKinds.All(CredentialRegistryProtocol.Kinds.Contains), id, "credential kind is unknown");
            Require(CredentialRunbookProtocol.Outcomes.Contains(runbook.TargetOutcome), id, "target outcome is unknown");
            Require(runbook.ReceiptFields is not null &&
                    runbook.ReceiptFields.ToHashSet(StringComparer.Ordinal).SetEquals(CredentialRunbookProtocol.ReceiptFields) &&
                    runbook.ReceiptFields.Count == CredentialRunbookProtocol.ReceiptFields.Length,
                id, "receipt fields differ from the durable receipt contract");
            var execution = runbook.ExecutionPolicy;
            Require(execution is { GenerationFenceRequired: true, DurableIdempotencyRequired: true,
                        SecretValuesAllowedInReceipts: false } &&
                    execution.OnUnknownPredicate.StartsWith("diagnosis-only", StringComparison.Ordinal),
                id, "execution policy must fence generations, keep receipts durable and secret-free");
            ValidateSteps(runbook);
            ValidateMatch(runbook);
        }
    }

    private static void ValidateSteps(CredentialRunbookDto runbook)
    {
        var steps = runbook.PlatformSteps ?? [];
        Require(steps.Count > 0 && steps.Distinct(StringComparer.Ordinal).Count() == steps.Count,
            runbook.Id, "platform steps are empty or repeated");
        var definitions = new List<CredentialRunbookStepDefinition>();
        foreach (var step in steps)
        {
            Require(CredentialRunbookProtocol.Steps.TryGetValue(step, out var definition),
                runbook.Id, $"step '{Shorten(step)}' is not an allowlisted adapter opcode");
            definitions.Add(definition!);
        }
        var boundaries = definitions.Any(step => step.HumanBoundary is not null);
        Require(boundaries == runbook.HumanSteps is { Count: > 0 }, runbook.Id,
            "human steps must match the steps that cross a human boundary");
        for (var index = 0; index < definitions.Count; index++)
        {
            var step = definitions[index];
            if (step.Effect is not (CredentialRunbookStepEffect.Revoke or CredentialRunbookStepEffect.Advertise))
                continue;
            var proof = LastIndex(definitions, index, candidate => candidate.Proves);
            var issue = LastIndex(definitions, index, candidate =>
                candidate.Effect is CredentialRunbookStepEffect.Issue or CredentialRunbookStepEffect.Stage);
            Require(proof >= 0 && proof > issue, runbook.Id,
                $"step '{step.StepId}' needs a verification step after the last issuance");
        }
    }

    private static void ValidateMatch(CredentialRunbookDto runbook)
    {
        Require(runbook.Match?.All is { Count: > 0 }, runbook.Id, "match needs typed predicates");
        foreach (var predicate in runbook.Match!.All)
        {
            Require(CredentialRunbookProtocol.Fields.ContainsKey(predicate.Field), runbook.Id,
                $"predicate field '{Shorten(predicate.Field)}' is not typed");
            var valid = predicate.Operator switch
            {
                "equals" => predicate.Value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False,
                "in" => predicate.Value.ValueKind == JsonValueKind.Array && predicate.Value.GetArrayLength() > 0 &&
                        predicate.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                _ => false,
            };
            Require(valid, runbook.Id, $"predicate '{predicate.Field}' has an unsupported operator or value");
            if (predicate.Field == "credential.kind")
                Require(Values(predicate.Value).All(value => runbook.CredentialKinds.Contains(value as string ?? "")),
                    runbook.Id, "credential kind predicate exceeds the runbook kinds");
        }
    }

    public static CredentialRunbookSelection Select(CredentialRunbookCatalogueDto catalogue, CredentialIncidentFacts facts)
    {
        var matched = new List<CredentialRunbookDto>();
        var candidates = new List<(CredentialRunbookDto Runbook, IReadOnlyList<string> Unknown)>();
        foreach (var runbook in catalogue.Runbooks)
        {
            var unknown = new List<string>();
            var rejected = false;
            foreach (var predicate in runbook.Match.All)
            {
                var actual = CredentialRunbookProtocol.Fields[predicate.Field](facts);
                if (actual is null)
                {
                    unknown.Add(predicate.Field);
                    continue;
                }
                if (!Values(predicate.Value).Contains(actual)) rejected = true;
            }
            if (rejected) continue;
            if (unknown.Count == 0) matched.Add(runbook);
            else candidates.Add((runbook, unknown));
        }
        if (matched.Count == 1)
        {
            var runbook = matched[0];
            return new(CredentialRunbookModes.Repair, runbook.Id, runbook.Version, PolicyFor(runbook), [],
                $"All typed predicates of {runbook.Id} hold.");
        }
        if (matched.Count > 1)
            return new(CredentialRunbookModes.DiagnosisOnly, null, null, Inconclusive, [],
                "More than one runbook matched; diagnosis only.");
        if (candidates.Count == 1)
            return new(CredentialRunbookModes.DiagnosisOnly, candidates[0].Runbook.Id, candidates[0].Runbook.Version,
                Inconclusive, candidates[0].Unknown,
                "A required predicate is not observed; diagnosis only, repair does not begin.");
        if (candidates.Count > 1)
            return new(CredentialRunbookModes.DiagnosisOnly, null, null, Inconclusive,
                candidates.SelectMany(item => item.Unknown).Distinct(StringComparer.Ordinal).ToArray(),
                "Several runbooks remain possible; diagnosis only.");
        var ambiguousAccess = facts.ProbeOutcome == "indeterminate" || facts.RequestNormalizedCode == "unauthorized";
        return new(CredentialRunbookModes.None, null, null, ambiguousAccess ? Inconclusive : ExistingOwner, [],
            ambiguousAccess
                ? "No runbook matches; an uncorroborated failure stays indeterminate."
                : "No runbook matches; the existing outcome owner keeps this observation.");
    }

    /// <summary>Kind-specific typed reaction. The runbook prose action policy is documentation only.</summary>
    public static CredentialRecoveryPolicy PolicyFor(CredentialRunbookDto runbook) => runbook.Kind switch
    {
        "refresh_rotation_conflict" => new("renewal", "binding", [],
            [CredentialRunbookHumanBoundaries.BrowserConsent], true, true, runbook.OperatorAction),
        "provider_incident_unauthorized" => new("none", "service-cohort", IncidentRetrySeconds,
            [], false, false, runbook.OperatorAction),
        "repository_scope_missing" => new("repository-repair", "repository", [],
            [CredentialRunbookHumanBoundaries.RepositoryAdministration], true, false, runbook.OperatorAction),
        _ => throw new FormatException($"Runbook kind '{runbook.Kind}' is not typed."),
    };

    /// <summary>Uncorroborated access failure: hold the binding, retry, and raise diagnosis after 15 minutes.</summary>
    public static readonly CredentialRecoveryPolicy Inconclusive =
        new("diagnosis-after-15-minutes", "binding", IncidentRetrySeconds, [], false, false, null);

    public static readonly CredentialRecoveryPolicy ExistingOwner =
        new("existing-owner", "none", [], [], false, false, null);

    public static IReadOnlyList<CredentialRunbookStepDefinition> Plan(CredentialRunbookDto runbook)
        => runbook.PlatformSteps.Select(step => CredentialRunbookProtocol.Steps[step]).ToArray();

    private static IEnumerable<object> Values(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(item => (object)item.GetString()!),
            JsonValueKind.True => [true],
            JsonValueKind.False => [false],
            _ => [value.GetString()!],
        };

    private static int LastIndex(List<CredentialRunbookStepDefinition> steps, int before,
        Func<CredentialRunbookStepDefinition, bool> predicate)
    {
        for (var index = before - 1; index >= 0; index--)
            if (predicate(steps[index])) return index;
        return -1;
    }

    private static void Require(bool condition, string? runbookId, string message)
    {
        if (!condition) throw new FormatException($"Runbook {runbookId ?? "(unknown)"}: {message}.");
    }

    private static string Shorten(string? value)
        => value is null ? "" : value.Length <= 40 ? value : value[..40];

    [GeneratedRegex("^RB-[A-Z0-9-]{1,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunbookId();
}
