using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Canonical resource bounds for immutable review plans. Applying the limits
/// before a subject is stored keeps the executed command and its fenced
/// evidence identical while preventing one .NET review from occupying the
/// entire host.
/// <para>
/// AGT-2820 (2026-09-15): the cap now covers <c>dotnet build</c>, not only
/// <c>dotnet test</c>. Four parallel reviews each ran an uncapped
/// <c>dotnet build agent-taskboard.sln --no-restore</c>; the host carried 46
/// dotnet processes at load 38 on 12 cores and killed a run with exit 143,
/// while the test step immediately after was careful to pass
/// <c>-maxcpucount:2</c>. Capping the build deterministically was chosen over
/// admitting reviews against measured host load: the plan is frozen before an
/// executor claims it, so a load-derived cap would make the fenced command
/// depend on when it happened to be built, and the host's load is already a
/// separate admission gate (<c>ReviewSlotAdmissionPolicy</c>).
/// </para>
/// <para>
/// The same pass turns MSBuild node reuse off. A reused node outlives the build
/// that created it and is reparented to init when its review worker goes away:
/// 26 orphaned <c>/nodemode:1</c> nodes, the oldest idle for eight days, held
/// 2963 MB on one host. A review is a one-shot build; it has nothing to gain
/// from a persistent node pool.
/// </para>
/// </summary>
public static partial class ReviewPlanResourcePolicy
{
    public const int DefaultDotNetMaxCpuCount = 2;

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
            && IsCappedVerb(verb))
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
        var isTest = string.Equals(source[0], "test", StringComparison.OrdinalIgnoreCase);
        var filtered = new List<string>(source.Count + 3) { source[0] };
        for (var index = 1; index < source.Count; index++)
        {
            var argument = source[index];
            if (MaxCpuArgument().IsMatch(argument)
                || NodeReuseArgument().IsMatch(argument)
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
        filtered.InsertRange(1, LimitArguments(maxCpuCount, isTest));
        return filtered;
    }

    private static string LimitShellCommand(string shellCommand, int maxCpuCount)
    {
        if (!DotNetBuildOrTest().IsMatch(shellCommand)) return shellCommand;
        var limited = MaxCpuShellArgument().Replace(shellCommand, string.Empty);
        limited = NodeReuseShellArgument().Replace(limited, string.Empty);
        limited = TestCollectionParallelismShellArgument().Replace(limited, string.Empty);
        limited = CollapseUnquotedHorizontalWhitespace(limited);
        return DotNetBuildOrTest().Replace(
            limited,
            match =>
            {
                var isTest = match.Value.EndsWith("test", StringComparison.OrdinalIgnoreCase);
                return $"{match.Value} {string.Join(' ', LimitArguments(maxCpuCount, isTest))}";
            });
    }

    /// <summary>
    /// The bounds, in one place so the shell form and the argv form cannot
    /// drift. Collection parallelism is a test-only knob; the CPU cap and the
    /// node-reuse switch apply to every MSBuild invocation a review makes.
    /// </summary>
    private static string[] LimitArguments(int maxCpuCount, bool isTest)
        => isTest
            ? [$"-maxcpucount:{maxCpuCount}", "-nodeReuse:false", "-p:ParallelizeTestCollections=false"]
            : [$"-maxcpucount:{maxCpuCount}", "-nodeReuse:false"];

    private static bool IsCappedVerb(string verb)
        => string.Equals(verb, "test", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verb, "build", StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"(?<![\w./-])dotnet\s+(?:test|build)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetBuildOrTest();

    [GeneratedRegex(@"^(?:-{1,2}nodeReuse|/nodeReuse):(?:true|false)$", RegexOptions.IgnoreCase)]
    private static partial Regex NodeReuseArgument();

    [GeneratedRegex(@"(?<!\S)(?:-{1,2}nodeReuse|/nodeReuse):(?:true|false)", RegexOptions.IgnoreCase)]
    private static partial Regex NodeReuseShellArgument();

    [GeneratedRegex(@"(?<!\S)(?:-{1,2}maxcpucount|/maxcpucount|-[mM])(?::\d+|\s+\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxCpuShellArgument();

    [GeneratedRegex(@"(?<!\S)(?:-[pP]:|/[pP]:|--property:?)ParallelizeTestCollections=(?:true|false)", RegexOptions.IgnoreCase)]
    private static partial Regex TestCollectionParallelismShellArgument();

}
