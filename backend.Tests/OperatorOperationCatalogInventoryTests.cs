using AgentStudio.Operations.Contracts;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Inventory of the operator operation catalogue (security model SM-4). Every
/// operation has exactly one tier; every mapped route an operator session can
/// reach resolves to one tier or to a refusal, never to two rules; no rule
/// points at a route that no longer exists; and every typed host operation is
/// catalogued. A new operator-reachable write without a deliberate rule is
/// refused at runtime, and a rule that overlaps or goes stale fails here.
/// </summary>
[Trait("Category", "Guard")]
public sealed class OperatorOperationCatalogInventoryTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-catalogue-" + Guid.NewGuid().ToString("N"));

    /// <summary>The Dossier's tier table (AGT-3353), seeded into the catalogue.</summary>
    public static TheoryData<string, OperatorTier> Seed => new()
    {
        { OperatorOperationIds.Observe, OperatorTier.T0 },
        { OperatorOperationIds.HostInspect, OperatorTier.T0 },
        { OperatorOperationIds.CardRequeue, OperatorTier.T1 },
        { OperatorOperationIds.CardSteer, OperatorTier.T1 },
        { OperatorOperationIds.CardAttention, OperatorTier.T1 },
        { OperatorOperationIds.OperatorLogAppend, OperatorTier.T1 },
        { OperatorOperationIds.ServiceRestart, OperatorTier.T1 },
        { OperatorOperationIds.HostDrain, OperatorTier.T1 },
        { OperatorOperationIds.HostResume, OperatorTier.T1 },
        { OperatorOperationIds.HostLogsCollect, OperatorTier.T0 },
        { OperatorOperationIds.HostConfigurationApply, OperatorTier.T1 },
        { OperatorOperationIds.HostCertificateIssue, OperatorTier.T1 },
        { OperatorOperationIds.HostCertificateRevoke, OperatorTier.T1 },
        { OperatorOperationIds.GateRerun, OperatorTier.T1 },
        { OperatorOperationIds.StageDeploy, OperatorTier.T1 },
        { OperatorOperationIds.ProductionPromote, OperatorTier.T1 },
        { OperatorOperationIds.SecretsChange, OperatorTier.T2 },
        { OperatorOperationIds.ExposureChange, OperatorTier.T2 },
        { OperatorOperationIds.DataDelete, OperatorTier.T2 },
        { OperatorOperationIds.HistoryRewrite, OperatorTier.T2 },
        { OperatorOperationIds.GateBypass, OperatorTier.T2 },
        { OperatorOperationIds.KeysRotate, OperatorTier.T2 },
        { OperatorOperationIds.HostEnrollment, OperatorTier.T2 },
        { OperatorOperationIds.AccessChange, OperatorTier.T2 },
        { OperatorOperationIds.GrantChange, OperatorTier.T2 },
        { OperatorOperationIds.CatalogueChange, OperatorTier.T2 },
        { OperatorOperationIds.DeployBlockerOverride, OperatorTier.T2 },
        { OperatorOperationIds.PersonaItemPublish, OperatorTier.T1 },
        { OperatorOperationIds.PersonaReportPublish, OperatorTier.T1 },
        { OperatorOperationIds.PersonaProposalDraft, OperatorTier.T1 },
        { OperatorOperationIds.PersonaDecisionRequest, OperatorTier.T1 },
        { OperatorOperationIds.PersonaProposalAccept, OperatorTier.T1 },
        { OperatorOperationIds.PersonaImportanceAdjust, OperatorTier.T1 },
        { OperatorOperationIds.PersonaBudgetRebalance, OperatorTier.T1 },
        { OperatorOperationIds.PersonaPause, OperatorTier.T1 },
    };

    [Theory]
    [MemberData(nameof(Seed))]
    public void The_tier_table_is_seeded_with_one_tier_per_operation(string id, OperatorTier tier)
    {
        var operation = Assert.Single(OperatorOperationCatalog.Operations, candidate => candidate.Id == id);
        Assert.Equal(tier, operation.Tier);
        Assert.Contains(operation.ScopeKind, OperatorScopeKinds.All);
    }

    [Fact]
    public void Every_operation_and_rule_is_known_and_unique()
    {
        Assert.Equal(OperatorOperationCatalog.Operations.Count,
            OperatorOperationCatalog.Operations.Select(operation => operation.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Seed.Count, OperatorOperationCatalog.Operations.Count);
        foreach (var rule in OperatorOperationCatalog.Routes)
        foreach (var id in OperatorOperationCatalog.PossibleOperations(rule))
            Assert.True(OperatorOperationCatalog.Find(id) is not null, $"{rule.Methods} {rule.Pattern} names unknown '{id}'.");
        // Reads are observe; a write rule never catalogues a read method.
        Assert.DoesNotContain(OperatorOperationCatalog.Routes, rule => rule.Methods is "GET" or "HEAD" or "OPTIONS" or "R" or "*");
    }

    [Fact]
    public void Every_typed_host_operation_is_catalogued()
    {
        foreach (var definition in OperationCatalogue.All)
            Assert.True(OperatorOperationCatalog.ForTypedOperation(definition.Id) is not null,
                $"Typed operation '{definition.Id}' has no catalogue entry and tier.");
        Assert.Equal(OperatorTier.T0, OperatorOperationCatalog.ForTypedOperation(OperationCatalogue.HostInspect.Id)!.Tier);
        Assert.Equal(OperatorTier.T1, OperatorOperationCatalog.ForTypedOperation(OperationCatalogue.ServiceRestart.Id)!.Tier);
    }

    [Fact]
    public void No_routine_operation_reaches_git_history_or_a_protected_branch()
    {
        // D3: the operator agent has no direct push; a change reaches a
        // protected branch as a card through the gate.
        Assert.DoesNotContain(OperatorOperationCatalog.Routes, rule =>
            OperatorOperationCatalog.PossibleOperations(rule).Any(id => OperatorOperationCatalog.Find(id)!.Tier != OperatorTier.T2)
            && (rule.Pattern.Contains("/git", StringComparison.OrdinalIgnoreCase)
                || rule.Pattern.Contains("push", StringComparison.OrdinalIgnoreCase)
                || rule.Pattern.Contains("merge", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Every_operator_reachable_route_resolves_to_exactly_one_tier_or_a_refusal_and_no_rule_is_stale()
    {
        var matched = new HashSet<OperatorOperationCatalog.RouteRule>();
        var overlapping = new List<string>();
        var untiered = new List<string>();
        foreach (var taskServer in new string?[] { null, "http://127.0.0.1:5071" })
        {
            using var factory = Factory(taskServer);
            foreach (var endpoint in Routes(factory.Services))
            foreach (var method in RoutePermissionCatalog.Methods(endpoint))
            {
                var pattern = RoutePermissionCatalog.Normalize(endpoint.RoutePattern);
                var route = $"{method} {endpoint.RoutePattern.RawText}";
                var rules = OperatorOperationCatalog.MatchingWrites(method, pattern);
                matched.UnionWith(rules);
                if (rules.Count > 1)
                {
                    overlapping.Add($"{route} -> {string.Join(", ", rules.Select(rule => rule.Pattern))}");
                    continue;
                }
                if (!OperatorReachable(endpoint, method)) continue;
                var tiers = IsRead(method)
                    ? [OperatorOperationCatalog.Find(OperatorOperationIds.Observe)!.Tier]
                    : rules.SelectMany(OperatorOperationCatalog.PossibleOperations)
                        .Select(id => OperatorOperationCatalog.Find(id)?.Tier).ToList();
                if (tiers.Any(tier => tier is null)) untiered.Add(route);
            }
        }

        Assert.True(overlapping.Count == 0,
            "Routes matched by more than one operator catalogue rule (one route, one tier):\n  "
            + string.Join("\n  ", overlapping.Distinct().Order(StringComparer.Ordinal)));
        Assert.True(untiered.Count == 0,
            "Operator-reachable routes without a tier:\n  " + string.Join("\n  ", untiered.Distinct().Order(StringComparer.Ordinal)));
        var stale = OperatorOperationCatalog.Routes.Where(rule => !matched.Contains(rule))
            .Select(rule => $"{rule.Methods} {rule.Pattern}").ToList();
        Assert.True(stale.Count == 0,
            "Operator catalogue rules that match no mapped route (renamed or removed route):\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Every_t1_operation_except_typed_host_operations_has_a_route()
    {
        var routed = OperatorOperationCatalog.Routes.SelectMany(OperatorOperationCatalog.PossibleOperations).ToHashSet(StringComparer.Ordinal);
        var typed = OperatorOperationCatalog.TypedOperations.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var operation in OperatorOperationCatalog.Operations.Where(operation => operation.Tier == OperatorTier.T1))
            Assert.True(routed.Contains(operation.Id) || typed.Contains(operation.Id), $"T1 '{operation.Id}' has no route or typed operation.");
    }

    /// <summary>
    /// A route an operator session gets past authentication and the
    /// structural floors on: a declared permission other than anonymous,
    /// own-session, or runner service, outside event streams.
    /// </summary>
    private static bool OperatorReachable(RouteEndpoint endpoint, string method)
    {
        var required = RoutePermissionCatalog.Resolve(endpoint, method);
        return required is not null
               && required.Permission is not (IamPermissions.Anonymous or IamPermissions.SelfSession or IamPermissions.RunnerService)
               && endpoint.RoutePattern.RawText?.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) != true;
    }

    private static bool IsRead(string method)
        => method is "GET" or "HEAD" or "OPTIONS";

    private WebApplicationFactory<Program> Factory(string? taskServer)
    {
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = SecurityProfiles.Networked,
                ["AllowedHosts"] = "*",
                ["TaskServer:BaseUrl"] = taskServer,
            }));
        });
        _ = factory.Server;
        return factory;
    }

    private static IReadOnlyList<RouteEndpoint> Routes(IServiceProvider services)
        => services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Distinct()
            .ToList();

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
