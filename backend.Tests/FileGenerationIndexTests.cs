using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class FileGenerationIndexTests : IDisposable
{
    private readonly string _jobFolder;

    public FileGenerationIndexTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "file-generation-index-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Upsert_WritesSidecarAndReplacesExistingEntryForFile()
    {
        var index = new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance);

        index.Upsert(_jobFolder, new FileGenerationMeta
        {
            File = "aspect-code-quality.md",
            Kind = "aspect",
            Model = "claude-haiku-4-5",
            Cli = "claude",
            TokensIn = 10,
            TokensOut = 5,
            DurationMs = 400,
        });
        index.Upsert(_jobFolder, new FileGenerationMeta
        {
            File = "aspect-code-quality.md",
            Kind = "aspect",
            Model = "claude-sonnet-4-6",
            Cli = "claude",
            TokensIn = 20,
            TokensOut = 7,
            DurationMs = 800,
        });

        var path = Path.Combine(_jobFolder, FileGenerationIndex.RelativePath);
        Assert.True(File.Exists(path));
        var entries = index.ReadForJob(_jobFolder, cacheLegacy: false);

        var entry = Assert.Single(entries.Values);
        Assert.Equal("aspect-code-quality.md", entry.File);
        Assert.Equal("claude-sonnet-4-6", entry.Model);
        Assert.Equal(27, entry.TokensTotal);
        Assert.Equal(800, entry.DurationMs);
    }

    [Fact]
    public void ReadForJob_CachesLegacyPipelineProjectionIntoSidecar()
    {
        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var started = new DateTime(2026, 6, 9, 9, 0, 0, DateTimeKind.Utc);
        pipelineLog.EnsureRun(_jobFolder, PipelineCatalogue.Standard, "demo", "job-1", started);
        pipelineLog.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = "aspect-requirement-fit",
            Kind = StepKind.Aspect,
            Model = "claude-haiku-4-5",
            Status = PipelineStepStatus.Passed,
            StartedAt = started,
            CompletedAt = started.AddSeconds(3),
            DurationMs = 3_000,
            InputTokens = 100,
            OutputTokens = 40,
            CacheReadTokens = 10,
            CacheCreationTokens = 5,
        });
        var index = new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance, pipelineLog);

        var entries = index.ReadForJob(_jobFolder);

        Assert.True(entries.TryGetValue("aspect-requirement-fit.md", out var entry));
        Assert.Equal("aspect-requirement-fit.md", entry.File);
        Assert.Equal("aspect", entry.Kind);
        Assert.Equal("claude-haiku-4-5", entry.Model);
        Assert.Equal(155, entry.TokensTotal);
        Assert.Equal(1, entry.RunIndex);
        Assert.True(File.Exists(Path.Combine(_jobFolder, FileGenerationIndex.RelativePath)));
    }

    [Fact]
    public void CurrentRunIndex_ReturnsCurrentPipelineAttempt()
    {
        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        pipelineLog.Begin(_jobFolder, PipelineCatalogue.Standard, "demo", "job-1");
        pipelineLog.Begin(_jobFolder, PipelineCatalogue.Standard, "demo", "job-1");
        var index = new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance, pipelineLog);

        Assert.Equal(2, index.CurrentRunIndex(_jobFolder));
    }
}
