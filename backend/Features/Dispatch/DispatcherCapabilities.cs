using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Dispatch;

public sealed record DispatcherGrantV1(string Operation, string Tier, string Condition,
    string? BypassTier = null, bool PackagingOnly = false);
public sealed record DispatcherRoleV1(int SchemaVersion, string Id, string Kind, string Aspect,
    string Charter, string DefaultGrants, IReadOnlyList<string> ShadowGrants,
    string ReportTemplate, IReadOnlyList<string> Floors, IReadOnlyList<string> Reads);
public sealed record DispatcherGrantsV1(int SchemaVersion, string Role, IReadOnlyList<DispatcherGrantV1> Grants);

/// <summary>DS-5 catalogue names as data. PT-15 can import these without changing dispatch authority.</summary>
public static class DispatcherCapabilities
{
    public static readonly DispatcherGrantsV1 Catalogue = Load<DispatcherGrantsV1>("grants");
    public static readonly DispatcherRoleV1 Role = Load<DispatcherRoleV1>("role");
    public static IReadOnlyList<string> Operations => Catalogue.Grants.Select(g => g.Operation).ToArray();
    public static bool Admits(DispatcherOperationFacts facts)
        => Operations.Contains(facts.Operation) && MonolithTransitionAdapter.DispatcherOperation(facts);

    private static T Load<T>(string name)
    {
        using var stream = typeof(DispatcherCapabilities).Assembly
            .GetManifestResourceStream($"Policies.personas.dispatcher.{name}.v1.json")!;
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
