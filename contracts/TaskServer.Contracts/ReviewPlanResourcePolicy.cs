using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Canonical resource bounds for immutable review plans. Applying the limits
/// before a subject is stored keeps the executed command and its fenced
/// evidence identical while preventing one .NET review from occupying the
/// entire host.
/// </summary>
public static partial class ReviewPlanResourcePolicy
{
    public const int DefaultDotNetMaxCpuCount = 2;
    public const string DefaultGateTestFilter = "Category!=MachineBound&Category!=LiveCli";

    public static ReviewPlanDto Apply(
        ReviewPlanDto plan,
        int dotNetMaxCpuCount = DefaultDotNetMaxCpuCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dotNetMaxCpuCount, 1);
        var changed = false;
        var commands = plan.Commands.Select(command =>
        {
            var limited = Apply(command, dotNetMaxCpuCount);
            changed |= !ReferenceEquals(limited, command);
            return limited;
        }).ToArray();
        var preparation = (plan.Preparation ?? []).Select(command =>
        {
            var limited = Apply(command, dotNetMaxCpuCount);
            changed |= !ReferenceEquals(limited, command);
            return limited;
        }).ToArray();
        return changed
            ? plan with { Commands = commands, Preparation = preparation }
            : plan;
    }

    private static ReviewPreparationCommandDto Apply(
        ReviewPreparationCommandDto command,
        int maxCpuCount)
    {
        if (!IsShell(command.FileName)) return command;
        var arguments = command.Arguments.ToArray();
        var changed = false;
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index] is not ("-c" or "-lc")) continue;
            var limited = LimitShellCommand(arguments[index + 1], maxCpuCount);
            if (string.Equals(limited, arguments[index + 1], StringComparison.Ordinal)) continue;
            arguments[index + 1] = limited;
            changed = true;
        }
        return changed ? command with { Arguments = arguments } : command;
    }

    private static ReviewCommandDto Apply(ReviewCommandDto command, int maxCpuCount)
    {
        if (IsDotNet(command.FileName)
            && command.Arguments.FirstOrDefault() is { } verb
            && string.Equals(verb, "test", StringComparison.OrdinalIgnoreCase))
        {
            var directArguments = LimitDirectArguments(command.Arguments, maxCpuCount);
            return command.Arguments.SequenceEqual(directArguments, StringComparer.Ordinal)
                ? command
                : command with { Arguments = directArguments };
        }

        if (!IsShell(command.FileName)) return command;
        var arguments = command.Arguments.ToArray();
        var changed = false;
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index] is not ("-c" or "-lc")) continue;
            var limited = LimitShellCommand(arguments[index + 1], maxCpuCount);
            if (string.Equals(limited, arguments[index + 1], StringComparison.Ordinal)) continue;
            arguments[index + 1] = limited;
            changed = true;
        }
        return changed ? command with { Arguments = arguments } : command;
    }

    private static IReadOnlyList<string> LimitDirectArguments(
        IReadOnlyList<string> source,
        int maxCpuCount)
    {
        var filtered = new List<string>(source.Count + 2) { source[0] };
        for (var index = 1; index < source.Count; index++)
        {
            var argument = source[index];
            if (MaxCpuArgument().IsMatch(argument)
                || TestCollectionParallelismArgument().IsMatch(argument))
                continue;
            if (argument is "--maxcpucount" or "-maxcpucount"
                && index + 1 < source.Count
                && int.TryParse(source[index + 1], out _))
            {
                index++;
                continue;
            }
            filtered.Add(argument);
        }
        filtered.Insert(1, $"-maxcpucount:{maxCpuCount}");
        filtered.Insert(2, "-p:ParallelizeTestCollections=false");
        ApplyDefaultCategoryFilter(filtered);
        return filtered;
    }

    private static string LimitShellCommand(string shellCommand, int maxCpuCount)
    {
        if (!DotNetTest().IsMatch(shellCommand)) return shellCommand;
        var limited = MaxCpuShellArgument().Replace(shellCommand, string.Empty);
        limited = TestCollectionParallelismShellArgument().Replace(limited, string.Empty);
        limited = CollapseUnquotedHorizontalWhitespace(limited);
        limited = ApplyDefaultCategoryFilter(limited);
        limited = DotNetTest().Replace(
            limited,
            match => $"{match.Value} -maxcpucount:{maxCpuCount} -p:ParallelizeTestCollections=false");
        return limited;
    }

    private static void ApplyDefaultCategoryFilter(List<string> arguments)
    {
        var index = arguments.FindIndex(argument => argument is "--filter" or "-f");
        if (index >= 0 && index + 1 < arguments.Count)
        {
            var value = arguments[index + 1];
            if (!value.Contains("Category!=MachineBound", StringComparison.OrdinalIgnoreCase))
                value += "&Category!=MachineBound";
            if (!value.Contains("Category!=LiveCli", StringComparison.OrdinalIgnoreCase))
                value += "&Category!=LiveCli";
            arguments[index + 1] = value;
            return;
        }
        arguments.Add("--filter");
        arguments.Add(DefaultGateTestFilter);
    }

    private static string ApplyDefaultCategoryFilter(string shellCommand)
    {
        var filter = DotNetTestFilterArgument().Match(shellCommand);
        if (filter.Success)
        {
            var value = filter.Groups["value"].Value.Trim('"', '\'');
            if (!value.Contains("Category!=MachineBound", StringComparison.OrdinalIgnoreCase))
                value += "&Category!=MachineBound";
            if (!value.Contains("Category!=LiveCli", StringComparison.OrdinalIgnoreCase))
                value += "&Category!=LiveCli";
            return shellCommand[..filter.Index]
                   + $"--filter \"{value}\""
                   + shellCommand[(filter.Index + filter.Length)..];
        }
        return DotNetTest().Replace(
            shellCommand,
            match => $"{match.Value} --filter \"{DefaultGateTestFilter}\"");
    }

    private static string CollapseUnquotedHorizontalWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var quote = '\0';
        var escaped = false;
        foreach (var current in value)
        {
            if (escaped)
            {
                result.Append(current);
                escaped = false;
                continue;
            }
            if (current == '\\' && quote != '\'')
            {
                result.Append(current);
                escaped = true;
                continue;
            }
            if (current is '\'' or '"')
            {
                if (quote == '\0') quote = current;
                else if (quote == current) quote = '\0';
                result.Append(current);
                continue;
            }
            if (quote == '\0' && current is ' ' or '\t')
            {
                if (result.Length > 0 && result[^1] is not (' ' or '\t'))
                    result.Append(' ');
                continue;
            }
            result.Append(current);
        }
        return result.ToString().Trim();
    }

    private static bool IsDotNet(string fileName)
        => string.Equals(
            Path.GetFileNameWithoutExtension(fileName),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsShell(string fileName)
        => Path.GetFileName(fileName) is "sh" or "bash" or "dash";

    [GeneratedRegex(@"^(?:-{1,2}maxcpucount|/maxcpucount|-[mM]):\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex MaxCpuArgument();

    [GeneratedRegex(@"^(?:-[pP]:|/[pP]:|--property:?)ParallelizeTestCollections=(?:true|false)$", RegexOptions.IgnoreCase)]
    private static partial Regex TestCollectionParallelismArgument();

    [GeneratedRegex(@"(?<![\w./-])dotnet\s+test\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetTest();

    [GeneratedRegex(@"(?<!\S)(?:-{1,2}maxcpucount|/maxcpucount|-[mM])(?::\d+|\s+\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxCpuShellArgument();

    [GeneratedRegex(@"(?<!\S)(?:-[pP]:|/[pP]:|--property:?)ParallelizeTestCollections=(?:true|false)", RegexOptions.IgnoreCase)]
    private static partial Regex TestCollectionParallelismShellArgument();

    [GeneratedRegex("(?:--filter|-f)\\s+(?<value>\"[^\"]*\"|'[^']*'|[^\\s;&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetTestFilterArgument();

}
