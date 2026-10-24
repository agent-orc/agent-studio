using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;
using FollowUpRule = AgentStudio.OrchestrationDecisions.ReviewFollowUpPolicy;
using static AgentStudio.OrchestrationDecisions.ReissueLoopBreaker;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class TransitionDecisionBoundaryTests
{
    private static readonly string[] OwnedPolicies =
    [
        "ReviewFollowUpPolicy", "ReviewRoundBudgetPolicy", "CardRoundBudgetPolicy",
        "OperatorSweepPolicy", "ReissueLoopBreaker", "RemoteDeliveryIntegrationPolicy",
        "RunTimeoutSalvageContinuationPolicy", "ReviewInfrastructureRepeatPolicy",
        "OrchestrationSettlementPolicy", "RepeatedReviewBlockPolicy", "StageDecisionPolicy",
        "CouncilDecisionPolicy", "LocalLoopDecisionPolicy", "ReviewBudgetPolicy", "TransitionDecisionPolicy",
        "RoundCounterPolicy", "ContinuationReplayPolicy", "RepeatedAspectDecisionPolicy",
        "IntegrationResultPolicy", "LoopExhaustionPolicy",
        // AGT-3060 (I18): background deciders moved out of the monolith.
        "AiOperatorDecisionPolicy", "RunLivenessPolicy", "SteerTimeoutPolicy", "StaleProgressPolicy",
        "GateEnvironmentRetryPolicy", "GateEnvironmentContinuationPolicy",
        "DeliveryChainPolicy", "CauseBreakerClosePolicy", "IntakeRoutingPolicy", "AcceptanceRailDecisionPolicy",
        // AGT-3207: interventions on operational stall events.
        "StallInterventionPolicy",
        // AGT-3219: queue overrun, backlog and release rule of the batch gate.
        "GateQueueStallPolicy",
    ];

    [Fact]
    public void Decision_types_are_owned_by_one_pure_assembly()
    {
        var assembly = typeof(TransitionDecisionPolicy).Assembly;
        Assert.Equal(assembly, typeof(ReviewFollowUpPolicy).Assembly);
        Assert.Equal(assembly, typeof(ReviewRoundBudgetPolicy).Assembly);
        Assert.Equal(assembly, typeof(OperatorSweepPolicy).Assembly);
        Assert.Equal(assembly, typeof(RemoteDeliveryIntegrationPolicy).Assembly);
        Assert.Equal(assembly, typeof(AiOperatorDecisionPolicy).Assembly);
        Assert.Equal(assembly, typeof(LoopExhaustionPolicy).Assembly);
        Assert.Equal(assembly, typeof(RunLivenessPolicy).Assembly);
        Assert.Equal(assembly, typeof(GateEnvironmentRetryPolicy).Assembly);
        Assert.Equal(assembly, typeof(DeliveryChainPolicy).Assembly);
        Assert.Equal(assembly, typeof(AcceptanceRailDecisionPolicy).Assembly);
        var referenced = assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();
        Assert.DoesNotContain(referenced, name => name is "OrchestratorApi" or "task-server" or "orchestrator-engine"
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("System.Net.Http", StringComparison.Ordinal)
            || name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal));
    }

    [Fact]
    public void Library_has_one_namespace_and_no_host_friend_assemblies()
    {
        var root = RepositoryRoot.Find();
        foreach (var file in Sources(Path.Combine(root, "orchestration-decisions")))
        {
            var declarations = Regex.Matches(File.ReadAllText(file), @"\bnamespace\s+([\w.]+)")
                .Select(match => match.Groups[1].Value).ToArray();
            if (Path.GetFileName(file) == "GlobalUsings.cs")
                Assert.Empty(declarations);
            else
                Assert.Equal("AgentStudio.OrchestrationDecisions", Assert.Single(declarations));
        }
        var project = File.ReadAllText(Path.Combine(root, "orchestration-decisions", "Orchestration.Decisions.csproj"));
        Assert.DoesNotContain("InternalsVisibleTo", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosts_cannot_define_transition_policies_or_construct_new_transition_decisions()
    {
        var root = RepositoryRoot.Find();
        var violations = new List<string>();
        foreach (var directory in new[] { "backend", "task-server", "orchestrator-engine", "contracts" })
        foreach (var file in Sources(Path.Combine(root, directory)))
        {
            var source = File.ReadAllText(file);
            if (DefinesRule(source)) violations.Add(Path.GetRelativePath(root, file));
        }
        Assert.True(violations.Count == 0, "Transition rules belong in orchestration-decisions: " + string.Join(", ", violations));
    }

    [Fact]
    public void Transition_entry_points_call_the_shared_decision_library()
    {
        var root = RepositoryRoot.Find();
        var adapter = File.ReadAllText(Path.Combine(root, "backend/Features/Runner/MonolithTransitionAdapter.cs"));
        Assert.Matches(@"Decide\(TransitionFacts facts\)\s*=>\s*TransitionDecisionPolicy\.Decide\(facts\)", adapter);
        Assert.Matches(@"ReviewBudget\(ReviewBudgetFacts facts\)\s*=>\s*ReviewBudgetPolicy\.Decide\(facts\)", adapter);
        Assert.Matches(@"Sweep\([^)]*\)\s*=>\s*OperatorSweepPolicy\.Decide\(", adapter);
        Assert.Matches(@"LoopSignal\([^)]*\)\s*=>\s*LoopExhaustionPolicy\.Signal\(", adapter);
        Assert.Matches(@"ReviewText\(string output\)\s*=>\s*LegacyReviewDecisionPolicy\.Parse\(", adapter);
        Assert.DoesNotContain(Sources(Path.Combine(root, "backend"))
            .Where(path => !path.EndsWith("MonolithTransitionAdapter.cs", StringComparison.Ordinal)),
            path => File.ReadAllText(path).Contains("LoopExhaustionPolicy.Signal(", StringComparison.Ordinal));
        foreach (var path in new[] { "task-server/TaskServerTransitionAdapter.cs", "orchestrator-engine/EngineTransitionAdapter.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            Assert.Matches(@"Decide\(TransitionFacts facts\)\s*=>\s*TransitionDecisionPolicy\.Decide\(facts\)", source);
        }

        // Follow the returned value into the application path. A stray
        // adapter call elsewhere in the file cannot satisfy these checks.
        var decisionFlows = new Dictionary<string, string[]>
        {
            ["backend/Features/Runner/MonolithOrchestrationEndpoints.cs"] =
                [@"transition\s*=\s*MonolithTransitionAdapter\.Decide\([\s\S]{0,1500}MonolithTransitionAdapter\.StageDecisionPolicyToStageAction\(transition\.Action\)"],
            ["backend/Features/Runner/V1ReviewPlaneEndpoints.cs"] =
                [@"var\s+budgetDecision\s*=\s*MonolithTransitionAdapter\.ReviewBudget\([\s\S]{0,1800}var\s+roundBudget\s*=\s*budgetDecision\.RoundBudget",
                 @"var\s+shadowDecision\s*=\s*MonolithTransitionAdapter\.Decide\([\s\S]{0,900}monolithAction:\s*shadowDecision\.Action"],
            ["backend/Features/Runner/ReviewDecisionOrchestrator.cs"] =
                [@"var\s+budgetDecision\s*=\s*MonolithTransitionAdapter\.ReviewBudget\([\s\S]{0,1800}var\s+roundBudget\s*=\s*budgetDecision\.RoundBudget"],
            ["backend/Features/Runner/OperatorSweeps/OperatorSweepService.cs"] =
                [@"var\s+decision\s*=\s*MonolithTransitionAdapter\.Sweep\([\s\S]{0,250}decision\.Action"],
        };
        foreach (var (path, flows) in decisionFlows)
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            foreach (var flow in flows)
                Assert.Matches(flow, source);
        }

        // Inspect compiled calls so aliases, using static, and method names or
        // return types outside the known receipt set cannot bypass the boundary.
        var directCalls = typeof(MonolithTransitionAdapter).Assembly.GetTypes()
            .Where(type => type != typeof(MonolithTransitionAdapter))
            .SelectMany(DeclaredMethods)
            .SelectMany(method => DecisionCalls(method)
                .Select(call => $"{method.DeclaringType?.FullName}.{method.Name}: {call.DeclaringType?.Name}.{call.Name}"))
            .ToArray();
        Assert.True(directCalls.Length == 0, string.Join(", ", directCalls));
        var batch = File.ReadAllText(Path.Combine(root, "backend/Features/Pipeline/BatchGate/BatchGatePilotService.cs"));
        Assert.Matches(@"TransitionDecision BatchShadowDecision\([^)]*\)\s*=>\s*AgentStudio\.Runner\.MonolithTransitionAdapter\.StageGateTransition\(", batch);
    }

    [Theory]
    [InlineData("class ReviewRoundBudgetPolicy { }")]
    [InlineData("return new TransitionDecision(TransitionAction.Escalate, reason);")]
    [InlineData("return used >= allowed ? TransitionAction.Escalate : TransitionAction.FindingRound;")]
    [InlineData("var lane = outcome switch { \"done\" => TaskStates.AutoReview };")]
    public void Guard_rejects_reintroduced_host_rules(string source) => Assert.True(DefinesRule(source));

    [Fact]
    public void Library_cannot_read_environment_or_boundaries()
    {
        var forbidden = new Regex(@"\b(File|Directory|Process|HttpClient|IConfiguration|Environment)\s*[.(]|\bDateTime(?:Offset)?\.(?:UtcNow|Now)\b");
        foreach (var file in Sources(Path.Combine(RepositoryRoot.Find(), "orchestration-decisions")))
            Assert.False(forbidden.IsMatch(File.ReadAllText(file)), Path.GetFileName(file));
    }

    private static bool DefinesRule(string source)
    {
        var owned = string.Join("|", OwnedPolicies);
        return Regex.IsMatch(source, @"\bclass\s+(?:" + owned + @")\b")
            || Regex.IsMatch(source, @"\bnew\s+TransitionDecision\s*\(")
            || Regex.IsMatch(source, @"\?\s*TransitionAction\.")
            || Regex.IsMatch(source, @"\boutcome\s+switch\s*\{[^}]*=>\s*TaskStates\.", RegexOptions.Singleline);
    }

    [Fact]
    public void Direct_call_guard_resolves_aliases_and_using_static()
    {
        var calls = DeclaredMethods(typeof(DecisionCallSyntaxExamples))
            .SelectMany(DecisionCalls)
            .Select(call => $"{call.DeclaringType?.Name}.{call.Name}")
            .ToArray();
        Assert.Contains("ReviewFollowUpPolicy.Decide", calls);
        Assert.Contains("ReissueLoopBreaker.Evaluate", calls);
    }

    private static class DecisionCallSyntaxExamples
    {
        public static object ViaAlias() => FollowUpRule.Decide([], 0, 1);
        public static object ViaUsingStatic() => Evaluate(1, 2, false, true);
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance;
        return type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
    }

    private static IEnumerable<MethodBase> DecisionCalls(MethodBase caller)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var typeArguments = caller.DeclaringType?.GetGenericArguments();
        var methodArguments = caller.IsGenericMethod ? caller.GetGenericArguments() : null;
        for (var offset = 0; offset < il.Length;)
        {
            var code = il[offset++];
            var value = code == 0xfe ? (ushort)(0xfe00 | il[offset++]) : code;
            var opcode = IlOpCodes[value];
            var operandOffset = offset;
            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField
                    or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, operandOffset),
                _ => throw new InvalidOperationException($"Unsupported IL operand: {opcode.OperandType}"),
            };
            if (opcode != OpCodes.Call && opcode != OpCodes.Callvirt
                && opcode != OpCodes.Ldftn && opcode != OpCodes.Ldvirtftn)
                continue;
            var token = BitConverter.ToInt32(il, operandOffset);
            var target = caller.Module.ResolveMethod(token, typeArguments, methodArguments);
            if (target?.DeclaringType is { } policy
                && policy.Assembly == typeof(TransitionDecisionPolicy).Assembly
                && policy.IsAbstract && policy.IsSealed
                && (policy.Name.EndsWith("Policy", StringComparison.Ordinal)
                    || policy.Name.EndsWith("Breaker", StringComparison.Ordinal)))
                yield return target;
        }
    }

    private static readonly IReadOnlyDictionary<ushort, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));

    private static IEnumerable<string> Sources(string path) => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
}
