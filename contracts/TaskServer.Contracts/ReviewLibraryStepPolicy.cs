using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Version-one review-library envelope shared by both Task Server implementations and runner hosts.</summary>
public static class ReviewLibraryStepPolicy
{
    public const int Version = 1;

    public static ReviewPlanDto Seal(ReviewPlanDto plan, string subjectSha)
    {
        if (plan.LibraryVersion == 0)
        {
            if (plan.Commands.Any(command => command.LibraryStep is not null)
                || (plan.Preparation ?? []).Any(command => command.LibraryStep is not null))
                throw new ArgumentException("Legacy review plans cannot carry a library-step envelope.");
            return plan;
        }
        if (plan.LibraryVersion != Version)
            throw new ArgumentException("Unsupported review-library version.");
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectSha);
        var commands = plan.Commands.Select(command =>
        {
            var step = ForCommand(command, subjectSha);
            RejectChangedEnvelope(command.LibraryStep, step);
            return command with { LibraryStep = step };
        }).ToArray();
        var preparation = (plan.Preparation ?? []).Select(command =>
        {
            var step = ForPreparation(command, subjectSha);
            RejectChangedEnvelope(command.LibraryStep, step);
            return command with { LibraryStep = step };
        }).ToArray();
        return plan with { Commands = commands, Preparation = preparation };
    }

    public static bool Supports(ReviewPlanDto plan, IReadOnlySet<string> capabilities)
        => plan.LibraryVersion == 0
            ? plan.Commands.All(command => command.LibraryStep is null)
              && (plan.Preparation ?? []).All(command => command.LibraryStep is null)
            : plan.LibraryVersion == Version
              && plan.Commands.All(command => command.LibraryStep is not null)
              && (plan.Preparation ?? []).All(command => command.LibraryStep is not null)
              && Steps(plan).All(step => step.RequiredCapabilities.All(capabilities.Contains));

    public static IReadOnlyList<string> RequiredCapabilities(ReviewPlanDto plan)
        => Steps(plan).SelectMany(step => step.RequiredCapabilities)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static bool ValidPlan(ReviewPlanDto plan, string subjectSha)
        => plan.LibraryVersion == 0
            ? plan.Commands.All(command => command.LibraryStep is null)
              && (plan.Preparation ?? []).All(command => command.LibraryStep is null)
            : plan.LibraryVersion == Version
              && plan.Commands.All(command => Matches(command.LibraryStep, ForCommand(command, subjectSha)))
              && (plan.Preparation ?? []).All(command =>
                  Matches(command.LibraryStep, ForPreparation(command, subjectSha)));

    public static bool ValidReport(
        ReviewPlanDto plan,
        IReadOnlyList<ReviewCommandEvidenceDto> evidence,
        string? outcome = null)
    {
        if (string.Equals(outcome, "Pass", StringComparison.OrdinalIgnoreCase)
            && (plan.Commands.Where(command => command.Required).Any(command =>
                    !evidence.Any(item => item.Phase == "verification"
                        && item.WorkspaceRole == "candidate" && item.StepId == command.StepId))
                || (plan.Preparation ?? []).Any(command =>
                    !evidence.Any(item => item.Phase == "preparation"
                        && item.WorkspaceRole == "candidate" && item.StepId == command.StepId))))
            return false;
        foreach (var item in evidence)
        {
            var expected = item.Phase == "preparation"
                ? (plan.Preparation ?? []).FirstOrDefault(command => command.StepId == item.StepId)?.LibraryStep
                : plan.Commands.FirstOrDefault(command => command.StepId == item.StepId)?.LibraryStep;
            if (expected is null || !Matches(item.LibraryStep, expected)
                || !string.Equals(item.ExpectedResultSha, expected.InputSubjectSha,
                    StringComparison.OrdinalIgnoreCase)
                || (item.WorkspaceRole == "candidate"
                    && !string.Equals(item.HeadBefore, expected.InputSubjectSha,
                        StringComparison.OrdinalIgnoreCase))
                || (item.Budget is { } budget
                    && budget.LimitMs > expected.TimeoutSeconds * 1000L))
                return false;
        }
        return true;
    }

    private static IEnumerable<ReviewLibraryStepDto> Steps(ReviewPlanDto plan)
        => plan.Commands.Select(command => command.LibraryStep)
            .Concat((plan.Preparation ?? []).Select(command => command.LibraryStep))
            .OfType<ReviewLibraryStepDto>();

    private static ReviewLibraryStepDto ForCommand(ReviewCommandDto command, string sha)
    {
        var capabilities = new List<string> { ReviewCapabilities.LibraryStepV1 };
        if (command.CompareToBaseline) capabilities.Add(ReviewCapabilities.BaselineComparison);
        if (ReviewCommandKinds.IsAgent(command.ExecutionKind))
        {
            capabilities.Add(ReviewCapabilities.SemanticReview);
            if (!string.IsNullOrWhiteSpace(command.CliType))
            {
                capabilities.Add(CapabilityProtocol.CliExecution(command.CliType));
                capabilities.Add(CapabilityProtocol.ProviderAuthentication(command.CliType));
            }
        }
        AddToolchainRequirements(capabilities, command.FileName, command.Arguments);
        var limit = ReviewCommandKinds.IsAgent(command.ExecutionKind)
            ? ReviewAspectBudgetDefaults.CeilingSeconds
            : command.TimeoutSeconds;
        return Create(command.StepId, sha, limit, capabilities,
            JsonSerializer.Serialize(command with { LibraryStep = null }));
    }

    private static ReviewLibraryStepDto ForPreparation(ReviewPreparationCommandDto command, string sha)
    {
        var capabilities = new List<string>
        {
            ReviewCapabilities.LibraryStepV1,
            ReviewCapabilities.DependencyPreparation,
        };
        AddToolchainRequirements(capabilities, command.FileName, command.Arguments);
        return Create(command.StepId, sha, command.TimeoutSeconds, capabilities,
            JsonSerializer.Serialize(command with { LibraryStep = null }));
    }

    private static void AddToolchainRequirements(
        ICollection<string> capabilities, string fileName, IReadOnlyList<string> arguments)
    {
        var invocation = fileName + " " + string.Join(" ", arguments);
        if (ContainsTool(invocation, "dotnet")) capabilities.Add(CapabilityProtocol.DotNet);
        if (ContainsTool(invocation, "node") || ContainsTool(invocation, "npm")
            || ContainsTool(invocation, "npx")) capabilities.Add(CapabilityProtocol.Node);
        if (ContainsTool(invocation, "playwright")) capabilities.Add(CapabilityProtocol.Playwright);
    }

    private static bool ContainsTool(string invocation, string tool)
        => System.Text.RegularExpressions.Regex.IsMatch(
            invocation,
            $@"(?<![\w-]){tool}(?![\w-])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static ReviewLibraryStepDto Create(
        string id, string sha, int timeout, IEnumerable<string> capabilities, string payload)
    {
        if (string.IsNullOrWhiteSpace(id) || timeout is < 1 or > 7200)
            throw new ArgumentException("Review library step needs an id and a bounded timeout.");
        var required = capabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var digestInput = $"review-library:v1\n{id}\n{sha.ToLowerInvariant()}\n{payload}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput))).ToLowerInvariant();
        return new ReviewLibraryStepDto(id, Version, digest, sha.ToLowerInvariant(), required, timeout);
    }

    private static void RejectChangedEnvelope(ReviewLibraryStepDto? supplied, ReviewLibraryStepDto expected)
    {
        if (supplied is not null && !Matches(supplied, expected))
            throw new ArgumentException("Review library step digest or subject does not match the resolved plan.");
    }

    private static bool Matches(ReviewLibraryStepDto? supplied, ReviewLibraryStepDto expected)
        => supplied is not null
           && supplied.Id == expected.Id
           && supplied.Version == expected.Version
           && supplied.Digest == expected.Digest
           && supplied.InputSubjectSha == expected.InputSubjectSha
           && supplied.TimeoutSeconds == expected.TimeoutSeconds
           && supplied.Slot == expected.Slot
           && supplied.MaxDotNetCpuCount == expected.MaxDotNetCpuCount
           && supplied.RequiredCapabilities.SequenceEqual(expected.RequiredCapabilities, StringComparer.Ordinal);
}
