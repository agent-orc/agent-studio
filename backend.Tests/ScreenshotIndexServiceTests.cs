using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ScreenshotIndexServiceTests : IDisposable
{
    private readonly TaskArtifactFixture _files = new();
    public void Dispose() => _files.Dispose();

    [Theory]
    [InlineData("results-private")]
    [InlineData("RESULTS-private")]
    [InlineData("RESULTS")]
    public void AbsoluteSiblingImage_IsDenied(string sibling)
    {
        // The case-only sibling exists separately only on case-sensitive hosts.
        if (sibling == "RESULTS" && OperatingSystem.IsWindows()) return;
        var outside = _files.Write(sibling + "/secret.png");
        Assert.Null(Service().ResolveScreenshotFile(TaskArtifactFixture.Id, outside, _files.Watch).Path);
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("ancestor")]
    [InlineData("root")]
    public void LinkedScreenshot_IsDenied(string kind)
    {
        _files.Link("results", kind);
        Assert.Null(Service().ResolveScreenshotFile(TaskArtifactFixture.Id, "nested/proof.png", _files.Watch).Path);
    }

    [Fact]
    public void NestedImage_IsReadable_AndNonImageIsDenied()
    {
        var image = _files.Write("results/nested/proof.png");
        _files.Write("results/nested/report.html");
        Assert.Equal((image, "image/png"), Service().ResolveScreenshotFile(TaskArtifactFixture.Id, "nested/proof.png", _files.Watch));
        Assert.Null(Service().ResolveScreenshotFile(TaskArtifactFixture.Id, "nested/report.html", _files.Watch).Path);
    }

    private ScreenshotIndexService Service() => new(_files.Scanner, NullLogger<ScreenshotIndexService>.Instance);
}

internal sealed class TaskArtifactFixture : IDisposable
{
    internal const string Id = "ARTIFACT-1";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "artifact-boundary-" + Guid.NewGuid().ToString("N"));
    internal string Workspace => Path.Combine(Root, "workspace");
    internal string Watch => Path.Combine(Workspace, "projects", "test");
    internal string Job => Path.Combine(Watch, TaskStates.Ready, Id);
    internal TaskScannerService Scanner { get; }

    internal TaskArtifactFixture()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(Watch, state));
        Directory.CreateDirectory(Job);
        File.WriteAllText(Path.Combine(Job, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        { id = Id, title = "Boundary fixture", state = TaskStates.Ready, order = 1, agent = "codex" }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["WatchPaths:0:Name"] = "test", ["WatchPaths:0:Path"] = Watch }).Build();
        Scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
    }

    internal string Write(string relative)
    {
        var path = Path.Combine(Job, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "disposable fixture " + Guid.NewGuid().ToString("N"));
        return path;
    }

    internal void Link(string directory, string kind)
    {
        var outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        File.WriteAllText(Path.Combine(outside, "nested", "proof.png"), "harmless outside fixture");
        File.WriteAllText(Path.Combine(outside, "proof.png"), "harmless outside attachment");
        var artifact = Path.Combine(Job, directory);
        if (kind == "root") Directory.CreateSymbolicLink(artifact, outside);
        else
        {
            Directory.CreateDirectory(artifact);
            if (kind == "ancestor") Directory.CreateSymbolicLink(Path.Combine(artifact, "nested"), Path.Combine(outside, "nested"));
            else
            {
                Directory.CreateDirectory(Path.Combine(artifact, "nested"));
                File.CreateSymbolicLink(Path.Combine(artifact, "nested", "proof.png"), Path.Combine(outside, "nested", "proof.png"));
            }
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
