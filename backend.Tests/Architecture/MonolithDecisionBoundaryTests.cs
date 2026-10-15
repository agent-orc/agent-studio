using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// AGT-3060 (I18) with the operator decision of 15.10.2026: the per-transition
/// switch is the single selector of who decides, and its default stays
/// Monolith. Every background decider of <c>backend/</c> therefore reaches its
/// rule through the shared decision library behind a switch profile: an AI
/// Operator decider through <see cref="AiOperatorDecisionGateway"/> or a lane
/// profile through <see cref="MonolithTransitionSwitch"/>. No hosted service may
/// decide around the switch, a routed decider calls no library rule directly,
/// and no new host-defined rule may appear.
/// </summary>
[Trait("Category", "Guard")]
public sealed class MonolithDecisionBoundaryTests
{
    private const string InventoryPath = "docs/system/architecture/monolith-hosted-services.md";
    private const string Operator = "decide, operator";
    private const string Switched = "decide, switched";
    private const string Local = "decide, local";

    /// <summary><c>AddHostedService&lt;T&gt;()</c> and <c>AddHostedService(sp =&gt; sp.GetRequiredService&lt;T&gt;())</c>.</summary>
    private static readonly Regex HostedRegistration = new(
        @"AddHostedService\s*(?:<(?:[\w.]+\.)?(?<type>\w+)>|\(\s*\w+\s*=>\s*\w+\s*\.\s*GetRequiredService<(?:[\w.]+\.)?(?<type>\w+)>)",
        RegexOptions.Compiled);

    /// <summary>A hosted service whose decision runs in the named service it drives.</summary>
    private static readonly IReadOnlyDictionary<string, string> DecidesThrough = new Dictionary<string, string>
    {
        ["AutoReviewPostProcessingWorker"] = "ReviewDecisionOrchestrator",
        ["BatchGatePilotHostedService"] = "BatchGatePilotService",
        ["CauseBreakerHostedService"] = "CauseBreakerService",
        ["GateEnvironmentRetryHostedService"] = "GateEnvironmentRetryService",
        ["OperatorSweepHostedService"] = "OperatorSweepService",
        ["RunLivenessMonitorHostedService"] = "RunLivenessMonitor",
        ["StaleProgressSweepHostedService"] = "StaleProgressArchiver",
        ["SteerTimeoutMonitorHostedService"] = "SteerTimeoutMonitor",
    };

    /// <summary>
    /// The local runner and three supervisor deciders without engine
    /// occurrences remain on the monolith path. The set is closed.
    /// </summary>
    private static readonly IReadOnlySet<string> LocalDeciders = new HashSet<string>(StringComparer.Ordinal)
    {
        "TaskRunnerService",
        "OrchestratorPrepHostedService",
        "MetaCycleHostedService",
        "AutoInterventionHostedService",
    };

    /// <summary>
    /// Library helpers a routed decider may call: they validate an operator's
    /// Retry integration intent or word a park reason; they choose no transition.
    /// </summary>
    private static readonly IReadOnlySet<string> LibraryHelpers = new HashSet<string>(StringComparer.Ordinal)
    {
        "MonolithTransitionAdapter.GateEnvironmentRetryPolicyOperatorReplayRefusal",
        "MonolithTransitionAdapter.GateEnvironmentRetryPolicyParkedReason",
    };

    private static readonly Regex WorkflowFolder = new(
        @"^backend/(Features/(Runner|Pipeline|Tasks|Supervisor)|Shared/Runner)/", RegexOptions.Compiled);
    private static readonly Regex HostTransitionRule = new(
        @"\b(?:new\s+(?:TransitionDecision|AiOperatorDecision)\s*\(|class\s+\w*TransitionDecision\w*\b)",
        RegexOptions.Compiled);

    [Fact]
    public void Production_host_does_not_force_engine_only_decisions()
    {
        var program = StripComments(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "backend", "Host", "Program.cs")));
        Assert.DoesNotMatch(new Regex(@"RequireEngine|DecisionAuthority|Transitions:\w+""\]\s*=\s*""Engine"""), program);
    }

    [Fact]
    public void Default_switch_keeps_every_profile_on_the_monolith_and_honors_explicit_modes()
    {
        var empty = new ConfigurationBuilder().Build();
        foreach (var profile in OrchestrationEngineReadiness.EngineProfiles)
            Assert.Equal(OrchestrationExecutionMode.Monolith, MonolithTransitionSwitch.For(empty, profile));

        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestration:ExecutionMode"] = "Engine",
            ["Orchestration:Transitions:GateResult"] = "Monolith",
            [$"Orchestration:Transitions:{AiOperatorDecisionGateway.Transition}"] = "Engine",
        }).Build();
        Assert.Equal(OrchestrationExecutionMode.Monolith, MonolithTransitionSwitch.For(configured, "GateResult"));
        Assert.Equal(OrchestrationExecutionMode.Engine,
            MonolithTransitionSwitch.For(configured, AiOperatorDecisionGateway.Transition));
        Assert.False(new AiOperatorDecisionGateway(empty).EngineDecides);
        Assert.True(new AiOperatorDecisionGateway(configured).EngineDecides);
    }

    [Fact]
    public void Inventory_lists_every_registered_hosted_service()
    {
        var root = RepositoryRoot.Find();
        var inventory = Inventory.Load(Path.Combine(root, InventoryPath));
        var registered = BackendSources(root).Values
            .SelectMany(source => HostedRegistration.Matches(StripComments(source)))
            .Select(match => match.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(registered);
        Assert.Empty(registered.Where(type => !inventory.ContainsKey(type)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_hosted_decider_routes_its_decision_through_the_switch()
    {
        var root = RepositoryRoot.Find();
        var inventory = Inventory.Load(Path.Combine(root, InventoryPath));

        Assert.Empty(RoutingViolations(inventory, BackendSources(root)));
    }

    [Fact]
    public void Routed_deciders_call_no_decision_rule_directly()
    {
        var root = RepositoryRoot.Find();
        var inventory = Inventory.Load(Path.Combine(root, InventoryPath));
        var sources = BackendSources(root);
        var calls = DecisionCall();
        var violations = new List<string>();
        foreach (var service in inventory.Where(row => row.Value == Operator).Select(row => row.Key))
        foreach (var owner in new[] { service, DecidesThrough.GetValueOrDefault(service, service) }.Distinct())
        foreach (var (path, source) in DeclaringFiles(sources, owner))
        {
            var stripped = StripComments(source);
            foreach (Match call in calls.Matches(stripped))
                if (!LibraryHelpers.Contains(Regex.Replace(call.Value, @"[\s(]", string.Empty)))
                    violations.Add($"{path} ({service}) decides around the switch: {call.Value}");
            if (HostTransitionRule.IsMatch(stripped))
                violations.Add($"{path} ({service}) defines a transition decision");
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void Moved_transition_rules_cannot_be_redeclared_in_backend()
    {
        var sources = BackendSources(RepositoryRoot.Find());
        string[] movedRules =
        [
            "RunLivenessPolicy", "StaleProgressPolicy", "SteerTimeoutPolicy",
            "RunTimeoutSalvageContinuationPolicy", "GateEnvironmentRetryPolicy",
            "DeliveryChainPolicy", "CauseBreakerClosePolicy", "IntakeRoutingPolicy",
            "AcceptanceRailDecisionPolicy", "GateCancellationPolicy",
        ];
        var violations = sources.SelectMany(file => movedRules
            .Where(rule => Regex.IsMatch(StripComments(file.Value),
                @"\bclass\s+" + Regex.Escape(rule) + @"\b"))
            .Select(rule => $"{file.Key} redeclares {rule}"));
        Assert.Empty(violations);
    }

    [Fact]
    public void Backend_cannot_define_transition_decisions()
    {
        var root = RepositoryRoot.Find();
        var sources = BackendSources(root);
        var violations = PolicyViolations(sources);
        Assert.True(violations.Count == 0,
            "Transition rules belong in orchestration-decisions and are reached through a switch profile "
            + "(AiOperatorDecisionGateway or MonolithTransitionSwitch). " + string.Join("; ", violations));
    }

    [Theory]
    [InlineData("decide", "")]
    [InlineData("decide, held", "")]
    [InlineData("decide, local", "")]
    [InlineData(Operator, "")]
    [InlineData(Operator, "var verdict = DeliveryChainPolicy.Decide(facts);")]
    [InlineData(Switched, "")]
    [InlineData(Switched, "var mode = OrchestrationExecutionMode.Monolith;")]
    public void Guard_rejects_a_hosted_decider_that_bypasses_the_switch(string decisionClass, string body)
    {
        var inventory = new Dictionary<string, string> { ["NewSweepHostedService"] = decisionClass };
        var sources = new Dictionary<string, string>
        {
            ["backend/Features/Runner/NewSweepHostedService.cs"] = "public sealed class NewSweepHostedService { " + body + " }",
        };
        Assert.NotEmpty(RoutingViolations(inventory, sources));
    }

    [Theory]
    [InlineData(Operator, "_decisions.Submit(new AiOperatorDecisionRequest(p, t, s, payload, null)); AiOperatorDecisionGateway g;")]
    [InlineData(Switched, "MonolithTransitionSwitch.For(_configuration, \"GateResult\")")]
    public void Guard_accepts_a_routed_decider(string decisionClass, string body)
    {
        var inventory = new Dictionary<string, string> { ["NewSweepHostedService"] = decisionClass };
        var sources = new Dictionary<string, string>
        {
            ["backend/Features/Runner/NewSweepHostedService.cs"] = "public sealed class NewSweepHostedService { " + body + " }",
        };
        Assert.Empty(RoutingViolations(inventory, sources));
    }

    [Theory]
    [InlineData("backend/Features/Tasks/NewRail.cs", "return new TransitionDecision(action, reason);")]
    [InlineData("backend/Features/Supervisor/NewCycle.cs", "internal sealed class NewTransitionDecisionRules { }")]
    [InlineData("backend/Features/Tasks/Acceptance/DeliveryChainReconciler.cs", "return new AiOperatorDecision(d, s, a, c, e, r, t);")]
    public void Guard_rejects_a_reintroduced_host_rule(string path, string source)
        => Assert.NotEmpty(PolicyViolations(new Dictionary<string, string> { [path] = source }));

    /// <summary>
    /// A <c>decide</c> row must be routed: <c>decide, operator</c> reaches the
    /// AI Operator gateway and submits facts, <c>decide, switched</c> reads a
    /// transition profile of the switch, either in the service itself or in the
    /// service it drives. <c>decide, local</c> is limited to the closed set.
    /// </summary>
    private static List<string> RoutingViolations(IReadOnlyDictionary<string, string> inventory,
        IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (service, decisionClass) in inventory)
        {
            if (!decisionClass.StartsWith("decide", StringComparison.Ordinal)) continue;
            if (decisionClass == Local)
            {
                if (!LocalDeciders.Contains(service))
                    violations.Add($"{service} is a new local decider; route it through a switch profile");
                continue;
            }
            if (decisionClass is not (Operator or Switched))
            {
                violations.Add($"{service} is classed '{decisionClass}'; route it through a switch profile");
                continue;
            }
            var owners = new[] { service, DecidesThrough.GetValueOrDefault(service, service) }.Distinct();
            var code = string.Join("\n", owners.SelectMany(owner => DeclaringFiles(sources, owner))
                .Select(file => StripComments(file.Source)));
            if (code.Length == 0)
            {
                violations.Add($"{service} has no source in backend/");
                continue;
            }
            var routed = decisionClass == Operator
                ? Regex.IsMatch(code, @"\bAiOperatorDecisionGateway\b") && Regex.IsMatch(code, @"\.Submit\(")
                : Regex.IsMatch(code, @"\bMonolithTransitionSwitch\.For\(");
            if (!routed)
                violations.Add($"{service} is classed '{decisionClass}' but does not reach the switch");
        }
        return violations;
    }

    private static List<string> PolicyViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (path, raw) in sources)
        {
            if (!WorkflowFolder.IsMatch(path)) continue;
            if (HostTransitionRule.IsMatch(StripComments(raw)))
                violations.Add($"{path} defines a transition decision");
        }
        return violations;
    }

    private static IEnumerable<(string Path, string Source)> DeclaringFiles(
        IReadOnlyDictionary<string, string> sources, string type)
    {
        var declaration = new Regex(@"\bclass\s+" + Regex.Escape(type) + @"\b");
        return sources.Where(item => declaration.IsMatch(item.Value)).Select(item => (item.Key, item.Value));
    }

    private static Dictionary<string, string> BackendSources(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText);

    /// <summary>Calls into any static policy or breaker of the decision library, and the monolith adapter.</summary>
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

    /// <summary>Hosted-service rows of the inventory: service name to its class column.</summary>
    private static class Inventory
    {
        private static readonly Regex Row = new(@"^\|\s*(\w+)\s*\|[^|\n]*\|\s*([^|\n]+?)\s*\|", RegexOptions.Multiline);

        public static IReadOnlyDictionary<string, string> Load(string path)
            => Row.Matches(File.ReadAllText(path))
                .Where(match => match.Groups[1].Value is not ("Class" or "Hosted"))
                .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
    }

}
