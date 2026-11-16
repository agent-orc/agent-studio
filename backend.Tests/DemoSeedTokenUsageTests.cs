using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DemoSeedTokenUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "demo-token-seed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Owner_day_receipts_reconcile_with_pipeline_steps_and_workspace_totals()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "scripts", "seed-demo-workspace.mjs")))
            repo = repo.Parent;
        Assert.NotNull(repo);
        using var process = Process.Start(new ProcessStartInfo("node")
        {
            WorkingDirectory = repo.FullName,
            ArgumentList = { "scripts/seed-demo-workspace.mjs", "--root", _root, "--owner-day" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(120_000), "Demo seed timed out.");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());

        var projectPaths = new[]
        {
            (Name: "Demo App", Path: Path.Combine(_root, "projects", "demo-app")),
            (Name: "Demo Platform", Path: Path.Combine(_root, "projects", "demo-platform")),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var ledger = new ProjectTokenLedger(new AgentMessageBusStore(), config, new ProjectTokenReceiptReader());
        var pricing = TokenTestServices.Pricing();
        long workspaceTokens = 0;
        var unpricedModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projectPaths)
        {
            var snapshot = ledger.LoadSnapshot(project.Name, project.Path);
            var taskFiles = Directory.GetFiles(Path.Combine(project.Path, "tasks"), "task.json", SearchOption.AllDirectories);
            var histories = 0;
            long stepTokens = 0;
            foreach (var taskFile in taskFiles)
            {
                var pipelineFile = Path.Combine(Path.GetDirectoryName(taskFile)!, "pipeline-execution.json");
                if (!File.Exists(pipelineFile)) continue;
                histories++;
                using var pipeline = JsonDocument.Parse(File.ReadAllText(pipelineFile));
                using var task = JsonDocument.Parse(File.ReadAllText(taskFile));
                var steps = pipeline.RootElement.GetProperty("steps").EnumerateArray().ToArray();
                var receipt = task.RootElement.GetProperty("tokenSummary");
                var calls = receipt.GetProperty("entries").EnumerateArray().ToArray();
                Assert.Equal(steps.Length, calls.Length);
                for (var i = 0; i < steps.Length; i++)
                {
                    Assert.Equal(steps[i].GetProperty("model").GetString(), calls[i].GetProperty("model").GetString());
                    Assert.Equal(steps[i].GetProperty("startedAt").GetString(), calls[i].GetProperty("ts").GetString());
                    foreach (var field in new[] { "inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens" })
                        Assert.Equal(steps[i].GetProperty(field).GetInt64(), calls[i].GetProperty(field).GetInt64());
                    stepTokens += new[] { "inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens" }
                        .Sum(field => steps[i].GetProperty(field).GetInt64());
                }
            }
            Assert.True(histories > 0, $"{project.Name} has no pipeline history.");
            Assert.Equal(histories * 2, snapshot.Entries.Count);
            var projectTokens = snapshot.Entries.Sum(entry => (long)entry.TokenUsage!.InputTokens
                + entry.TokenUsage.OutputTokens + entry.TokenUsage.CacheReadTokens + entry.TokenUsage.CacheCreationTokens);
            Assert.Equal(stepTokens, projectTokens);
            foreach (var entry in snapshot.Entries)
            {
                var usage = entry.TokenUsage!;
                if (!pricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                        usage.CacheReadTokens, usage.CacheCreationTokens, entry.Ts).ModelKnown)
                    unpricedModels.Add(usage.Model!);
            }
            workspaceTokens += projectTokens;
        }
        Assert.True(workspaceTokens > 0);
        Assert.Contains("demo-unpriced-model", unpricedModels);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
