using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Magazine;
using AgentStudio.Registry;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// MAG-1 (AGT-3714): <c>studio-op magazine publish</c> against a running
/// Studio on a loopback port. The first call creates the article from the
/// Markdown file, the second refreshes it with the revision it read. The
/// local profile serves plain HTTP on loopback; token scopes are covered by
/// <see cref="MagazineEndpointTests"/>.
/// </summary>
public sealed class MagazineStudioOpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "magazine-studio-op-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [SkippableFact]
    public async Task Studio_op_publishes_and_then_refreshes_an_article_on_a_running_studio()
    {
        Skip.IfNot(NodeAvailable(out var node), "node is not on PATH; skipping the studio-op magazine test.");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = workspace,
                ["AllowedHosts"] = "*",
            }));
        });
        factory.UseKestrel(0);
        factory.StartServer();
        var url = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
        var workspaces = factory.Services.GetRequiredService<WorkspaceRegistry>();
        workspaces.EnsureLoaded();
        var project = factory.Services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(
            Path.Combine(workspace, "projects", "demo"), "Demo", workspaces.EnsureDefaultWorkspace().Id);

        var file = Path.Combine(_root, "decision-notes.md");
        File.WriteAllText(file, Article(project.Id, "24 can be accepted"));
        var created = await RunAsync(node!, url, file);
        Assert.True(created.Exit == 0, $"exit {created.Exit}\n{created.Stdout}\n{created.Stderr}");
        using (var json = JsonDocument.Parse(created.Stdout))
        {
            Assert.True(json.RootElement.GetProperty("created").GetBoolean());
            Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt32());
        }

        File.WriteAllText(file, Article(project.Id, "all accepted"));
        var refreshed = await RunAsync(node!, url, file);
        Assert.True(refreshed.Exit == 0, $"exit {refreshed.Exit}\n{refreshed.Stdout}\n{refreshed.Stderr}");
        using (var json = JsonDocument.Parse(refreshed.Stdout))
        {
            Assert.False(json.RootElement.GetProperty("created").GetBoolean());
            Assert.Equal(2, json.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("previousRevision").GetInt32());
        }

        var store = factory.Services.GetRequiredService<MagazineStore>();
        var scope = new MagazineScope(MagazineScopeKind.Project, project.Id);
        var article = store.Get(scope, "decision-notes")!;
        Assert.Equal("Decision notes: 10 November", article.Title);
        Assert.Equal("decision-brief", article.Kind);
        Assert.Equal([new MagazineSource("card", "AGT-3714"), new MagazineSource("batch", "batch-17")], article.Sources);
        Assert.Contains("all accepted", article.Body, StringComparison.Ordinal);
        Assert.Contains("24 can be accepted", store.Get(scope, "decision-notes", 1)!.Body, StringComparison.Ordinal);
        Assert.Equal([2, 1], store.History(scope, "decision-notes")!.Select(entry => entry.Revision));
    }

    private static string Article(string projectId, string state) => string.Join('\n',
        "---",
        $"scope: project:{projectId}",
        "slug: decision-notes",
        "title: \"Decision notes: 10 November\"",
        "summary: >",
        "  29 cards wait for you: 24 can be accepted,",
        "  4 need your decision, 1 gets rework.",
        "kind: decision-brief",
        "sources: [card:AGT-3714, batch:batch-17]",
        "---",
        "",
        "## Waiting for you",
        "",
        $"- The batch: {state}.",
        "");

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string node, string url, string file)
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        var start = new ProcessStartInfo
        {
            FileName = node,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot.Find(), "tools", "studio-op", "studio-op.mjs"));
        foreach (var argument in new[] { "magazine", "publish", file, "--json", "--url", url }) start.ArgumentList.Add(argument);
        // Only what the CLI needs: a home without a token file and the PATH.
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot) start.Environment["SystemRoot"] = systemRoot;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static bool NodeAvailable(out string? node)
    {
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), exe);
            if (File.Exists(candidate))
            {
                node = candidate;
                return true;
            }
        }
        node = null;
        return false;
    }
}
