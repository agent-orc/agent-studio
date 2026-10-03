using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiTaskCrossReferenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-task-cross-ref-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LinkAuto_AppendsBothSides_AndDeduplicatesRepeatedCompletion()
    {
        var page = PreparePage("docs/concepts/runtime.md", "# Runtime model\n");
        var task = PrepareTask();
        var runner = new WikiTaskCrossReferenceService(NullLogger<WikiTaskCrossReferenceService>.Instance);

        Assert.Equal(1, runner.LinkAuto(_root, task, ["docs/concepts/runtime.md"]));
        var rescanned = task with { RelatedWikiPages = ReadTaskPages() };
        Assert.Equal(0, runner.LinkAuto(_root, rescanned, ["docs/concepts/runtime.md"]));

        Assert.Single(ReadTaskPages());
        using var sidecar = JsonDocument.Parse(File.ReadAllText(page + ".meta.json"));
        var related = sidecar.RootElement.GetProperty("relatedTasks").EnumerateArray().ToList();
        Assert.Single(related);
        Assert.Equal("AGT-2053", related[0].GetProperty("key").GetString());
        Assert.Equal("auto", related[0].GetProperty("source").GetString());
    }

    [Fact]
    public void LinkAuto_PreservesManualReferences_AndDoesNotCleanDeletedTargets()
    {
        var page = PreparePage("docs/operations/setup.md", "# Setup\n");
        File.WriteAllText(page + ".meta.json", """
        { "quality": "A", "relatedTasks": [
          { "key": "MAN-1", "title": "Manual", "linkedAt": "2026-01-01T00:00:00Z", "source": "manual" }
        ] }
        """);
        var task = PrepareTask();
        var runner = new WikiTaskCrossReferenceService(NullLogger<WikiTaskCrossReferenceService>.Instance);

        runner.LinkAuto(_root, task, ["docs/operations/setup.md"]);
        var persisted = ReadTaskPages();
        File.Delete(page);
        runner.LinkAuto(_root, task with { RelatedWikiPages = persisted }, []);

        Assert.Single(ReadTaskPages());
        using var sidecar = JsonDocument.Parse(File.ReadAllText(page + ".meta.json"));
        Assert.Equal("A", sidecar.RootElement.GetProperty("quality").GetString());
        Assert.Equal(2, sidecar.RootElement.GetProperty("relatedTasks").GetArrayLength());
    }

    [Fact]
    public void LinkAuto_SweepLinksPagesNamingTheTask_AndSkipsOversizedPages()
    {
        // AGT-2991: the docs/ sweep reads each page to look for the task key;
        // a page past the text cap is skipped instead of loaded whole.
        PreparePage("docs/concepts/mentions.md", "# Mentions\nDelivered by AGT-2053.\n");
        var huge = PreparePage("docs/concepts/huge.md", "# Huge\nAGT-2053\n");
        using (var writer = new StreamWriter(huge, append: true))
        {
            var filler = new string('x', 1023);
            for (var i = 0; i < 5 * 1024; i++) writer.WriteLine(filler);
        }
        var task = PrepareTask();
        var runner = new WikiTaskCrossReferenceService(NullLogger<WikiTaskCrossReferenceService>.Instance);

        Assert.Equal(1, runner.LinkAuto(_root, task, []));

        var linked = Assert.Single(ReadTaskPages());
        Assert.EndsWith("concepts/mentions.md", linked.RelPath);
    }

    private TaskInfo PrepareTask()
    {
        var folder = Path.Combine(_root, "task");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{\"id\":\"agt-2053\",\"title\":\"Cross references\"}");
        return new TaskInfo { Id = "agt-2053", Key = "AGT-2053", Title = "Cross references", FolderPath = folder, ProjectName = "Demo" };
    }

    private string PreparePage(string relPath, string content)
    {
        var path = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private List<RelatedWikiPage> ReadTaskPages()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "task", "task.json")));
        return JsonSerializer.Deserialize<List<RelatedWikiPage>>(doc.RootElement.GetProperty("relatedWikiPages").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
