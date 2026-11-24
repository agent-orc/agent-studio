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

    public static bool Supports(ReviewPlanDto plan, IReadOnlySet<string> capabilities, bool routePending = false)
        => plan.LibraryVersion == 0
            ? plan.Commands.All(command => command.LibraryStep is null)
              && (plan.Preparation ?? []).All(command => command.LibraryStep is null)
            : plan.LibraryVersion == Version
              && plan.Commands.All(command => command.LibraryStep is not null)
              && (plan.Preparation ?? []).All(command => command.LibraryStep is not null)
              && ExecutionCapabilities(plan, routePending).All(capabilities.Contains);

    /// <summary>
    /// Every toolchain key a sealed step can require, with the invocation tokens
    /// that trigger it and the executable a host probes to satisfy it. Review
    /// executors register from this table (AGT-2987), so a new key added here
    /// reaches the registration set in the same release as the plans that
    /// require it.
    /// </summary>
    public static IReadOnlyList<ReviewToolchainRequirement> ToolchainRequirements { get; } =
    [
        new(CapabilityProtocol.DotNet, "dotnet", ["dotnet"]),
        new(CapabilityProtocol.Node, "node", ["node", "npm", "npx"]),
        new(CapabilityProtocol.Playwright, "playwright", ["playwright"]),
    ];

    /// <summary>
    /// Why <see cref="Supports"/> refuses <paramref name="plan"/> for
    /// <paramref name="capabilities"/>: the required keys the executor lacks,
    /// or the library-step version key when the envelope itself is unusable.
    /// Empty exactly when the plan is supported.
    /// </summary>
    public static IReadOnlyList<string> MissingCapabilities(ReviewPlanDto plan, IReadOnlySet<string> capabilities, bool routePending = false)
    {
        if (Supports(plan, capabilities, routePending)) return [];
        var missing = ExecutionCapabilities(plan, routePending).Where(key => !capabilities.Contains(key)).ToArray();
        return missing.Length > 0 ? missing : [$"review:library-step:v{plan.LibraryVersion}"];
    }

    public static IReadOnlyList<string> RequiredCapabilities(ReviewPlanDto plan)
        => Steps(plan).SelectMany(step => step.RequiredCapabilities)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Provider requirements follow the selected execution route, separately from the authored digest.</summary>
    public static IReadOnlyList<string> ExecutionCapabilities(ReviewPlanDto plan, bool routePending = false)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in plan.Commands)
        {
            foreach (var key in command.LibraryStep?.RequiredCapabilities ?? [])
                if (!ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    || command.ModelSelection is null && !routePending
                    || !key.StartsWith("cli-execution:", StringComparison.Ordinal)
                       && !key.StartsWith("provider-auth:", StringComparison.Ordinal)) keys.Add(key);
            if (ReviewCommandKinds.IsAgent(command.ExecutionKind) && command.ModelSelection is { ShouldLaunch: true } selected)
            {
                keys.Add(CapabilityProtocol.CliExecution(selected.Route.CliType));
                keys.Add(CapabilityProtocol.ProviderAuthentication(selected.Route.CliType));
            }
        }
        foreach (var preparation in plan.Preparation ?? [])
            foreach (var key in preparation.LibraryStep?.RequiredCapabilities ?? []) keys.Add(key);
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    public static bool ValidPlan(ReviewPlanDto plan, string subjectSha)
        => plan.LibraryVersion == 0
            ? plan.Commands.All(command => command.LibraryStep is null)
              && (plan.Preparation ?? []).All(command => command.LibraryStep is null)
            : plan.LibraryVersion == Version
              && plan.Commands.All(command => ValidSelection(command) && Matches(command.LibraryStep, ForCommand(command, subjectSha)))
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
        command = ConfiguredCommand(command);
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
            JsonSerializer.Serialize(ConfiguredCommand(command) with { LibraryStep = null }));
    }

    // The library digest binds the authored command. A claim-scoped routing
    // receipt describes its execution without changing that frozen payload.
    private static bool ValidSelection(ReviewCommandDto command)
        => command.ModelSelection is not { } selection || selection.ShouldLaunch
            && command.FileName == selection.Route.CliType && command.CliType == selection.Route.CliType
            && command.Model == selection.Route.Model && command.ThinkingLevel == selection.Route.ThinkingLevel;

    private static ReviewCommandDto ConfiguredCommand(ReviewCommandDto command)
        => command.ModelSelection is not { } selection ? command : command with
        {
            FileName = selection.Primary.CliType,
            CliType = selection.Primary.CliType,
            Model = selection.Primary.Model,
            ThinkingLevel = selection.Primary.ThinkingLevel,
            ModelSelection = null,
        };

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
        foreach (var requirement in ToolchainRequirements)
        {
            if (requirement.InvocationTools.Any(tool => ContainsTool(invocation, tool)))
                capabilities.Add(requirement.Key);
        }
        // AGT-2981: the render scripts call `docker compose config` and node.
        if (ComposeRenderGatePolicy.IsRenderInvocation(invocation))
        {
            capabilities.Add(CapabilityProtocol.ComposeRender);
            capabilities.Add(CapabilityProtocol.Node);
        }
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

/// <summary>One toolchain key of the review library and how a host proves it.</summary>
public sealed record ReviewToolchainRequirement(
    string Key,
    string ProbeExecutable,
    IReadOnlyList<string> InvocationTools);
