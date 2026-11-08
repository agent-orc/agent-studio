using System.Text.RegularExpressions;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Raw workspace walks belong to index refresh and the three audited background
/// jobs. An HTTP route or route helper must consume a published snapshot.
/// </summary>
[Trait("Category", "Guard")]
public sealed class RequestRawScanBoundaryTests
{
    [Fact]
    public void FeatureCallersOfRawScanMatchBackgroundInventory()
    {
        var root = RepositoryRoot.Find();
        var backend = Path.Combine(root, "backend");
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Features/Git/GitStateIndexService.cs"] = 1,
            ["Features/Pipeline/AcceptedIntegrationBackstopHostedService.cs"] = 1,
            ["Features/Tasks/Acceptance/HistoricalIntegrationVerificationSweep.cs"] = 1,
        };
        var actual = Directory.EnumerateFiles(Path.Combine(backend, "Features"), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is not "TaskScannerService.cs" and not "TaskIndexCache.cs")
            .Select(path => (Path: path, Count: Regex.Matches(File.ReadAllText(path),
                @"\bScanAllJobsRaw\s*\(").Count))
            .Where(item => item.Count > 0)
            .ToDictionary(item => Path.GetRelativePath(backend, item.Path).Replace('\\', '/'),
                item => item.Count, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    }
}
