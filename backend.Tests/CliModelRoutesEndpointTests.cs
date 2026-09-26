using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class CliModelRoutesEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-routes-endpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Endpoint_KeepsProfiles_AndAddsCatalogueStateAndNonReroutableCallers()
    {
        Directory.CreateDirectory(_root);
        var resetAt = DateTime.UtcNow.AddDays(3);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance).Write([
            new QuotaSnapshot
            {
                CliType = CliTypes.Claude,
                FetchedAt = DateTime.UtcNow,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 98, ResetAt = resetAt }],
            },
        ]);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["PublicDemo:Enabled"] = "false",
                })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.GetAsync("/api/cli/quota/model-routes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Object, root.GetProperty("profiles").ValueKind);
        Assert.Contains("TokenEconomy", root.GetProperty("catalogueVersion").GetString());
        Assert.Contains(root.GetProperty("routes").EnumerateArray(), route =>
            route.GetProperty("fromModel").GetString() == ModelIds.ClaudeOpus5
            && route.GetProperty("toModel").GetString() == ModelIds.Gpt56Sol
            && route.GetProperty("source").GetString() == "catalogue");
        var claudeState = root.GetProperty("states").GetProperty(CliTypes.Claude);
        Assert.Equal("fallback-active", claudeState.GetProperty("state").GetString());
        var weekly = Assert.Single(claudeState.GetProperty("windows").EnumerateArray());
        Assert.Equal(98, weekly.GetProperty("usedPct").GetDouble());
        Assert.Equal(95, weekly.GetProperty("capPct").GetDouble());
        Assert.NotEqual(JsonValueKind.Null, weekly.GetProperty("resetAt").ValueKind);
        Assert.Contains(root.GetProperty("callersCannotReroute").EnumerateArray(), item =>
            item.GetProperty("caller").GetString() == "quota-probe");

        var preference = await client.PutAsJsonAsync(
            "/api/cli/quota/fallback-preference",
            new { cliType = CliTypes.Claude, preferFallback = true });
        Assert.Equal(HttpStatusCode.OK, preference.StatusCode);
        using var preferenceJson = JsonDocument.Parse(await preference.Content.ReadAsStringAsync());
        Assert.True(preferenceJson.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(
            resetAt.ToString("O"),
            preferenceJson.RootElement.GetProperty("expiresAt").GetDateTime().ToString("O"));

        using var preferredJson = JsonDocument.Parse(
            await client.GetStringAsync("/api/cli/quota/model-routes"));
        Assert.Equal(
            "fallback-preferred",
            preferredJson.RootElement.GetProperty("states").GetProperty(CliTypes.Claude)
                .GetProperty("state").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
