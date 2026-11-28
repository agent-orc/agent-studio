using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodexExecutionModelCatalogueTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fleet_advertisements_drive_availability_and_keep_older_pins_selectable()
    {
        var coding = Host("coding", Now,
            Capability("cli-execution:codex", models: [ModelIds.Gpt62Sol, ModelIds.Gpt6Sol]));
        var review = Host("review", Now,
            Capability("cli-execution:codex"),
            Capability("model-acceptance:codex:gpt-6.2-astra"));
        var stale = Host("stale", Now.AddMinutes(-4),
            Capability("cli-execution:codex", models: [ModelIds.Gpt61Sol]));

        var catalogue = CodexExecutionModelCatalogue.Build([coding, review, stale], Now);
        var sol = Assert.Single(catalogue.Models, model => model.IsDefault);
        Assert.Equal("execution-hosts", catalogue.Source);
        Assert.Equal(ModelIds.Gpt62Sol, sol.Id);
        Assert.True(sol.Available);
        Assert.Equal("medium", sol.DefaultThinkingLevel);
        Assert.True(Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt62Astra).Available);
        Assert.False(Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt61Sol).Available);
        var superseded = Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt6Sol);
        Assert.True(superseded.Available);
        Assert.False(superseded.Deprecated);
        Assert.Contains("Superseded", superseded.AvailabilityNote);
    }

    [Fact]
    public void A_fresh_refusal_overrides_the_same_hosts_cli_model_list()
    {
        var host = Host("coding", Now,
            Capability("cli-execution:codex", models: [ModelIds.Gpt62Sol]),
            Capability("model-acceptance:codex:gpt-6.2-sol", status: "blocked"),
            Capability("model-acceptance:codex:gpt-6.2-astra", freshUntil: Now));

        var catalogue = CodexExecutionModelCatalogue.Build([host], Now);
        Assert.All(catalogue.Models, model => Assert.False(model.Available));
        Assert.Equal(ModelIds.Gpt62Sol, Assert.Single(catalogue.Models, model => model.IsDefault).Id);
    }

    private static RunnerCapabilitySnapshotDto Host(string id, DateTime lastSeen, params CapabilityHealthDto[] capabilities)
        => new(id, id, id, "instance", "test", 1, "active", lastSeen, lastSeen,
            new RemoteHostAdmissionDto(id, "open", null, null, null, null), capabilities, null);

    private static CapabilityHealthDto Capability(string key, string status = "ready",
        DateTime? freshUntil = null, string[]? models = null)
        => new(key, "test", status, "healthy", null, Now, freshUntil ?? Now.AddMinutes(1), true,
            null, null, null, null, 0, "0.166.0", null, null, [], [], SupportedModels: models);
}
