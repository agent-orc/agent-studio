using System.Diagnostics;
using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2793 round 4, wiring point 4: <c>scripts/release/promote-develop-to-main.sh</c>
/// runs on the runner host, outside this backend process, so promotion has no
/// in-process transition to hook <see cref="BranchReclaimTriggerService.ReclaimAfterPromotionToMain"/>
/// off of. The script instead calls this endpoint once its atomic main+tag push
/// is verified. These tests lock in the two observable outcomes of that call.
/// </summary>
public sealed class BranchReclaimPromotionEndpointTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "branch-reclaim-promotion-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;
    private readonly string _repo;

    public BranchReclaimPromotionEndpointTests()
    {
        _watchPath = Path.Combine(_tempDir, "project-store");
        _repo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_watchPath);
        RunGit(_tempDir, "init", "-q", "-b", "main", _repo);
        RunGit(_repo, "config", "user.email", "test@example.com");
        RunGit(_repo, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "seed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "Demo",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repo,
                    ["WatchPaths:0:RepositoryPath"] = _repo,
                });
            });
            b.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

    [Fact]
    public async Task KnownProject_ReturnsOk()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.PostAsync(
            $"/api/git/branch-reclaim/promotion?project={Uri.EscapeDataString("Demo")}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownProject_ReturnsBadRequest()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.PostAsync(
            $"/api/git/branch-reclaim/promotion?project={Uri.EscapeDataString("NoSuchProject")}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
    }
}
