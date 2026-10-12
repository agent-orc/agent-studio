using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Limits a clean repeat to the classes that produced the original red test results.</summary>
internal static partial class ReviewTargetedRerunPolicy
{
    internal static ReviewCommandDto Select(
        ReviewCommandDto command, IReadOnlyList<string> failures, string output = "", string? signal = null)
    {
        if (signal is not null || AbortedRun().IsMatch(output)) return command;
        var names = failures
            .Select(name => name.Split('(', 2)[0])
            .ToArray();
        if (failures.Count is 0 or > 10 || names.Any(name => !TestName().IsMatch(name)))
            return command;
        var classes = names
            .Select(name => name[..name.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (classes.Length is 0 or > 5)
            return command;

        var classFilter = ClassFilter(failures);
        if (classFilter is null) return command;
        if (Path.GetFileNameWithoutExtension(command.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && command.Arguments.FirstOrDefault() == "test")
        {
            var args = command.Arguments.ToList();
            var original = RemoveFilter(args);
            args.Add("--filter");
            args.Add(Combine(original, classFilter));
            return command with { Arguments = args };
        }

        if (Path.GetFileName(command.FileName) is not ("sh" or "bash" or "dash")) return command;
        var arguments = command.Arguments.ToArray();
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index] is not ("-c" or "-lc")) continue;
            var shell = arguments[index + 1].Trim();
            if (!shell.StartsWith("dotnet test ", StringComparison.OrdinalIgnoreCase)
                || ShellChain().IsMatch(shell)) return command;
            var filter = ShellFilter().Match(shell);
            var original = filter.Success ? filter.Groups["value"].Value.Trim('"', '\'') : null;
            if (original is not null && original.IndexOfAny(['"', '\'', '`', '$']) >= 0)
                return command;
            var rerun = filter.Success ? shell.Remove(filter.Index, filter.Length) : shell;
            arguments[index + 1] = rerun.TrimEnd() + $" --filter \"{Combine(original, classFilter)}\"";
            return command with { Arguments = arguments };
        }
        return command;
    }

    internal static string? ClassFilter(IReadOnlyList<string> failures)
    {
        var names = failures.Select(name => name.Split('(', 2)[0]).ToArray();
        if (failures.Count is 0 or > 10 || names.Any(name => !TestName().IsMatch(name)))
            return null;
        var classes = names.Select(name => name[..name.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return classes.Length is 0 or > 5 ? null
            : string.Join('|', classes.Select(name => $"FullyQualifiedName~{name}."));
    }

    internal static ReviewCommandDto? PrecedingBuild(ReviewPlanDto plan, ReviewCommandDto test)
        => plan.Commands.TakeWhile(command => command.StepId != test.StepId)
            .LastOrDefault(command => Path.GetFileName(command.FileName) is "dotnet" or "sh" or "bash" or "dash"
                && command.Arguments.Any(argument => argument.Contains("dotnet build", StringComparison.Ordinal)
                    || argument == "build"));

    internal static IReadOnlyList<string> ObservedPassingFailures(
        string output, IReadOnlyList<string> failures)
    {
        var passed = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => PassedTest().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value.Split('(', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
        return failures.Select(name => name.Split('(', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .Where(passed.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? RemoveFilter(List<string> arguments)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] != "--filter") continue;
            var original = arguments[index + 1];
            arguments.RemoveRange(index, 2);
            return original;
        }
        return null;
    }

    private static string Combine(string? original, string classFilter)
        => string.IsNullOrWhiteSpace(original) ? classFilter : $"({original})&({classFilter})";

    [GeneratedRegex(@"^[A-Za-z0-9_.+`]+\.[A-Za-z0-9_+`]+$")]
    private static partial Regex TestName();

    [GeneratedRegex(@"\s(?:&&|\|\||\||;)\s")]
    private static partial Regex ShellChain();

    [GeneratedRegex(@"\s--filter\s+(?<value>""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ShellFilter();

    [GeneratedRegex(@"\b(?:Test Run Aborted|active test run was aborted|test host (?:crashed|terminated)|blame.*hang|process.*killed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbortedRun();

    [GeneratedRegex(@"^\s*Passed\s+(?<name>.+?)\s+\[[^\]]+\]\s*$")]
    private static partial Regex PassedTest();
}
