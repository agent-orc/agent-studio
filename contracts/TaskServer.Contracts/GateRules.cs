using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Gate subject, report and plan rules shared by every authority that hands
/// out claimable gate attempts: the Task Server and the monolith authority.
/// </summary>
public static class GateRules
{
    private static readonly JsonSerializerOptions GateJson = new(JsonSerializerDefaults.Web);

    public static string PlanHash(GatePlan plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(plan, GateJson)))).ToLowerInvariant();

    /// <summary>Host capabilities a gate plan needs, derived from its frozen commands.</summary>
    public static IReadOnlyList<string> RequiredCapabilities(IEnumerable<GateCommand> commands)
    {
        var commandText = string.Join(' ', commands.SelectMany(command =>
            new[] { command.FileName }.Concat(command.Arguments))).ToLowerInvariant();
        var capabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess,
        };
        if (commandText.Contains("dotnet", StringComparison.Ordinal))
            capabilities.Add(CapabilityProtocol.DotNet);
        if (commandText.Contains("npm", StringComparison.Ordinal)
            || commandText.Contains("node", StringComparison.Ordinal)
            || commandText.Contains("npx", StringComparison.Ordinal))
            capabilities.Add(CapabilityProtocol.Node);
        if (commandText.Contains("playwright", StringComparison.Ordinal))
            capabilities.Add(CapabilityProtocol.Playwright);
        // AGT-2981: a Compose-render step routes to a gate host that
        // can run `docker compose config`; the scripts also need node.
        if (ComposeRenderGatePolicy.IsRenderInvocation(commandText))
        {
            capabilities.Add(CapabilityProtocol.ComposeRender);
            capabilities.Add(CapabilityProtocol.Node);
        }
        return capabilities.Order(StringComparer.Ordinal).ToArray();
    }

    public static void ValidateSubject(CreateGateSubjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceRunId) || string.IsNullOrWhiteSpace(request.TaskId)
            || string.IsNullOrWhiteSpace(request.RepositoryId)
            || string.IsNullOrWhiteSpace(request.PolicyHash) || string.IsNullOrWhiteSpace(request.PipelineDefinitionVersion)
            || string.IsNullOrWhiteSpace(request.TestSelectionAuditDigest)
            || request.MaxAttempts is < 1 or > 5
            || !ValidDigest(request.ExpectedSha, 40, 64) || !ValidDigest(request.PlanHash, 64)
            || (string.IsNullOrWhiteSpace(request.ResultRef) &&
                (string.IsNullOrWhiteSpace(request.SourceBundleArtifactId) || !ValidDigest(request.SourceBundleSha256, 64))))
            throw new ArgumentException("Gate subject requires a bounded, exact source and all audit identities.");
        if (request.BatchId is not null)
        {
            var candidatePrefix = $"refs/agent-studio/batch-candidates/{request.BatchId}/{request.MembershipDigest}/";
            if (!Guid.TryParseExact(request.BatchId, "N", out _)
                || !ValidDigest(request.MembershipDigest, 64)
                || !ValidDigest(request.BaseSha, 40, 64)
                || request.MemberRunIds is not { Count: > 0 and <= 8 }
                || request.MemberRunIds.Distinct(StringComparer.Ordinal).Count() != request.MemberRunIds.Count
                || !request.MemberRunIds.Contains(request.SourceRunId, StringComparer.Ordinal)
                || request.ResultRef is null
                || !request.ResultRef.StartsWith(candidatePrefix, StringComparison.Ordinal)
                || !long.TryParse(request.ResultRef[candidatePrefix.Length..], out var candidateFence)
                || candidateFence <= 0
                || request.SourceBundleArtifactId is not null)
                throw new ArgumentException("Batch gate subject requires a closed manifest identity and candidate ref.");
            if (request.Plan.SubjectBindingDigest != request.MembershipDigest)
                throw new ArgumentException("Batch gate plan must bind its membership digest.");
        }
        else if (request.MembershipDigest is not null || request.BaseSha is not null
                 || request.MemberRunIds is not null)
            throw new ArgumentException("Batch identity fields require a batch id.");
        var plan = request.Plan;
        if (plan.GateId != "post-build-test-gate" || plan.Version < 1 || plan.Commands.Count is < 1 or > 32
            || plan.OverallTimeoutSeconds is < 1 or > 21600 || plan.MaxOutputBytes is < 1 or > 1048576
            || plan.CleanupPolicy != "always" || Path.IsPathRooted(plan.WorkingSubdirectory)
            || plan.WorkingSubdirectory.Split('/', '\\').Any(segment => segment == "..")
            || plan.SourcePaths is { Count: > 32 }
            || plan.SourcePaths?.Any(path => string.IsNullOrWhiteSpace(path)
                || path.Length > 256
                || Path.IsPathRooted(path)
                || path.Split('/', '\\').Any(segment => segment is ".." or "." or "")) == true
            || plan.Commands.Select(command => command.StepId).Distinct(StringComparer.Ordinal).Count() != plan.Commands.Count
            || plan.Commands.Any(command => string.IsNullOrWhiteSpace(command.StepId)
                || string.IsNullOrWhiteSpace(command.FileName) || command.TimeoutSeconds is < 1 or > 21600
                || Path.IsPathRooted(command.WorkingSubdirectory)
                || command.WorkingSubdirectory.Split('/', '\\').Any(segment => segment == "..")))
            throw new ArgumentException("Gate plan must be a bounded catalogued post-build-test plan.");
        var calculated = PlanHash(plan);
        if (!string.Equals(calculated, request.PlanHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Gate plan hash does not match its frozen commands.");
    }

    public static bool SameSubject(GateSubject existing, CreateGateSubjectRequest request)
        => existing.TaskId == request.TaskId && existing.RepositoryId == request.RepositoryId
            && existing.RepositoryUrl == request.RepositoryUrl
            && string.Equals(existing.ExpectedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
            && existing.ResultRef == request.ResultRef
            && existing.SourceBundleArtifactId == request.SourceBundleArtifactId
            && existing.SourceBundleSha256 == request.SourceBundleSha256
            && existing.PolicyHash == request.PolicyHash
            && existing.PipelineDefinitionVersion == request.PipelineDefinitionVersion
            && existing.TestSelectionAuditDigest == request.TestSelectionAuditDigest
            && existing.DispatchDeadline == request.DispatchDeadline.ToUniversalTime()
            && existing.MaxAttempts == request.MaxAttempts
            && existing.BatchId == request.BatchId
            && existing.MembershipDigest == request.MembershipDigest
            && existing.BaseSha == request.BaseSha
            && (existing.MemberRunIds ?? []).SequenceEqual(request.MemberRunIds ?? [], StringComparer.Ordinal);

    /// <summary>Returns why a report breaks its frozen plan, or null for an admissible report.</summary>
    public static string? ReportViolation(GatePlan plan, GateReport report)
    {
        if (report.Outcome is not (GateStates.Passed or GateStates.ProductFailed or GateStates.InfraFailed)
            || report.CleanupStatus is not ("clean" or "failed")
            || string.IsNullOrWhiteSpace(report.EnvironmentIdentity)
            || string.IsNullOrWhiteSpace(report.ToolchainIdentity)
            || report.Commands.Count > plan.Commands.Count
            || report.Commands.Select(item => item.StepId).Distinct(StringComparer.Ordinal).Count() != report.Commands.Count
            || report.Commands.Where((item, index) =>
                item.StepId != plan.Commands[index].StepId).Any()
            || report.Commands.Any(item => !ValidDigest(item.OutputSha256, 64)
                || Encoding.UTF8.GetByteCount(item.OutputExcerpt) > plan.MaxOutputBytes)
            || report.OutputDigests.Any(digest => !ValidDigest(digest, 64))
            || report.ArtifactDigests.Any(digest => !ValidDigest(digest, 64)))
            return "Gate report is outside its frozen plan or evidence bounds.";
        if (report.FailureClassification is not null
            && report.FailureClassification is not (GateFailureClasses.ProductFailure
                or GateFailureClasses.ExecutionTimeout or GateFailureClasses.LostLease
                or GateFailureClasses.SnapshotUnavailable or GateFailureClasses.ToolFailure
                or GateFailureClasses.CleanupFailure))
            return "Gate failure classification is unknown.";
        if (report.FailureClassification is null
            && (report.Outcome != GateStates.Passed || report.Commands.Count != plan.Commands.Count
                || report.Commands.Any(item => item.ExitCode != 0 || item.TimedOut)
                || !ValidDigest(report.TestedTree, 40, 64) || report.DirtyBefore))
            return "Passing gate lacks full clean command proof.";
        if (report.FailureClassification == GateFailureClasses.ProductFailure
            && (report.Outcome != GateStates.ProductFailed || !ValidDigest(report.TestedTree, 40, 64)
                || report.DirtyBefore || !report.Commands.Any(item => item.ExitCode is > 0 && !item.TimedOut)))
            return "Product failure lacks normal failing command proof.";
        return null;
    }

    private static bool ValidDigest(string? value, params int[] lengths)
        => value is not null
           && lengths.Contains(value.Length)
           && value.All(Uri.IsHexDigit);
}

public static class GateOutcomePolicy
{
    public static (string State, string Outcome, string? FailureClassification) Decide(
        GateReport report, int attemptNumber, int maxAttempts)
    {
        if (report.CleanupStatus != "clean")
            return (GateStates.InfraFailed, GateFailureClasses.GateInfra, GateFailureClasses.CleanupFailure);
        if (report.FailureClassification == GateFailureClasses.ProductFailure)
            return (GateStates.ProductFailed, GateFailureClasses.ProductFailure, GateFailureClasses.ProductFailure);
        if (report.FailureClassification is null && report.Outcome == GateStates.Passed)
            return (GateStates.Passed, GateStates.Passed, null);
        if (report.FailureClassification == GateFailureClasses.ExecutionTimeout)
            return attemptNumber < maxAttempts
                ? (GateStates.InfraRetry, GateFailureClasses.ExecutionTimeout, GateFailureClasses.ExecutionTimeout)
                : (GateStates.TimedOut, GateFailureClasses.GateInfra, GateFailureClasses.ExecutionTimeout);
        return Infra(report.FailureClassification ?? GateFailureClasses.ToolFailure, attemptNumber, maxAttempts);
    }

    private static (string State, string Outcome, string FailureClassification) Infra(
        string failure, int attemptNumber, int maxAttempts)
        => attemptNumber < maxAttempts
            ? (GateStates.InfraRetry, failure, failure)
            : (GateStates.InfraFailed, GateFailureClasses.GateInfra, failure);
}
