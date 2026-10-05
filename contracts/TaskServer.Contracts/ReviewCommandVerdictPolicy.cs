namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Command steps are judged by exit status; verdict markers come only from
/// aspect replies (AGT-3016).
/// <para>
/// A deterministic review command (<c>verify-N</c>, a compose render, any
/// <see cref="ReviewCommandKinds.Tool"/> step) owns no verdict text. Its
/// output is a test or build log, and such a log can quote a literal
/// <c>[[ASPECT_VERDICT: ...]]</c> sentinel from test data. Reading that quote
/// as the step's verdict turned a green <c>dotnet test</c> into a
/// <c>concerns</c> build-tests row ("malformed: duplicate-key") and refused
/// the delivery gate on AGT-2954 and AGT-2996. The runner judges command
/// steps through <see cref="FromExitCode"/>; the task server repairs reports
/// from executors that still read markers out of command output through
/// <see cref="NormalizeReport"/>.
/// </para>
/// </summary>
public static class ReviewCommandVerdictPolicy
{
    public const string CommandPassed = "CommandPassed";
    public const string CommandFailed = "CommandFailed";

    /// <summary>Classification prefix the runner gives a parsed aspect marker.</summary>
    public const string AspectMarkerClassification = "RemoteAspectVerdict";

    /// <summary>Classification the runner gives an aspect reply without a parseable marker.</summary>
    public const string UnparseableClassification = "review:unparseable";

    private const string CommandEvidencePrefix = "command:";

    /// <summary>The only verdict a command step can produce from its own run.</summary>
    public static ReviewVerdictDto FromExitCode(string aspect, string stepId, int? exitCode)
    {
        var passed = exitCode == 0;
        return new ReviewVerdictDto(
            aspect,
            passed ? "pass" : "block",
            passed ? CommandPassed : CommandFailed,
            passed
                ? $"Review command '{stepId}' passed."
                : $"Review command '{stepId}' exited {exitCode?.ToString() ?? "without an exit code"}.",
            $"{CommandEvidencePrefix}{stepId}",
            passed ? "none" : $"successful execution of {stepId}");
    }

    /// <summary>
    /// True when a verdict was read from a verdict marker (or from a missing
    /// one) rather than derived from a command's exit status.
    /// </summary>
    public static bool IsMarkerDerived(ReviewVerdictDto verdict)
    {
        var classification = verdict.Classification ?? string.Empty;
        return classification.StartsWith(AspectMarkerClassification, StringComparison.OrdinalIgnoreCase)
               || classification.StartsWith(UnparseableClassification, StringComparison.OrdinalIgnoreCase)
               || classification.Contains("malformed:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces every marker-derived verdict on a tool-only aspect with the
    /// exit-status verdict of the command step it stands for. Aspects that
    /// have an agent command keep their verdicts untouched, so the malformed
    /// reply handling of real aspect replies is unchanged.
    /// </summary>
    public static ReviewReportRequest NormalizeReport(ReviewReportRequest request, ReviewPlanDto? plan)
    {
        var plannedCommands = plan?.Commands
            .Select(command => (command.StepId, command.Aspect, command.ExecutionKind))
            .ToArray()
            ?? request.Commands
                .Where(command => command.Phase == "verification" && command.WorkspaceRole == "candidate")
                .Select(command => (command.StepId, command.Aspect, command.ExecutionKind))
                .ToArray();
        var agentAspects = plannedCommands
            .Where(command => ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .Select(command => command.Aspect)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toolSteps = plannedCommands
            .Where(command => !ReviewCommandKinds.IsAgent(command.ExecutionKind)
                              && !agentAspects.Contains(command.Aspect))
            .GroupBy(command => command.Aspect, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(command => command.StepId).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);
        if (toolSteps.Count == 0 || !request.Verdicts.Any(verdict =>
                toolSteps.ContainsKey(verdict.Aspect) && IsMarkerDerived(verdict)))
            return request;

        // A command verdict cites its own step. The remaining steps of the
        // aspect, in plan order, are the ones a marker-derived verdict stood
        // for: the runner emits one verdict per command in plan order.
        var unclaimed = toolSteps.ToDictionary(
            pair => pair.Key,
            pair => new Queue<string>(pair.Value.Where(step => !request.Verdicts.Any(verdict =>
                string.Equals(verdict.Aspect, pair.Key, StringComparison.OrdinalIgnoreCase)
                && !IsMarkerDerived(verdict)
                && string.Equals(CitedStep(verdict), step, StringComparison.Ordinal)))),
            StringComparer.OrdinalIgnoreCase);
        var verdicts = request.Verdicts.Select(verdict =>
        {
            if (!IsMarkerDerived(verdict)
                || !unclaimed.TryGetValue(verdict.Aspect, out var steps)
                || !steps.TryDequeue(out var step))
                return verdict;
            var evidence = request.Commands.LastOrDefault(command =>
                string.Equals(command.StepId, step, StringComparison.Ordinal)
                && command.Phase == "verification"
                && command.WorkspaceRole == "candidate");
            return FromExitCode(verdict.Aspect, step, evidence?.ExitCode);
        }).ToArray();
        return request with { Verdicts = verdicts };
    }

    private static string? CitedStep(ReviewVerdictDto verdict)
    {
        var evidence = verdict.EvidenceChecked?.Trim();
        if (evidence is null || !evidence.StartsWith(CommandEvidencePrefix, StringComparison.Ordinal))
            return null;
        var step = evidence[CommandEvidencePrefix.Length..];
        var end = step.IndexOf(';');
        return (end < 0 ? step : step[..end]).Trim();
    }
}
