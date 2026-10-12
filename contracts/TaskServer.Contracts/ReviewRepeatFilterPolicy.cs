namespace AgentStudio.TaskServer.Contracts;

/// <summary>Checks that a clean repeat narrowed only to classes from the failed run.</summary>
public static class ReviewRepeatFilterPolicy
{
    public static bool IsValid(ReviewCommandEvidenceDto repeat, IReadOnlyList<ReviewCommandEvidenceDto> commands)
    {
        if (repeat.Phase != "clean-repeat")
            return repeat.RepeatFilter is null && repeat.RepeatObservedTests is null;
        if (repeat.RepeatFilter is null)
            return repeat.RepeatObservedTests is null;

        var candidate = commands.FirstOrDefault(command => command.StepId == repeat.StepId
            && command.Phase == "verification" && command.WorkspaceRole == "candidate");
        if (candidate is null || repeat.RepeatObservedTests is null) return false;
        var failed = (candidate.InitialFailures ?? (candidate.NewFailures ?? [])
            .Concat(candidate.PreExistingFailures ?? [])
            .Concat(candidate.FlakyQuarantinedFailures ?? []).ToArray())
            .Select(WithoutParameters)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (failed.Length is 0 or > 10 || failed.Any(name => !ValidTestName(name))) return false;
        var classes = failed.Select(name => name[..name.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (classes.Length is 0 or > 5) return false;
        var expectedFilter = string.Join('|', classes.Select(name => $"FullyQualifiedName~{name}."));
        if (!string.Equals(repeat.RepeatFilter, expectedFilter, StringComparison.Ordinal)
            || repeat.RepeatObservedTests.Any(name => !failed.Contains(name, StringComparer.Ordinal)))
            return false;
        return repeat.ExitCode != 0 || repeat.RepeatObservedTests.Order(StringComparer.Ordinal)
            .SequenceEqual(failed.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static string WithoutParameters(string name) => name.Split('(', 2)[0];

    private static bool ValidTestName(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator > 0 && separator < name.Length - 1
            && name.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '.' or '+' or '`');
    }
}
