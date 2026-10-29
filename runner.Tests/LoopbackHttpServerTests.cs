using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

[Trait("Category", "MachineBound")]
public sealed class LoopbackHttpServerTests
{
    [Fact]
    public async Task Server_keeps_its_ephemeral_port_and_disposes_twice()
    {
        using var server = new LoopbackHttpServer(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        using var client = new HttpClient();
        Assert.InRange(server.Port, 1, 65535);
        using var response = await client.GetAsync(server.BaseUrl);
        Assert.True(response.IsSuccessStatusCode);
        server.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task Git_server_serves_a_bare_repository_on_its_bound_port()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var seed = Path.Combine(root.FullName, "seed");
            var bare = Path.Combine(root.FullName, "repo.git");
            await GitAsync(root.FullName, "init", "-b", "main", seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "item.txt"), "content");
            await GitAsync(seed, "add", "item.txt");
            await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "-m", "seed");
            await GitAsync(root.FullName, "clone", "--bare", seed, bare);
            using var server = new GitSmartHttpServer(root.FullName);
            var result = await GitAsync(root.FullName, "ls-remote", server.UrlFor("repo.git"), "HEAD");
            Assert.Matches("^[0-9a-f]{40}\\s+HEAD", result.StdOut);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<ProcessResult> GitAsync(string directory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, directory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }
}
