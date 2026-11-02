using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// clock-independent: the ADR heading date is parsed as text and never compared with now.

namespace AgentStudio.Tests;

/// <summary>
/// <c>GET /api/projects/{p}/architecture</c> reads the ADR archive of the
/// project checkout. Only a regular checkout file within the size limit is
/// parsed; anything else reads as an absent archive.
/// </summary>
public sealed class ArchitectureOverviewCheckoutBoundaryTests : IDisposable
{
    private const string ProjectName = "adr-boundary";
    private const string AdrRel = "docs/system/architecture/decisions/adr-archive.md";
    private const string Archive = """
        # Decisions

        ## ADR-0001 - Keep it small (2026-01-01)

        **Status:** Accepted

        Body text.
        """;

    private readonly CheckoutFileFixture _checkout = new("adr-boundary");

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public void RegularArchive_IsServed()
    {
        _checkout.Write(AdrRel, Archive);
        _checkout.Commit();

        var docs = BuildService();
        var overview = docs.GetArchitectureOverview(ProjectName);

        Assert.NotNull(overview);
        Assert.True(overview.Exists);
        Assert.Contains(overview.Decisions, decision => decision.Id == "ADR-0001");
        Assert.NotNull(docs.GetArchitectureDecision(ProjectName, "ADR-0001"));
    }

    [Fact]
    public void CommittedLinkToFileOutsideCheckout_IsRefused()
    {
        var outside = _checkout.OutsideFile("adr.md", Archive);
        if (!_checkout.TryCommitLink(AdrRel, outside)) return;

        AssertAbsent();
    }

    [Fact]
    public void CommittedLinkToProcessEnvironment_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(_checkout.TryCommitLink(AdrRel, "/proc/self/environ"));

        AssertAbsent();
    }

    [Fact]
    public void CommittedLinkToGitConfig_IsRefused()
    {
        if (!_checkout.TryCommitLink(AdrRel, _checkout.Full(".git/config"))) return;

        AssertAbsent();
    }

    [Fact]
    public void LinkedDocsDirectory_IsRefused()
    {
        var outsideDocs = Path.Combine(_checkout.Outside, "docs");
        Directory.CreateDirectory(Path.Combine(outsideDocs, "system", "architecture", "decisions"));
        File.WriteAllText(Path.Combine(outsideDocs, "system", "architecture", "decisions", "adr-archive.md"), Archive);
        try
        {
            Directory.CreateSymbolicLink(_checkout.Full("docs"), outsideDocs);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        AssertAbsent();
    }

    [Fact]
    public void OversizeArchive_IsRefused()
    {
        _checkout.WriteOversize(AdrRel, ProjectDocsService.MaxAdrArchiveBytes);

        AssertAbsent();
    }

    private void AssertAbsent()
    {
        var docs = BuildService();
        var overview = docs.GetArchitectureOverview(ProjectName);
        Assert.NotNull(overview);
        Assert.False(overview.Exists);
        Assert.Empty(overview.Decisions);
        Assert.Null(docs.GetArchitectureDecision(ProjectName, "ADR-0001"));
    }

    private ProjectDocsService BuildService()
    {
        var jobs = Path.Combine(_checkout.Parent, "jobs");
        Directory.CreateDirectory(jobs);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _checkout.Root,
                ["WatchPaths:0:Path"] = jobs,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        return new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);
    }
}
