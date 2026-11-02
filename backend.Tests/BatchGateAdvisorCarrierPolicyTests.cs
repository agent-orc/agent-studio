using System.Text.Json;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3411: the networked server cannot start the advisor CLI. The engine
/// carries the advisor when it owns the batch plan; otherwise the advisor is
/// reported as off with its reason.
/// </summary>
public sealed class BatchGateAdvisorCarrierPolicyTests
{
    [Theory]
    [InlineData("advise", true, OrchestrationExecutionMode.Monolith, "advise", BatchGateAdvisorCarrier.Monolith, false)]
    [InlineData("enforce", true, OrchestrationExecutionMode.Shadow, "enforce", BatchGateAdvisorCarrier.Monolith, false)]
    [InlineData("advise", true, OrchestrationExecutionMode.Engine, "advise", BatchGateAdvisorCarrier.Engine, false)]
    [InlineData("advise", false, OrchestrationExecutionMode.Engine, "advise", BatchGateAdvisorCarrier.Engine, false)]
    [InlineData("advise", false, OrchestrationExecutionMode.Monolith, "off", BatchGateAdvisorCarrier.None, true)]
    [InlineData("enforce", false, OrchestrationExecutionMode.Shadow, "off", BatchGateAdvisorCarrier.None, true)]
    [InlineData("off", false, OrchestrationExecutionMode.Monolith, "off", BatchGateAdvisorCarrier.None, false)]
    [InlineData("off", true, OrchestrationExecutionMode.Engine, "off", BatchGateAdvisorCarrier.None, false)]
    public void Resolve_Matrix(string configured, bool hostCliAllowed, OrchestrationExecutionMode owner,
        string effective, string carrier, bool reported)
    {
        var result = BatchGateAdvisorCarrierPolicy.Resolve(configured, hostCliAllowed, owner);

        Assert.Equal(effective, result.EffectiveMode);
        Assert.Equal(carrier, result.Carrier);
        Assert.Equal(reported, result.Reason is not null);
    }

    [Fact]
    public void SettingsResponse_Networked_MonolithOwner_ReportsTheConfiguredAdvisorAsOff()
    {
        var body = Response(new BatchGateAdvisorOptions(Mode: "advise"),
            Config(("Security:Profile", "networked")));

        Assert.Equal("advise", body.GetProperty("settings").GetProperty("advisor").GetProperty("mode").GetString());
        var effective = body.GetProperty("advisorEffective");
        Assert.Equal("off", effective.GetProperty("effectiveMode").GetString());
        Assert.Equal(BatchGateAdvisorCarrier.None, effective.GetProperty("carrier").GetString());
        Assert.Equal(BatchGateAdvisorCarrierPolicy.HostCliDisabledReason, effective.GetProperty("reason").GetString());
    }

    [Fact]
    public void SettingsResponse_Networked_EngineOwner_ReportsTheEngineAsCarrier()
    {
        var body = Response(new BatchGateAdvisorOptions(Mode: "enforce"), Config(
            ("Security:Profile", "networked"),
            ($"Orchestration:Transitions:{BatchGatePilotService.BatchPlanProfile}", "Engine")));

        var effective = body.GetProperty("advisorEffective");
        Assert.Equal("enforce", effective.GetProperty("effectiveMode").GetString());
        Assert.Equal(BatchGateAdvisorCarrier.Engine, effective.GetProperty("carrier").GetString());
        Assert.Equal(JsonValueKind.Null, effective.GetProperty("reason").ValueKind);
    }

    [Fact]
    public void SettingsResponse_Local_KeepsTheMonolithAdvisor()
    {
        var body = Response(new BatchGateAdvisorOptions(Mode: "advise"), Config(("Security:Profile", "local")));

        var effective = body.GetProperty("advisorEffective");
        Assert.Equal("advise", effective.GetProperty("effectiveMode").GetString());
        Assert.Equal(BatchGateAdvisorCarrier.Monolith, effective.GetProperty("carrier").GetString());
    }

    private static JsonElement Response(BatchGateAdvisorOptions advisor, IConfiguration configuration)
        => JsonSerializer.SerializeToElement(
            AgentStudio.Projects.ProjectSettingsEndpoints.BatchGateSettingsResponse(
                new BatchGateFormationOptions(Advisor: advisor), configuration),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
