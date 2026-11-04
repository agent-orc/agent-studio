extern alias Runner;
extern alias TaskServerAssembly;

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// MS-5 boundary of the Dossier model-selection-and-fallback, in the style of
/// <see cref="TransitionDecisionBoundaryTests"/>: one place decides CLI, model
/// and thinking level. The compiled monolith is scanned for every call to
/// <see cref="CliRouter.Get"/>, every CLI start
/// (<see cref="ICliExecutionService.StartAsync"/>) and every direct call to a
/// concrete one-shot implementation, which bypasses the selection-routed
/// <see cref="CliOneShotRegistry"/>. Each hit must be on an allow-list:
/// <list type="bullet">
///   <item><see cref="SelectionRoutedStarts"/> start the route the selection
///   service returned, and the guard proves the call into the service;</item>
///   <item><see cref="PendingStarts"/> are the paths MS-6 moves behind the
///   service. That list only shrinks; MS-6 empties it;</item>
///   <item><see cref="RouterReaders"/> read execution state, availability or
///   the model catalogue through the router and start nothing.</item>
/// </list>
/// A new direct start, or a router call in a new type, fails the build. A
/// stale entry fails too, so the lists stay exact.
/// </summary>
[Trait("Category", "Guard")]
public sealed class ModelSelectionBoundaryTests
{
    /// <summary>CLI starts that run the route of a selection-service decision, and the member that asks the service.</summary>
    private static readonly IReadOnlyDictionary<string, string> SelectionRoutedStarts = new Dictionary<string, string>
    {
        // Local coding run: PlanQuotaAdmission asks IModelSelectionService (local-coding-launch).
        ["AgentStudio.Runner.ProjectRunner.RunCliAsync"] = "AgentStudio.Runner.ProjectRunner.PlanQuotaAdmission",
    };

    /// <summary>Starts that still decide outside the service. MS-6 moves each behind it and removes the entry.</summary>
    private static readonly IReadOnlyDictionary<string, string> PendingStarts = new Dictionary<string, string>
    {
        ["AgentStudio.Runner.ProjectRunner.RunConflictResolutionStepAsync"] =
            "MS-6: merge resolver reads the step config and the legacy admission adapter, then starts directly",
    };

    /// <summary>Types that call <see cref="CliRouter.Get"/> without starting a model run.</summary>
    private static readonly IReadOnlyDictionary<string, string> RouterReaders = new Dictionary<string, string>
    {
        ["AgentStudio.Runner.ProjectRunner"] = "execution state, stop, recovery; its starts are listed above",
        ["AgentStudio.Runner.TaskRunnerService"] = "availability, execution state and stop",
        ["AgentStudio.Tasks.TaskCoreRuntime"] = "execution state",
        ["AgentStudio.Tasks.BoardReadSignatureSource"] = "execution state",
        ["AgentStudio.Tasks.TaskDetailResources"] = "execution state",
        ["AgentStudio.Tasks.TaskTransitionService"] = "output resource release",
        ["AgentStudio.Tasks.TaskEndpointHelpers"] = "execution state",
        ["AgentStudio.Tasks.TaskCrudEndpoints"] = "model catalogue for the creation recommendation",
        ["AgentStudio.Cli.CliEndpoints"] = "model catalogue and CLI path checks",
        ["AgentStudio.Cli.CliVersionMonitorHostedService"] = "CLI version probe",
        ["AgentStudio.Cli.QuotaProbeBase"] = "quota probe of the installed CLI",
        ["AgentStudio.Pipeline.CliPipelineModelCatalogueProvider"] = "model catalogue for the step economy advisor",
        ["AgentStudio.Connector.ConnectorDevSeatCompatibilityEndpoints"] = "dev-seat compatibility description and catalogue",
        // Top-level statements compile into the global-namespace Program type.
        ["Program"] = "Codex catalogue warm-up",
    };

    [Fact]
    public void Cli_starts_and_router_calls_stay_on_the_allow_lists()
    {
        var sites = Scan(typeof(ModelSelectionService).Assembly);

        var unlistedStarts = sites.Starts.Keys
            .Where(owner => !SelectionRoutedStarts.ContainsKey(owner) && !PendingStarts.ContainsKey(owner))
            .Select(owner => $"{owner} -> {string.Join(", ", sites.Starts[owner])}")
            .ToArray();
        Assert.True(unlistedStarts.Length == 0,
            "A CLI start outside the selection service boundary. Ask IModelSelectionService for the route "
            + "(Dossier model-selection-and-fallback, MS-5) instead of starting directly:\n  "
            + string.Join("\n  ", unlistedStarts));

        var unlistedReaders = sites.RouterCallers
            .Where(type => !RouterReaders.ContainsKey(type))
            .ToArray();
        Assert.True(unlistedReaders.Length == 0,
            "CliRouter.Get(...) outside the allow-list. Model runs take their CLI from the selection service:\n  "
            + string.Join("\n  ", unlistedReaders));
    }

    [Fact]
    public void Allow_lists_have_no_stale_entries()
    {
        var sites = Scan(typeof(ModelSelectionService).Assembly);
        var stale = SelectionRoutedStarts.Keys.Concat(PendingStarts.Keys)
            .Where(owner => !sites.Starts.ContainsKey(owner))
            .Concat(RouterReaders.Keys.Where(type => !sites.RouterCallers.Contains(type)))
            .ToArray();
        Assert.True(stale.Length == 0, "Remove allow-list entries whose call site is gone: " + string.Join(", ", stale));
    }

    [Fact]
    public void Selection_routed_starts_ask_the_selection_service()
    {
        var sites = Scan(typeof(ModelSelectionService).Assembly);
        foreach (var (owner, via) in SelectionRoutedStarts)
        {
            Assert.Contains(via, sites.Calls[owner]);
            Assert.Contains("AgentStudio.Cli.IModelSelectionService.Select", sites.Calls[via]);
        }
        // The one-shot registry dispatch is the only caller of the concrete one-shots it wraps.
        Assert.Contains("AgentStudio.Cli.IModelSelectionService.Select",
            sites.Calls["AgentStudio.Cli.CliOneShotRegistry.DispatchAsync"]);
        // The remote coding claim asks the service inside the claim endpoint.
        Assert.Contains(sites.Calls, entry => entry.Key.StartsWith("AgentStudio.Tasks.LeaseEndpoints.", StringComparison.Ordinal)
            && entry.Value.Contains("AgentStudio.Cli.IModelSelectionService.Select"));
    }

    [Fact]
    public void Guard_rejects_a_new_direct_cli_start()
    {
        var sites = Scan(typeof(ModelSelectionBoundaryTests).Assembly);
        var sample = typeof(DirectStartSample).FullName!.Replace('+', '.');
        Assert.Contains(sample + ".StartDirectlyAsync", sites.Starts.Keys);
        Assert.Contains(sample + ".BypassRegistryAsync", sites.Starts.Keys);
        Assert.Contains(typeof(DirectStartSample).FullName!, sites.RouterCallers);
        Assert.False(SelectionRoutedStarts.ContainsKey(sample + ".StartDirectlyAsync"));
    }

    [Fact]
    public void Legacy_types_are_thin_adapters_over_the_shared_planner()
    {
        var sites = Scan(typeof(ModelSelectionService).Assembly);
        Assert.Equal(typeof(ProviderModelFallback).Assembly, typeof(ModelSelectionPlanner).Assembly);
        Assert.Equal(typeof(ProviderModelFallback).Assembly, typeof(SelectionRequest).Assembly);
        Assert.Equal(typeof(ProviderModelFallback).Assembly, typeof(ModelRoutingTier).Assembly);
        foreach (var (adapter, planner) in new[]
                 {
                     ("AgentStudio.Cli.QuotaAdmissionPlanner.Plan", "Plan"),
                     ("AgentStudio.Cli.CliQuotaFallbackService.Resolve", "ResolveRoute"),
                     ("AgentStudio.Pipeline.ModelRoutingPolicyRegistry.RouteMeetsFloor", "RouteMeetsFloor"),
                     ("AgentStudio.Pipeline.ModelRoutingPolicyRegistry.ProviderRejectionFallback", "RejectionSibling"),
                     ("AgentStudio.Runner.MechanicalFreshRunRoutePolicy.Qualify", "QualifyFreshRun"),
                     ("AgentStudio.Cli.ModelSelectionService.Select", "Plan"),
                 })
            Assert.Contains($"AgentStudio.TaskServer.Contracts.ModelSelectionPlanner.{planner}", sites.Calls[adapter]);
        Assert.Contains("AgentStudio.Cli.IModelSelectionService.Select", sites.Calls["AgentStudio.Cli.QuotaAdmissionService.Plan"]);
    }

    [Fact]
    public void Task_server_and_runner_use_the_planner_from_the_contracts_package()
    {
        var planner = typeof(ModelSelectionPlanner).Assembly.GetName().Name;
        var taskServer = typeof(TaskServerAssembly::AgentStudio.TaskServer.TaskServerStore).Assembly;
        var runner = typeof(Runner::AgentRunner.TaskServerClient).Assembly;
        foreach (var (assembly, member) in new[] { (taskServer, "RejectionSibling"), (runner, "ClaimRoute") })
        {
            Assert.Contains(assembly.GetReferencedAssemblies(), name => name.Name == planner);
            Assert.Contains(Scan(assembly).Calls.Values,
                calls => calls.Contains($"AgentStudio.TaskServer.Contracts.ModelSelectionPlanner.{member}"));
        }
    }

    [Fact]
    public void Planner_reads_no_clock_file_or_environment()
    {
        var forbidden = new Regex(@"\b(File|Directory|Process|HttpClient|IConfiguration|Environment|Random)\s*[.(]|\bDateTime(?:Offset)?\.(?:UtcNow|Now|Today)\b|\bTimeProvider\b");
        var folder = Path.Combine(RepositoryRoot.Find(), "contracts", "TaskServer.Contracts", "ModelSelection");
        foreach (var file in new[] { "ModelSelectionPlanner.cs", "SelectionQuotaMath.cs", "SelectionContracts.cs" })
            Assert.False(forbidden.IsMatch(File.ReadAllText(Path.Combine(folder, file))), file);
    }

    private sealed record ScanResult(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Starts,
        IReadOnlySet<string> RouterCallers,
        IReadOnlyDictionary<string, IReadOnlySet<string>> Calls);

    private static ScanResult Scan(Assembly assembly)
    {
        var starts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var routerCallers = new HashSet<string>(StringComparer.Ordinal);
        var calls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in LoadableTypes(assembly))
        foreach (var method in DeclaredMethods(type))
        {
            var owner = OwnerOf(method);
            if (!calls.TryGetValue(owner, out var ownerCalls)) calls[owner] = ownerCalls = new(StringComparer.Ordinal);
            foreach (var target in CallTargets(method))
            {
                var declaring = target.DeclaringType;
                if (declaring is null) continue;
                ownerCalls.Add($"{declaring.FullName?.Split('`')[0].Replace('+', '.')}.{target.Name}");
                if (declaring == typeof(CliRouter) && target.Name == nameof(CliRouter.Get))
                    routerCallers.Add(OuterType(method.DeclaringType!).FullName!);
                else if (IsCliStart(target, declaring) && !IsInsideOneShotChain(method))
                {
                    if (!starts.TryGetValue(owner, out var list)) starts[owner] = list = [];
                    list.Add($"{declaring.Name}.{target.Name}");
                }
            }
        }
        return new ScanResult(
            starts.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value, StringComparer.Ordinal),
            routerCallers,
            calls.ToDictionary(item => item.Key, item => (IReadOnlySet<string>)item.Value, StringComparer.Ordinal));
    }

    /// <summary>A process start through a CLI backend, or a call that skips the selection-routed one-shot registry.</summary>
    private static bool IsCliStart(MethodBase target, Type declaring)
        => (target.Name == nameof(ICliExecutionService.StartAsync)
                && typeof(ICliExecutionService).IsAssignableFrom(declaring))
            || (target.Name == nameof(ICliOneShot.RunAsync)
                && declaring is { IsInterface: false }
                && typeof(ICliOneShot).IsAssignableFrom(declaring));

    /// <summary>One-shot decorators and the registry itself sit behind the registry dispatch.</summary>
    private static bool IsInsideOneShotChain(MethodBase method)
    {
        var owner = OuterType(method.DeclaringType!);
        return owner == typeof(CliOneShotRegistry) || typeof(ICliOneShot).IsAssignableFrom(owner)
            || typeof(ICliExecutionService).IsAssignableFrom(owner);
    }

    private static Type OuterType(Type type)
    {
        while (type.DeclaringType is not null && IsCompilerGenerated(type))
            type = type.DeclaringType;
        return type;
    }

    /// <summary>
    /// Closures, lambdas and state machines. The compiler leaves
    /// <see cref="CompilerGeneratedAttribute"/> off the state machine of an
    /// async lambda, so the unspeakable name counts as well.
    /// </summary>
    private static bool IsCompilerGenerated(Type type)
        => type.IsDefined(typeof(CompilerGeneratedAttribute)) || type.Name.StartsWith('<');

    /// <summary>"Namespace.Type.Method" with lambdas, local functions and state machines folded into their source method.</summary>
    private static string OwnerOf(MethodBase method)
    {
        var declaring = method.DeclaringType!;
        var name = method.Name.StartsWith('<')
            ? SourceName(method.Name)
            : declaring.Name.StartsWith('<')
                && !declaring.Name.StartsWith("<>", StringComparison.Ordinal)
                ? SourceName(declaring.Name)
                : method.Name;
        return $"{OuterType(declaring).FullName!.Split('`')[0].Replace('+', '.')}.{name}";
    }

    private static string SourceName(string generated)
    {
        var match = Regex.Match(generated, @"^<+([^<>]+)>");
        return match.Success ? match.Groups[1].Value : generated;
    }

    private static IEnumerable<MethodBase> CallTargets(MethodBase caller)
    {
        byte[]? il;
        try { il = caller.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException) { il = null; }
        if (il is null) yield break;
        var typeArguments = caller.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArguments = caller is MethodInfo { IsGenericMethod: true } genericMethod
            ? genericMethod.GetGenericArguments() : null;
        for (var offset = 0; offset < il.Length;)
        {
            var value = il[offset] == 0xFE && offset + 1 < il.Length
                ? (ushort)(0xFE00 | il[++offset])
                : il[offset];
            offset++;
            if (!IlOpCodes.TryGetValue(value, out var opcode)) yield break;
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
                _ => 0,
            };
            if (opcode != OpCodes.Call && opcode != OpCodes.Callvirt
                && opcode != OpCodes.Ldftn && opcode != OpCodes.Ldvirtftn && opcode != OpCodes.Newobj)
                continue;
            MethodBase? target;
            try
            {
                target = caller.Module.ResolveMethod(BitConverter.ToInt32(il, operandOffset), typeArguments, methodArguments);
            }
            catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException
                or FileNotFoundException or FileLoadException)
            {
                continue;
            }
            if (target is not null) yield return target;
        }
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance;
        return type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
    }

    private static readonly IReadOnlyDictionary<ushort, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));

    /// <summary>What a new consumer must not do: pick a CLI and start it, or call a concrete one-shot.</summary>
    private static class DirectStartSample
    {
        public static Task StartDirectlyAsync(CliRouter router)
            => router.Get(CliTypes.Codex).StartAsync("job", "job-key", "prompt", ".");

        public static Task BypassRegistryAsync(CodexOneShot oneShot)
            => oneShot.RunAsync(new CliOneShotRequest(CliTypes.Codex, "gpt-6-sol", "prompt"));
    }
}
