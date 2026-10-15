using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// AGT-3060 (I18, AGT-W65 D14 A): no transition decision may return to
/// <c>backend/</c>. A production host is engine-only, the moved background
/// deciders call no decision policy, and every remaining monolith call site or
/// host-defined policy is frozen in a baseline that only shrinks.
/// </summary>
[Trait("Category", "Guard")]
public sealed class MonolithDecisionBoundaryTests
{
    private const string BaselinePath = "backend.Tests/Architecture/Fixtures/monolith-decision-baseline.txt";
    private static readonly Regex WorkflowFolder = new(
        @"^backend/(Features/(Runner|Pipeline|Tasks|Supervisor)|Shared/Runner)/", RegexOptions.Compiled);
    private static readonly Regex HostPolicy = new(@"\bclass\s+(\w+(?:Policy|Rules|Breaker))\b", RegexOptions.Compiled);

    /// <summary>The background deciders AGT-3060 moved into the engine; they submit facts only.</summary>
    private static readonly string[] MovedDeciders =
    [
        "backend/Features/Runner/RunLivenessMonitor.cs",
        "backend/Features/Runner/StaleProgressArchiver.cs",
        "backend/Features/Runner/SteerTimeoutMonitor.cs",
        "backend/Features/Runner/RunLivenessMonitorHostedService.cs",
        "backend/Features/Runner/StaleProgressSweepHostedService.cs",
        "backend/Features/Runner/SteerTimeoutMonitorHostedService.cs",
        "backend/Features/Pipeline/GateEnvironmentRetry/GateEnvironmentRetryHostedService.cs",
        "backend/Features/Runner/OperatorSweeps/OperatorSweepHostedService.cs",
    ];

    [Fact]
    public void Production_host_pins_the_decision_authority_to_the_engine()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "backend", "Host", "Program.cs"));
        Assert.Matches(new Regex(
            @"if \(!builder\.Environment\.IsEnvironment\(""Test""\) && !underTestHost\)\s*"
            + @"OrchestrationDecisionAuthority\.RequireEngine\(builder\.Configuration\);"), program);
    }

    [Fact]
    public void Moved_background_deciders_call_no_decision_policy()
    {
        var root = RepositoryRoot.Find();
        var pattern = DecisionCall();
        foreach (var file in MovedDeciders)
        {
            var source = StripComments(File.ReadAllText(Path.Combine(root, file)));
            Assert.False(pattern.IsMatch(source), $"{file} decides in the monolith: {pattern.Match(source).Value}");
        }
    }

    [Fact]
    public void Monolith_decision_code_does_not_grow()
    {
        var root = RepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText);
        var baseline = Baseline.Load(Path.Combine(root, BaselinePath));
        var violations = Violations(sources, baseline);
        // The list only shrinks: a removed call or policy must leave the baseline too.
        var calls = DecisionCall();
        violations.AddRange(baseline.Calls
            .Where(entry => (sources.TryGetValue(entry.Key, out var source)
                ? calls.Matches(StripComments(source)).Count : 0) < entry.Value)
            .Select(entry => $"{entry.Key} has fewer decision calls than its baseline {entry.Value}; lower it"));
        violations.AddRange(baseline.Policies
            .Where(entry => !sources.TryGetValue(entry.Split(' ')[0], out var source)
                || !Regex.IsMatch(StripComments(source), @"\bclass\s+" + Regex.Escape(entry.Split(' ')[1]) + @"\b"))
            .Select(entry => $"{entry} is gone; remove it from the baseline"));
        Assert.True(violations.Count == 0,
            "Transition decisions belong in orchestration-decisions and run in the Orchestrator Engine "
            + "(submit facts through AiOperatorDecisionGateway). " + string.Join("; ", violations));
    }

    [Theory]
    [InlineData("backend/Features/Runner/NewSweep.cs", "var verdict = RunLivenessPolicy.Decide(facts);")]
    [InlineData("backend/Features/Runner/NewSweep.cs", "var verdict = MonolithTransitionAdapter.Operator(payload);")]
    [InlineData("backend/Features/Runner/RunLivenessMonitor.cs", "return StaleProgressPolicy.Decide(facts);")]
    [InlineData("backend/Features/Tasks/NewRail.cs", "public static class ReviewRetryPolicy { }")]
    [InlineData("backend/Features/Supervisor/NewCycle.cs", "internal sealed class NightlyRules { }")]
    public void Guard_rejects_a_reintroduced_monolith_decision(string path, string source)
    {
        var violations = Violations(new Dictionary<string, string> { [path] = source }, Baseline.Empty);
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void Guard_rejects_a_grown_call_site_and_accepts_a_baselined_one()
    {
        var baseline = new Baseline(new Dictionary<string, int> { ["backend/Features/Tasks/Old.cs"] = 1 },
            new HashSet<string>(StringComparer.Ordinal));
        const string one = "var a = TransitionDecisionPolicy.CompletionLane(outcome, false);";
        Assert.Empty(Violations(new Dictionary<string, string> { ["backend/Features/Tasks/Old.cs"] = one }, baseline));
        Assert.NotEmpty(Violations(new Dictionary<string, string>
        { ["backend/Features/Tasks/Old.cs"] = one + "\n" + one }, baseline));
    }

    private static List<string> Violations(IReadOnlyDictionary<string, string> sources, Baseline baseline)
    {
        var violations = new List<string>();
        var calls = DecisionCall();
        foreach (var (path, raw) in sources)
        {
            var source = StripComments(raw);
            var count = calls.Matches(source).Count;
            var allowed = baseline.Calls.GetValueOrDefault(path);
            if (count > allowed)
                violations.Add($"{path} has {count} decision call(s), baseline {allowed}");
            if (!WorkflowFolder.IsMatch(path)) continue;
            foreach (Match match in HostPolicy.Matches(source))
                if (!baseline.Policies.Contains($"{path} {match.Groups[1].Value}"))
                    violations.Add($"{path} defines {match.Groups[1].Value}");
        }
        return violations;
    }

    /// <summary>Calls into any static policy or breaker of the decision library, and the test-host adapter.</summary>
    private static Regex DecisionCall()
    {
        var names = typeof(TransitionDecisionPolicy).Assembly.GetExportedTypes()
            .Where(type => type.IsAbstract && type.IsSealed
                && (type.Name.EndsWith("Policy", StringComparison.Ordinal)
                    || type.Name.EndsWith("Breaker", StringComparison.Ordinal)))
            .Select(type => type.Name)
            .Append("MonolithTransitionAdapter")
            .Order(StringComparer.Ordinal);
        return new Regex(@"\b(?:" + string.Join("|", names) + @")\.\w+\s*\(");
    }

    private static string StripComments(string source) => Regex.Replace(source, "//.*", string.Empty);

    private sealed record Baseline(IReadOnlyDictionary<string, int> Calls, IReadOnlySet<string> Policies)
    {
        public static Baseline Empty { get; } = new(new Dictionary<string, int>(), new HashSet<string>());

        public static Baseline Load(string path)
        {
            var calls = new Dictionary<string, int>(StringComparer.Ordinal);
            var policies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path).Select(item => item.Trim())
                         .Where(item => item.Length > 0 && !item.StartsWith('#')))
            {
                var parts = line.Split(' ', 3);
                if (parts is ["call", var file, var count]) calls[file] = int.Parse(count);
                else if (parts is ["policy", var owner, var type]) policies.Add($"{owner} {type}");
                else throw new InvalidDataException($"Malformed baseline line: {line}");
            }
            return new(calls, policies);
        }
    }
}
