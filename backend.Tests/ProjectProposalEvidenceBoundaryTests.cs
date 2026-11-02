using AgentStudio.Proposals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// clock-independent: the generation date only names the proposal folder and never meets now.

namespace AgentStudio.Tests;

/// <summary>
/// Proposal screenshots are served from the project checkout. Only a regular
/// checkout file within the size limit resolves; a link, Git metadata and an
/// oversize file do not.
/// </summary>
public sealed class ProjectProposalEvidenceBoundaryTests : IDisposable
{
    private const string ProjectName = "proposal-boundary";
    private const string Generation = "2026-07-11";
    private const string ProposalsRel = "docs/concepts/proposals";

    private readonly CheckoutFileFixture _checkout = new("proposal-boundary");

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public void RegularScreenshot_IsServed()
    {
        var full = WriteProposal("assets/evidence.png");
        File.WriteAllBytes(full, [137, 80, 78, 71]);
        _checkout.Commit();

        Assert.Equal(full, BuildService().GetEvidencePath(ProjectName, $"{Generation}/assets/evidence.png"));
    }

    [Fact]
    public void CommittedLinkToFileOutsideCheckout_IsRefused()
    {
        var full = WriteProposal("assets/evidence.png");
        var outside = _checkout.OutsideFile("evidence.png", "outside");
        if (!_checkout.TryCommitLink(Rel(full), outside)) return;

        Assert.Null(BuildService().GetEvidencePath(ProjectName, $"{Generation}/assets/evidence.png"));
    }

    [Fact]
    public void CommittedLinkToProcessEnvironment_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var full = WriteProposal("assets/evidence.png");
        Assert.True(_checkout.TryCommitLink(Rel(full), "/proc/self/environ"));

        Assert.Null(BuildService().GetEvidencePath(ProjectName, $"{Generation}/assets/evidence.png"));
    }

    [Fact]
    public void GitConfig_IsRefused()
    {
        var nested = WriteProposal(".git/config");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "[core]\n");
        var linked = WriteProposal("assets/config.png", id: "survey-002");

        var service = BuildService();
        Assert.Null(service.GetEvidencePath(ProjectName, $"{Generation}/.git/config"));
        if (_checkout.TryCommitLink(Rel(linked), _checkout.Full(".git/config")))
            Assert.Null(BuildService().GetEvidencePath(ProjectName, $"{Generation}/assets/config.png"));
    }

    [Fact]
    public void OversizeScreenshot_IsRefused()
    {
        var full = WriteProposal("assets/evidence.png");
        _checkout.WriteOversize(Rel(full), ProjectProposalService.MaxEvidenceBytes);

        Assert.Null(BuildService().GetEvidencePath(ProjectName, $"{Generation}/assets/evidence.png"));
    }

    /// <summary>Writes one proposal citing <paramref name="evidence"/>; returns the evidence path.</summary>
    private string WriteProposal(string evidence, string id = "survey-001")
    {
        _checkout.Write($"{ProposalsRel}/{Generation}/{id}.md", $$"""
            ---
            id: "{{id}}"
            generation: "{{Generation}}"
            finding: "Finding"
            evidenceScreenshot: "{{Generation}}/{{evidence}}"
            proposal: "Proposal"
            estimatedEffort: "medium"
            severity: "medium"
            status: "proposed"
            spawnedTask: null
            ---
            """);
        var full = _checkout.Full($"{ProposalsRel}/{Generation}/{evidence}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private string Rel(string full) => Path.GetRelativePath(_checkout.Root, full);

    private ProjectProposalService BuildService()
    {
        var jobs = Path.Combine(_checkout.Parent, "jobs");
        Directory.CreateDirectory(jobs);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Path.Combine(_checkout.Parent, ".task-repository"),
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = jobs,
                ["WatchPaths:0:RootPath"] = _checkout.Root,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(scanner, clients, registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        return new ProjectProposalService(scanner, registry, mutations, NullLogger<ProjectProposalService>.Instance);
    }
}
