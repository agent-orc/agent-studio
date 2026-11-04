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
    public void Migration_preserves_other_step_settings_across_twelve_projects_and_three_pipeline_types()
    {
        var root = Path.Combine(Path.GetTempPath(), "model-selection-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var projects = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
            var pipelineTypes = new[] { PipelineTypes.Task, PipelineTypes.Bug, PipelineTypes.Feature };
            for (var i = 0; i < 12; i++)
            {
                var project = $"project-{i:D2}";
                foreach (var pipelineType in pipelineTypes)
                {
                    projects.SetPipelineStep(project, pipelineType, "aspect-code-quality",
                        new PipelineStepSetting { CliType = "claude", Model = "claude-opus-5", ThinkingLevel = "high" });
                    projects.SetPipelineStep(project, pipelineType, "aspect-security",
                        new PipelineStepSetting { CliType = "claude", Model = "claude-sonnet-5", ThinkingLevel = "high",
                            Enabled = false, Mode = "warn" });
                }
            }
            var profiles = new Dictionary<string, CliModelRouteProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new() { CliType = "claude", PrimaryModel = "claude-opus-5" },
            };

            var migration = projects.MigrateModelSelectionSteps(profiles);

            Assert.Equal(36, migration.InheritedSteps);
            Assert.Equal(36, migration.ExplicitSteps);
            Assert.Equal(12, migration.Projects);
            Assert.True(migration.Applied);
            foreach (var i in Enumerable.Range(0, 12))
            foreach (var pipelineType in pipelineTypes)
            {
                var settings = projects.Get($"project-{i:D2}");
                var steps = PipelineTypeSettings.ForType(settings, pipelineType)!.PipelineSteps!;
                Assert.Null(steps["aspect-code-quality"].Model);
                Assert.Equal("high", steps["aspect-code-quality"].ThinkingLevel);
                Assert.Equal("claude-sonnet-5", steps["aspect-security"].Model);
                Assert.Equal("high", steps["aspect-security"].ThinkingLevel);
                Assert.False(steps["aspect-security"].Enabled);
                Assert.Equal("warn", steps["aspect-security"].Mode);
                Assert.Equal("claude-sonnet-5", settings.ModelSelectionOverrides!["review-aspect"]
                    .ByPipelineType![pipelineType].ByStep!["aspect-security"].Primary!.Model);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
