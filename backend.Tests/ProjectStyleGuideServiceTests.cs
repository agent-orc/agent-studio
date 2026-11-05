using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectStyleGuideServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "style-guide-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(
        Path.GetTempPath(), "style-guide-tests-outside-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildCatalogue_HeaderGuideMatchesAngularProjectAndSpecificArea()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        File.WriteAllText(Path.Combine(_root, "frontend", "angular.json"), "{}");
        WriteGuide("header-zones.md", "header-zones", "Header zones",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"header\"]}");

        var catalogue = ProjectStyleGuideService.BuildCatalogue("PROJ-002", "Studio", _root);
        var guide = Assert.Single(catalogue.Guides);
        Assert.Equal("header-zones", guide.Id);
        Assert.Equal(["header"], guide.AppliesTo.TaskAreas);
        Assert.Contains(guide.Match.Technologies, technology => technology.Key == "angular");
    }

    [Fact]
    public void BuildCatalogue_UsesStableProjectAndTechnologyContractsWithMatchReasons()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        File.WriteAllText(Path.Combine(_root, "frontend", "angular.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "backend", "App.csproj"), "<Project />");

        WriteGuide(
            "angular-components.md",
            "angular-components",
            "Angular component guide",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");
        WriteGuide(
            "dotnet-backend.md",
            "dotnet-backend",
            ".NET backend guide",
            "{\"projects\":[\"ASS\"],\"technologies\":[\"dotnet\"],\"taskAreas\":[\"backend\"]}");
        WriteGuide(
            "other-project.md",
            "other-project",
            "Other project guide",
            "{\"projects\":[\"PROJ-999\"],\"technologies\":[\"csharp\"],\"taskAreas\":[\"backend\"]}");
        WriteGuide(
            "display-name.md",
            "display-name",
            "Display-name selector",
            "{\"projects\":[\"Demo project\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");
        File.WriteAllText(Path.Combine(_root, "docs", "quality", "README.md"), "# Ordinary index");

        var catalogue = ProjectStyleGuideService.BuildCatalogue(
            "PROJ-007", "Demo project", _root, ["ASS"]);

        Assert.Equal("PROJ-007", catalogue.ProjectKey);
        Assert.Equal("Demo project", catalogue.ProjectDisplayName);
        Assert.Collection(
            catalogue.Technologies,
            angular => Assert.Equal(("angular", "Angular"), (angular.Key, angular.DisplayLabel)),
            csharp => Assert.Equal(("csharp", "C#"), (csharp.Key, csharp.DisplayLabel)),
            dotnet => Assert.Equal(("dotnet", ".NET"), (dotnet.Key, dotnet.DisplayLabel)));
        Assert.Collection(
            catalogue.Guides.OrderBy(guide => guide.Id),
            angular =>
            {
                Assert.Equal("angular-components", angular.Id);
                Assert.True(angular.Match.ProjectWildcard);
                Assert.Equal("angular", Assert.Single(angular.Match.Technologies).Key);
            },
            dotnet =>
            {
                Assert.Equal("dotnet-backend", dotnet.Id);
                Assert.False(dotnet.Match.ProjectWildcard);
                Assert.Equal("ass", dotnet.Match.ProjectSelector);
                Assert.Equal("dotnet", Assert.Single(dotnet.Match.Technologies).Key);
            });
        Assert.Empty(catalogue.Warnings);

        var json = JsonSerializer.Serialize(catalogue);
        Assert.DoesNotContain("RepositoryRoot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseGuide_RejectsMalformedOrIncompleteDeclaredFrontmatter()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        var malformed = Path.Combine(_root, "docs", "quality", "bad.md");
        File.WriteAllText(malformed,
            "---\nstyleGuideId: bad\ntitle: Bad\nversion: 1\nsummary: Bad\n" +
            "promptSummary: Bad\nappliesTo: not-json\n---\n# Bad");
        var incomplete = Path.Combine(_root, "docs", "quality", "incomplete.md");
        File.WriteAllText(incomplete,
            "---\nstyleGuideId: incomplete\ntitle: Incomplete\nversion: 1\nsummary: Missing prompt\n" +
            "appliesTo: {\"projects\":[\"*\"],\"technologies\":[\"dotnet\"],\"taskAreas\":[\"backend\"]}\n---\n");
        var unknownTechnology = Path.Combine(_root, "docs", "quality", "unknown-technology.md");
        File.WriteAllText(unknownTechnology, ValidGuide(
            "unknown-technology",
            "Unknown technology",
            "{\"projects\":[\"*\"],\"technologies\":[\"typescript\"],\"taskAreas\":[\"frontend\"]}"));

        Assert.Null(ProjectStyleGuideService.ParseGuide(malformed, _root));
        Assert.Null(ProjectStyleGuideService.ParseGuide(incomplete, _root));

        var catalogue = ProjectStyleGuideService.BuildCatalogue("PROJ-001", "Demo", _root);
        Assert.Equal(3, catalogue.Warnings.Count);
        Assert.Contains(catalogue.Warnings, warning => warning.Message.Contains("appliesTo", StringComparison.Ordinal));
        Assert.Contains(catalogue.Warnings, warning => warning.Message.Contains("promptSummary", StringComparison.Ordinal));
        Assert.Contains(catalogue.Warnings, warning => warning.Message.Contains("Unknown technology key", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCatalogue_EmptySelectorsNeverActAsImplicitWildcards()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        WriteGuide(
            "empty-projects.md",
            "empty-projects",
            "Empty projects",
            "{\"projects\":[],\"technologies\":[\"dotnet\"],\"taskAreas\":[\"backend\"]}");
        WriteGuide(
            "empty-technologies.md",
            "empty-technologies",
            "Empty technologies",
            "{\"projects\":[\"*\"],\"technologies\":[],\"taskAreas\":[\"backend\"]}");

        var catalogue = ProjectStyleGuideService.BuildCatalogue("PROJ-001", "Demo", _root);

        Assert.Empty(catalogue.Guides);
        Assert.Empty(catalogue.Warnings);
    }

    [Fact]
    public void BuildCatalogue_RejectsOversizedGuidesAndPackageJsonBeforeReadingThem()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        WriteGuide(
            "oversized.md",
            "oversized",
            "Oversized guide",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");
        File.AppendAllText(
            Path.Combine(_root, "docs", "quality", "oversized.md"),
            new string('x', ProjectStyleGuideService.MaxGuideFileBytes));
        File.WriteAllText(
            Path.Combine(_root, "package.json"),
            "{\"dependencies\":{\"@angular/core\":\"1\"}}" +
            new string(' ', ProjectStyleGuideService.MaxPackageJsonBytes));

        var catalogue = ProjectStyleGuideService.BuildCatalogue("PROJ-001", "Demo", _root);

        Assert.Empty(catalogue.Guides);
        Assert.DoesNotContain(catalogue.Technologies, technology => technology.Key == "angular");
        Assert.Contains(catalogue.Warnings, warning =>
            warning.RelPath == "quality/oversized.md" && warning.Message.Contains("byte limit", StringComparison.Ordinal));
        Assert.Contains(catalogue.Warnings, warning =>
            warning.RelPath == "package.json" && warning.Message.Contains("byte limit", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCatalogue_RejectsGuideAndDiscoverySymlinksThatCouldEscapeTheRepository()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        var outsideSource = Path.Combine(_outside, "source");
        var outsideQuality = Path.Combine(_outside, "quality");
        Directory.CreateDirectory(outsideSource);
        Directory.CreateDirectory(outsideQuality);
        File.WriteAllText(Path.Combine(outsideSource, "angular.json"), "{}");
        File.WriteAllText(Path.Combine(outsideQuality, "outside-guide.md"), ValidGuide(
            "outside-guide", "Outside guide",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}"));

        var directoryLink = Path.Combine(_root, "linked-source");
        var qualityLink = Path.Combine(_root, "docs", "quality");
        Assert.True(TryCreateDirectoryLink(directoryLink, outsideSource),
            "Could not create a directory symlink/junction for the discovery boundary test.");
        Assert.True(TryCreateDirectoryLink(qualityLink, outsideQuality),
            "Could not create a directory symlink/junction for the guide boundary test.");

        var catalogue = ProjectStyleGuideService.BuildCatalogue("PROJ-001", "Demo", _root);

        Assert.Empty(catalogue.Guides);
        Assert.DoesNotContain(catalogue.Technologies, technology => technology.Key == "angular");
        Assert.Contains(catalogue.Warnings, warning =>
            warning.RelPath == "linked-source" && warning.Message.Contains("symbolic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalogue.Warnings, warning =>
            warning.RelPath == "quality" && warning.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCatalogue_EnforcesDeterministicGuideFileCountLimit()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        File.WriteAllText(Path.Combine(_root, "angular.json"), "{}");
        for (var index = 0; index < ProjectStyleGuideService.MaxGuideFiles + 1; index++)
        {
            WriteGuide(
                $"guide-{index:00}.md",
                $"guide-{index:00}",
                $"Guide {index:00}",
                "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");
        }

        var catalogue = ProjectStyleGuideService.BuildCatalogue("PROJ-001", "Demo", _root);

        Assert.Equal(ProjectStyleGuideService.MaxGuideFiles, catalogue.Guides.Count);
        Assert.DoesNotContain(catalogue.Guides, guide => guide.Id == $"guide-{ProjectStyleGuideService.MaxGuideFiles:00}");
        Assert.Contains(catalogue.Warnings, warning => warning.Message.Contains("first 64", StringComparison.Ordinal));
    }

    [Fact]
    public void GetCatalogue_ReusesOneSnapshotUntilExplicitRefresh()
    {
        var repository = Path.Combine(_root, "repository");
        var storage = Path.Combine(_root, "workspace", "projects", "demo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(Path.Combine(repository, "docs", "quality"));
        Directory.CreateDirectory(storage);
        File.WriteAllText(Path.Combine(repository, "angular.json"), "{}");
        WriteGuideAt(
            repository,
            "angular.md",
            "angular-guide",
            "First title",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = storage,
            ["WatchPaths:0:RepositoryPath"] = repository
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var record = registry.EnsureProjectForStorage(storage, "Demo", "ws-default");
        registry.SetRepositoryPath(record.Id, repository);
        var service = new ProjectStyleGuideService(
            scanner, registry, NullLogger<ProjectStyleGuideService>.Instance);

        var first = Assert.IsType<ProjectStyleGuideCatalogue>(service.GetCatalogue("Demo"));
        WriteGuideAt(
            repository,
            "angular.md",
            "angular-guide",
            "Refreshed title",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");
        var cached = Assert.IsType<ProjectStyleGuideCatalogue>(service.GetCatalogue("Demo"));
        var refreshed = Assert.IsType<ProjectStyleGuideCatalogue>(service.GetCatalogue("Demo", refresh: true));

        Assert.Same(first, cached);
        Assert.Equal(record.Id, first.ProjectKey);
        Assert.Equal(first.SnapshotId, cached.SnapshotId);
        Assert.Equal("First title", Assert.Single(cached.Guides).Title);
        Assert.NotEqual(first.SnapshotId, refreshed.SnapshotId);
        Assert.Equal("Refreshed title", Assert.Single(refreshed.Guides).Title);
        Assert.True(refreshed.RefreshAfterUtc > refreshed.CapturedAtUtc);
    }

    private void WriteGuide(string fileName, string id, string title, string appliesTo)
        => WriteGuideAt(_root, fileName, id, title, appliesTo);

    private static void WriteGuideAt(
        string repositoryRoot,
        string fileName,
        string id,
        string title,
        string appliesTo)
    {
        var directory = Path.Combine(repositoryRoot, "docs", "quality");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), ValidGuide(id, title, appliesTo));
    }

    private static string ValidGuide(string id, string title, string appliesTo)
        => $"---\nstyleGuideId: {id}\ntitle: {title}\nversion: 1\nsummary: summary\n" +
           $"promptSummary: Follow {id}.\nappliesTo: {appliesTo}\n---\n# {title}\n";

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or PlatformNotSupportedException
                                   or NotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(link);
                start.ArgumentList.Add(target);
                using var process = System.Diagnostics.Process.Start(start);
                process?.WaitForExit();
                return process?.ExitCode == 0 && Directory.Exists(link);
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        TryDeleteLink(Path.Combine(_root, "linked-source"));
        TryDeleteLink(Path.Combine(_root, "docs", "quality"));
        TryDelete(_root);
        TryDelete(_outside);
    }

    private static void TryDeleteLink(string path)
    {
        try { Directory.Delete(path); }
        catch
        {
            try { File.Delete(path); }
            catch { /* best effort */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}
