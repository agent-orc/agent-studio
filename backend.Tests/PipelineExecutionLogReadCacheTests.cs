using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The board polls call <see cref="PipelineExecutionLog.Read"/> per active card
/// on every request. Read memoizes the normalized record keyed on the file's
/// (mtime, length) so an unchanged file is not re-parsed each poll, while any
/// write (which advances the file's mtime) is observed on the very next Read.
/// </summary>
public sealed class PipelineExecutionLogReadCacheTests : IDisposable
{
    private readonly string _jobFolder;
    private readonly PipelineExecutionLog _log;

    public PipelineExecutionLogReadCacheTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "pipeline-read-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
        _log = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Read_UnchangedFile_ReturnsMemoizedInstanceWithoutReparsing()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        var first = _log.Read(_jobFolder);
        var second = _log.Read(_jobFolder);

        Assert.NotNull(first);
        // A re-parse would allocate a fresh record every call; the same reference
        // proves the second read was served from the mtime/length cache.
        Assert.Same(first, second);
    }

    [Fact]
    public void Read_AfterWrite_ObservesNewRecordBecauseMtimeAdvanced()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var attemptOne = _log.Read(_jobFolder);
        Assert.Equal(1, attemptOne?.Attempt);

        // A restart rewrites pipeline-execution.json (new attempt, archived prior
        // steps): its length and mtime change, so the next Read must not serve
        // the cached attempt-one record.
        _log.Complete(_jobFolder);
        _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        var attemptTwo = _log.Read(_jobFolder);

        Assert.NotSame(attemptOne, attemptTwo);
        Assert.Equal(2, attemptTwo?.Attempt);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        Assert.Null(_log.Read(_jobFolder));
    }
}
