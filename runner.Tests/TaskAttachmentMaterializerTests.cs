using System.Net;
using System.Text;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class TaskAttachmentMaterializerTests
{
    [Theory]
    [InlineData(true, "/api/v1/runner/tasks/PFB-8/attachments/preview-feedback-crop.png")]
    [InlineData(false, "/api/tasks/PFB-8/attachments/preview-feedback-crop.png")]
    public async Task Continuation_crop_is_downloaded_into_the_runner_workspace(
        bool durable, string expectedPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-crop-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var gitDirectory = Path.Combine(root, "git");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(gitDirectory);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".git"), "gitdir: ../git\n");
        var crop = Encoding.UTF8.GetBytes("marked preview crop " + Guid.NewGuid());
        var handler = new CropHandler(crop);
        try
        {
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
            using var client = new TaskServerClient(http, "runner-1", usesDurableTaskServer: durable);

            var files = await TaskAttachmentMaterializer.MaterializeAsync(
                client, "PFB-8",
                "Please fix the marked heading: ![crop](attachments/preview-feedback-crop.png)",
                workspace, 25L * 1024 * 1024, CancellationToken.None);

            var delivered = Assert.Single(files);
            Assert.Equal(Path.Combine(workspace, "attachments", "preview-feedback-crop.png"), delivered);
            Assert.Equal(crop, await File.ReadAllBytesAsync(delivered));
            Assert.Equal(expectedPath, Assert.Single(handler.Paths));
            Assert.Contains("/attachments/preview-feedback-crop.png",
                await File.ReadAllTextAsync(Path.Combine(gitDirectory, "info", "exclude")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CropHandler(byte[] crop) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(crop),
            });
        }
    }
}
