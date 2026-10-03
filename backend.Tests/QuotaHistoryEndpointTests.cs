using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3001: <c>GET /api/cli/quota/history?cli=claude&amp;hours=48</c> returns the
/// recorded series per window with the three-hour rate and the 100 % forecast.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class QuotaHistoryEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quota-history-endpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task History_ReturnsWindowsPointsAndForecast()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        var store = new QuotaHistoryStore(configuration, NullLogger<QuotaHistoryStore>.Instance);
        var latest = DateTime.UtcNow.AddMinutes(-5);
        var weeklyReset = latest.AddDays(3);
        // 60 hours of readings every 30 minutes, weekly rising 0.5 %/h, then 2 %/h for the last 3 h.
        for (var i = 120; i >= 0; i--)
        {
            var at = latest.AddMinutes(-30 * i);
            var weekly = i > 6 ? 40 + (120 - i) * 0.25 : 40 + 114 * 0.25 + (6 - i);
            store.Record(new QuotaSnapshot
            {
                CliType = CliTypes.Claude,
                FetchedAt = at,
                Windows =
                [
                    new QuotaWindow { Label = "Current week (all models)", UsedPct = weekly, ResetAt = weeklyReset },
                    new QuotaWindow { Label = "Current session", UsedPct = 30, ResetAt = latest.AddHours(2) },
                ],
            });
        }

        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.GetAsync("/api/cli/quota/history?cli=claude&hours=48");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("claude", root.GetProperty("cliType").GetString());
        Assert.Equal(48, root.GetProperty("hours").GetInt32());
        Assert.Equal(14, root.GetProperty("retentionDays").GetInt32());
        Assert.Equal(3, root.GetProperty("rateLookbackHours").GetDouble());

        var windows = root.GetProperty("windows").EnumerateArray().ToList();
        Assert.Equal(["session", "weekly"], windows.Select(window => window.GetProperty("kind").GetString()));
        var week = windows[1];
        Assert.Equal("Current week (all models)", week.GetProperty("label").GetString());
        // 48 h at 30-minute spacing = 96 readings plus the one on the boundary at most.
        Assert.InRange(week.GetProperty("points").GetArrayLength(), 96, 97);

        var forecast = week.GetProperty("forecast");
        Assert.Equal("full-before-reset", forecast.GetProperty("status").GetString());
        Assert.Equal(74.5, forecast.GetProperty("currentPct").GetDouble(), precision: 6);
        Assert.Equal(2.0, forecast.GetProperty("ratePctPerHour").GetDouble(), precision: 3);
        Assert.True(forecast.GetProperty("reachesFullBeforeReset").GetBoolean());
        var fullAt = forecast.GetProperty("forecastFullAt").GetDateTime().ToUniversalTime();
        Assert.InRange((fullAt - latest).TotalHours, 12.7, 12.8);           // 25.5 points left at 2 %/h
    }

    [Theory]
    [InlineData("/api/cli/quota/history")]
    [InlineData("/api/cli/quota/history?cli=unknown")]
    [InlineData("/api/cli/quota/history?cli=claude&hours=0")]
    [InlineData("/api/cli/quota/history?cli=claude&hours=337")]
    public async Task History_RejectsInvalidQuery(string url)
    {
        Directory.CreateDirectory(_root);
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task History_WithoutRecordedReadings_ReturnsEmptyWindows()
    {
        Directory.CreateDirectory(_root);
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/cli/quota/history?cli=codex"));

        Assert.Equal(48, json.RootElement.GetProperty("hours").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("windows").GetArrayLength());
    }

    private WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["PublicDemo:Enabled"] = "false",
                })));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
