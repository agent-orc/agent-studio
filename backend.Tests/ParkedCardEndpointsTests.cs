using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /api/parked-cards</c> (AGT-2492). Proves the
/// route, its DI graph (sweep plus the real probe), and the payload shape an
/// operator reads - the parts the in-process sweep tests cannot reach.
/// </summary>
public sealed class ParkedCardEndpointsTests : IDisposable
{
    private const string ProjectName = "parked-cards-test";

    private readonly string _workspace;
    private readonly string _watchPath;

    public ParkedCardEndpointsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-parked-api-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task GetParkedCards_ListsTheParkedCardWithItsBlockerAndAge()
    {
        var parkedAt = DateTime.UtcNow.AddDays(-5);
        WriteParkedJob("waiting-on-baseline", parkedAt);
        WriteJob(TaskStates.Ready, "not-parked");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/parked-cards");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("total").GetInt32());

        var item = root.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("waiting-on-baseline", item.GetProperty("jobId").GetString());
        Assert.Equal(TaskStates.HumanReview, item.GetProperty("lane").GetString());
        Assert.Equal(
            HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
            item.GetProperty("blockerType").GetString());
        Assert.Equal(
            ParkedBlockerConditionKinds.GitAncestor,
            item.GetProperty("conditionKind").GetString());

        // Aging: the number that was missing while AGT-2220 sat for four days.
        Assert.True(item.GetProperty("parkedForSeconds").GetInt64() >= 5 * 86400);

        // The workspace is not a Git checkout, so the condition cannot be read.
        // That must surface as "nobody can tell", never as a resolved blocker.
        Assert.Equal(ParkedBlockerStatuses.Undeterminable, item.GetProperty("status").GetString());
        Assert.False(item.GetProperty("isRecallable").GetBoolean());

        // recallableOnly is the operator's working queue and stays empty while
        // no blocker is provably gone.
        var filtered = await client.GetAsync("/api/parked-cards?recallableOnly=true");
        using var filteredPayload = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert.Equal(0, filteredPayload.RootElement.GetProperty("total").GetInt32());
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["DeliveryChain:Guarded"] = "false",
                    });
                });
            });

    private void WriteParkedJob(string slug, DateTime parkedAt)
    {
        var dir = WriteJob(TaskStates.HumanReview, slug, parkedAt);
        var blockerType = HumanReviewEscalationCategories.ReviewSubjectUnmaterializable;
        ParkedBlockerMarker.Write(dir, new ParkedBlockerRecord
        {
            BlockerType = blockerType,
            Condition = ParkedBlockerCatalog.ConditionFor(blockerType),
            Lane = TaskStates.HumanReview,
            ParkedAt = parkedAt,
            Reason = "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto rerun",
        });
    }

    private string WriteJob(string state, string slug, DateTime? enteredLaneAt = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var entered = (enteredLaneAt ?? DateTime.UtcNow).ToString("o");
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"agent\":\"claude\",\"cliType\":\"claude\",\"enteredLaneAt\":\"{entered}\"}}");
        return dir;
    }
}
