using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace TaskServer.Tests;

public sealed class DeprecatedTaskFieldTests
{
    [Fact]
    public async Task CreateTask_IgnoresLegacyParentFieldAndReportsDeprecation()
    {
        var context = new DefaultHttpContext();
        var request = JsonSerializer.Deserialize<CreateTaskRequest>(
            """{"title":"Task","epicId":"old-parent"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fields = request.CompatibilityFields;
        Assert.NotNull(fields);
        Assert.Equal("old-parent", fields["epicId"].GetString());

        var result = await TaskServerEndpoints.CreateTaskAsync(
            context, fields, () => Task.FromResult(new { id = "task-1" }));

        Assert.Equal("true", context.Response.Headers["Deprecation"].ToString());
        Assert.Contains("epicId", context.Response.Headers.Warning.ToString());
        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
