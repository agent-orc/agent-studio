using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PreviewFeedbackEndpointTests
{
    [Fact]
    public async Task Versioned_feedback_route_saves_crop_and_writes_the_same_cards_fix_round()
    {
        var root = Path.Combine(Path.GetTempPath(), "preview-feedback-http-" + Guid.NewGuid().ToString("N"));
        var jobs = Path.Combine(root, "jobs");
        const string jobId = "preview-heading";
        var folder = Path.Combine(jobs, TaskStates.HumanReview, jobId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = jobId, key = "PFB-1", title = "Improve heading", state = TaskStates.HumanReview,
            order = 1, agent = "codex", cliType = "codex",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Original change request.");

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(root, "workspace"),
                    ["WatchPaths:0:Name"] = "Preview Project",
                    ["WatchPaths:0:Path"] = jobs,
                    ["WatchPaths:0:RootPath"] = root,
                }));
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
            var crop = Encoding.UTF8.GetBytes("marked area " + Guid.NewGuid());
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(crop);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "crop", "selection.png");
            form.Add(new StringContent("Increase the heading contrast"), "comment");
            form.Add(new StringContent("https://stage.example.test/pricing"), "previewAddress");
            form.Add(new StringContent("main h1"), "selector");

            var path = $"/api/v1/projects/-/tasks/{jobId}/preview-feedback?watchPath={Uri.EscapeDataString(jobs)}";
            var response = await client.PostAsync(path, form);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var currentFolder = Assert.Single(Directory.GetDirectories(jobs, jobId, SearchOption.AllDirectories));
            var attachments = Directory.GetFiles(Path.Combine(currentFolder, "attachments"), "*.png");
            var saved = Assert.Single(attachments);
            Assert.Equal(crop, File.ReadAllBytes(saved));
            var markdown = string.Join("\n", Directory.GetFiles(currentFolder, "*.md").Select(File.ReadAllText));
            Assert.Contains("main h1", markdown);
            Assert.Contains("https://stage.example.test/pricing", markdown);
            Assert.Contains("Increase the heading contrast", markdown);
            Assert.Contains("attachments/" + Path.GetFileName(saved), markdown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
