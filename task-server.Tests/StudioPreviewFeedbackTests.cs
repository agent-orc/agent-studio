using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioPreviewFeedbackTests
{
    [Fact]
    public async Task Feedback_route_saves_crop_and_submits_selector_to_the_same_cards_fix_round()
    {
        using var data = new TempDirectory("studio-preview-feedback");
        await using var factory = new StudioTestApiFactory(data.Path);
        using var client = StudioTestClient.Create(factory);

        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            client, "/api/v1/workspaces", new CreateWorkspaceRequest("Preview feedback workspace"));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            client, "/api/v1/projects", new CreateProjectRequest(workspace.WorkspaceId, "Preview feedback project", "PFB"));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            client, $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest("Fix heading", State: "5-human-review"));
        var path = $"/api/v1/projects/-/tasks/{task.TaskId}";
        var crop = Encoding.UTF8.GetBytes("unique preview crop " + Guid.NewGuid());

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(crop);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "crop", "marked.png");
        form.Add(new StringContent("Increase the heading contrast"), "comment");
        form.Add(new StringContent("https://stage.example.test/pricing"), "previewAddress");
        form.Add(new StringContent("main h1"), "selector");

        var response = await client.PostAsync($"{path}/preview-feedback", form);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var continued = System.Text.Json.JsonSerializer.Deserialize<TaskLifecycleResponse>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(task.TaskId, continued!.Task.TaskId);
        Assert.Equal(task.TaskId, continued.ContinuationReceipt!.TaskId);

        var intents = await client.GetFromJsonAsync<List<ContinuationIntentProjection>>($"{path}/continuations");
        var intent = Assert.Single(intents!);
        Assert.Equal("preview-feedback", intent.Receipt.Reason);
        Assert.Equal(task.TaskId, intent.Receipt.TaskId);
        Assert.Contains("main h1", intent.Prompt);
        Assert.Contains("https://stage.example.test/pricing", intent.Prompt);
        Assert.Contains("Increase the heading contrast", intent.Prompt);
        var match = Regex.Match(intent.Prompt, @"attachments/(preview-feedback-[a-f0-9]+\.png)");
        Assert.True(match.Success, intent.Prompt);

        var saved = await client.GetAsync($"{path}/attachments/{match.Groups[1].Value}");
        saved.EnsureSuccessStatusCode();
        Assert.Equal(crop, await saved.Content.ReadAsByteArrayAsync());

        using var runnerRead = await client.GetAsync(
            $"/api/v1/runner/tasks/{task.TaskKey}/attachments/{match.Groups[1].Value}");
        runnerRead.EnsureSuccessStatusCode();
        Assert.Equal(crop, await runnerRead.Content.ReadAsByteArrayAsync());
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client, string path, TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<TResponse>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }
}
