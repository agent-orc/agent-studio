using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelSelectionPolicyStoreTests
{
    [Fact]
    public void Legacy_routes_are_imported_once_and_the_old_file_stays_read_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "model-selection-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var projects = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
            projects.SetPipelineStep("project", PipelineTypes.Task, "aspect-code-quality",
                new PipelineStepSetting { CliType = "claude", Model = "claude-opus-5" });
            var oldPath = Path.Combine(root, "cli-model-routing.json");
            var oldContent = JsonSerializer.Serialize(new[] { new CliModelRouteProfile
            {
                CliType = "claude", PrimaryModel = "claude-opus-5",
                FallbackCliType = "codex", FallbackModel = "gpt-6-sol",
            } });
            File.WriteAllText(oldPath, oldContent);
            var ledger = new LoggingModelSelectionLedger(NullLogger<LoggingModelSelectionLedger>.Instance);

            var policy = new ModelSelectionPolicyStore(config, projects, ledger).Get();
            var reloaded = new ModelSelectionPolicyStore(config, projects, ledger).Get();

            Assert.Equal(1, policy.Migration.InheritedSteps);
            Assert.Equal(policy.Migration, reloaded.Migration);
            Assert.Equal("gpt-6-sol", reloaded.LegacyProfiles["claude"].FallbackModel);
            Assert.Equal(new SelectionRoute("claude", "claude-opus-5", "xhigh"),
                reloaded.ModelFallbacks["gpt-6-sol:xhigh"]);
            Assert.Equal("catalogue", reloaded.Consumers["coding"].Fallbacks?.Mode);
            Assert.Equal(oldContent, File.ReadAllText(oldPath));
            Assert.True(File.Exists(Path.Combine(root, "model-selection-policy.json")));
            Assert.Null(PipelineTypeSettings.ForType(projects.Get("project"), PipelineTypes.Task)!
                .PipelineSteps!["aspect-code-quality"].Model);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
