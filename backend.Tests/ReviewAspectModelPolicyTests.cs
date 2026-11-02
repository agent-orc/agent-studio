using AgentStudio.Pipeline;
using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3420: the default review aspect model belongs to the review CLI in use,
/// and a pair the model metadata registry rejects never reaches a sealed plan
/// (AOW-15, 2026-11-02: Claude CLI sealed with a GPT model).
/// </summary>
public sealed class ReviewAspectModelPolicyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aspect-model-" + Guid.NewGuid().ToString("N"));

    public ReviewAspectModelPolicyTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Review the concept.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Default_without_settings_keeps_the_codex_support_model()
    {
        var choice = ReviewAspectModelPolicy.ResolveDefault(null, null);

        Assert.Equal(CliTypes.Codex, choice.CliType);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, choice.Model);
        Assert.Null(choice.Fault);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("/usr/local/bin/claude")]
    public void Default_model_is_resolved_from_the_claude_cli(string cli)
    {
        var choice = ReviewAspectModelPolicy.ResolveDefault(cli, null);

        Assert.Equal(CliTypes.Claude, choice.CliType);
        Assert.Equal(ModelMetadataRegistry.DefaultForCli(CliTypes.Claude), choice.Model);
        Assert.True(ModelMetadataRegistry.IsCompatibleWithCli(CliTypes.Claude, choice.Model));
        Assert.Null(choice.Fault);
    }

    [Fact]
    public void Rejected_host_pair_is_a_planning_fault_naming_the_setting()
    {
        var choice = ReviewAspectModelPolicy.ResolveDefault("claude", ModelIds.Gpt55);

        Assert.Equal(CliTypes.Claude, choice.CliType);
        Assert.True(ModelMetadataRegistry.IsCompatibleWithCli(CliTypes.Claude, choice.Model));
        Assert.Contains(ReviewAspectModelPolicy.ModelSetting, choice.Fault);
        Assert.Contains(ModelIds.Gpt55, choice.Fault);
    }

    [Fact]
    public void Compatible_host_pair_is_kept()
    {
        var choice = ReviewAspectModelPolicy.ResolveDefault("claude", ModelIds.ClaudeSonnet5);

        Assert.Equal(new ReviewAspectModelChoice(CliTypes.Claude, ModelIds.ClaudeSonnet5), choice);
    }

    [Fact]
    public void Step_matrix_resolves_cli_owned_models_and_falls_back_on_rejected_pairs()
    {
        var host = ReviewAspectModelPolicy.ResolveDefault(null, null);
        PipelineStepConfigResolver.ModelResolution Runtime() =>
            new(host.Model, PipelineStepConfigResolver.ModelSourceRuntime, null, null, null, null, host.Model);
        PipelineStepConfigResolver.ModelResolution Step(string model) =>
            new(model, PipelineStepConfigResolver.ModelSourceStep, model, null, null, null, host.Model);

        Assert.Equal(host, ReviewAspectModelPolicy.ResolveStep(host, "aspect-code-quality", null, Runtime()));

        var claudeOnly = ReviewAspectModelPolicy.ResolveStep(host, "aspect-code-quality", "claude", Runtime());
        Assert.Equal(CliTypes.Claude, claudeOnly.CliType);
        Assert.Equal(ReviewAspectModelPolicy.DefaultModelFor(CliTypes.Claude), claudeOnly.Model);
        Assert.Null(claudeOnly.Fault);

        Assert.Equal(new ReviewAspectModelChoice(CliTypes.Claude, ModelIds.ClaudeSonnet5),
            ReviewAspectModelPolicy.ResolveStep(host, "aspect-code-quality", "claude", Step(ModelIds.ClaudeSonnet5)));

        var rejected = ReviewAspectModelPolicy.ResolveStep(host, "aspect-code-quality", "claude", Step(ModelIds.Gpt55));
        Assert.Equal(host.CliType, rejected.CliType);
        Assert.Equal(host.Model, rejected.Model);
        Assert.Contains("pipelineSteps.aspect-code-quality.model", rejected.Fault);

        var modelOnly = ReviewAspectModelPolicy.ResolveStep(host, "aspect-code-quality", null, Step(ModelIds.ClaudeOpus5));
        Assert.Equal(host with { Fault = modelOnly.Fault }, modelOnly);
        Assert.Contains(ReviewAspectModelPolicy.CliSetting, modelOnly.Fault);
    }

    [Theory]
    [InlineData("claude", "gpt-5.5", true)]
    [InlineData("codex", "claude-opus-5", true)]
    [InlineData("claude", "claude-opus-5", false)]
    [InlineData("codex", "gpt-5.5", false)]
    [InlineData(null, "gpt-5.5", false)]
    [InlineData("claude", null, false)]
    [InlineData("claude", "some-unlisted-model", false)]
    public void Settings_refusal_rejects_only_a_registry_rejected_pair(string? cli, string? model, bool refused)
    {
        var refusal = ReviewAspectModelPolicy.SettingsRefusal("aspect-code-quality", cli, model);

        Assert.Equal(refused, refusal is not null);
        if (refused) Assert.Contains("pipelineSteps.aspect-code-quality.model", refusal);
    }

    [Fact]
    public void Plan_with_claude_review_cli_and_no_model_setting_seals_a_claude_model()
    {
        var plan = BuildPlan(new() { [ReviewAspectModelPolicy.CliSetting] = "claude" });

        var aspect = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit");
        Assert.Equal(CliTypes.Claude, aspect.CliType);
        Assert.True(ModelMetadataRegistry.IsCompatibleWithCli(CliTypes.Claude, aspect.Model));
    }

    [Fact]
    public void Plan_never_seals_a_rejected_host_pair_and_logs_the_planning_error()
    {
        var logger = new CapturingLogger();
        var plan = BuildPlan(new()
        {
            [ReviewAspectModelPolicy.CliSetting] = "claude",
            [ReviewAspectModelPolicy.ModelSetting] = ModelIds.Gpt55,
        }, logger: logger);

        var aspect = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit");
        Assert.Equal(CliTypes.Claude, aspect.CliType);
        Assert.True(ModelMetadataRegistry.IsCompatibleWithCli(CliTypes.Claude, aspect.Model));
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("remote-review-plan-model-pair-rejected", error.Message);
        Assert.Contains(ReviewAspectModelPolicy.ModelSetting, error.Message);
    }

    [Fact]
    public void Plan_falls_back_to_the_valid_default_for_a_rejected_project_step_pair()
    {
        var logger = new CapturingLogger();
        var plan = BuildPlan(new(), new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.RemoteConceptFitStepId] = new PipelineStepSetting
                {
                    CliType = "claude", Model = ModelIds.Gpt55,
                },
            },
        }, logger);

        var aspect = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit");
        Assert.Equal(CliTypes.Codex, aspect.CliType);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, aspect.Model);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("remote-review-plan-model-pair-fallback", StringComparison.Ordinal));
        Assert.Contains($"pipelineSteps.{PipelineCatalogue.RemoteConceptFitStepId}.model", warning.Message);
    }

    private ReviewPlanDto BuildPlan(
        Dictionary<string, string?> configuration,
        ProjectSettings? settings = null,
        CapturingLogger? logger = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var task = new TaskInfo
        {
            Id = "AOW-15", TaskKey = "AOW-15", Title = "Concept", Mode = TaskModes.Concept,
            FolderPath = _folder, ProjectName = "Test",
        };
        return new RemoteReviewPlanBuilder(aspects, config, logger: logger)
            .Build(task, null, settings, "refs/heads/main");
    }

    private sealed class CapturingLogger : ILogger<RemoteReviewPlanBuilder>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
