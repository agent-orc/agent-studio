using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Prompt text is composed with LF on every host (<c>PromptText.AppendLf</c> /
/// <c>PromptText.NewLine</c> in backend and runner). <c>AppendLine</c>,
/// <c>Environment.NewLine</c> and <c>WriteLine</c> emit CRLF on Windows, so a
/// prompt built with them differs between the Windows merge gate and a Linux
/// runner: the enrichment test that compared an <c>AppendLine</c> block with a
/// literal <c>"\n"</c> held the Windows gate red for two days (AGT-3003).
/// <para>
/// Two rules: every method whose name says it builds a prompt or follow-up
/// (<c>*Prompt*</c>, <c>*FollowUp*</c>), and every registered prompt builder
/// whose name does not say so, must not use a platform newline.
/// </para>
/// </summary>
public sealed partial class PromptLineEndingGuardTests
{
    private static readonly string[] ProductionRoots = ["backend", "runner", "task-server"];

    /// <summary>
    /// Prompt builders whose names do not match the naming rule: the prompt
    /// block helpers they call, and the code that appends to <c>prompt.md</c> or
    /// <c>orchestrator-follow-up.md</c>. Register a new builder here when its
    /// name does not contain "Prompt" or "FollowUp".
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> RegisteredPromptBuilders =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["backend/Features/Runner/PromptEnrichmentService.cs"] = ["RenderLaunchContext"],
            ["backend/Features/Runner/IntakeRunner.cs"] = ["RenderEnrichedContextMarkdown", "RenderConstraintMarkdown"],
            ["backend/Features/Runner/ReissuePromptExperiment.cs"] = ["BuildTreatmentFindings"],
            ["backend/Features/Runner/ReviewDecisionOrchestrator.cs"] = ["BuildBranchDiffSummary", "BuildDiffSummary"],
            ["backend/Features/Runner/OrchestratorChat.cs"] =
            [
                "ResolveCommit", "AppendScopedPreamble", "AppendContextLedger", "AppendResolvedBlocks",
                "RenderContinuity", "AppendProjectStateSnapshot", "AppendCurrentUserPreferences", "AppendNavigationContext",
            ],
            ["backend/Features/Runner/ProjectRunner.cs"] = ["AppendDocSnippet", "BuildReissueFindingsBlock", "WriteWorktreeBlockedGateItem"],
            ["backend/Features/Runner/UiIterationGate.cs"] = ["BuildAgentInstructions"],
            ["backend/Features/Runner/ReviewEvidenceContext.cs"] = ["Render"],
            ["backend/Features/Runner/RequirementAcceptanceScope.cs"] = ["Describe"],
            ["backend/Features/Runner/GlobalOrchestratorBootstrap.cs"] = ["BuildWatchedProjectsBlock", "BuildTaskSnapshotBlock"],
            ["backend/Features/Orchestrator/OrchestratorContextDigestService.cs"] = ["RenderDigest"],
            ["backend/Features/Orchestrator/OrchestratorTaskPromptContextComposer.cs"] = ["Compose"],
            ["backend/Features/Orchestrator/OrchestratorWorkbenchPromptContextComposer.cs"] = ["Compose"],
            ["backend/Features/Drift/AdrCodeDriftAnalysisService.cs"] = ["RenderRefList", "RenderRecentTasks", "RenderReportPointers"],
            ["backend/Features/Drift/DocsMarketingDriftAnalysisService.cs"] =
                ["RenderRefList", "RenderQueueJobs", "RenderRecentTasks", "RenderMarketingDocs", "RenderReportPointers"],
            ["backend/Features/Drift/SoftwareArchitectureDriftAnalysisService.cs"] =
                ["RenderRefList", "RenderRecentTasks", "RenderReportPointers", "RenderArchitectureModel"],
            ["backend/Features/Drift/SpecTaskDriftAnalysisService.cs"] =
                ["RenderRefList", "RenderActiveJobs", "RenderJobRefs", "RenderDuplicates", "RenderReportPointers"],
            ["backend/Features/Runtime/SkillReadinessService.cs"] = ["BuildFixTaskContent"],
            ["backend/Features/ExecutionPreparation/ProjectDefinitionProposalService.cs"] = ["Generate", "AppendList"],
            ["backend/Features/Analysis/RoadmapAlignmentReviewService.cs"] =
                ["RenderQueueSummary", "RenderJobsByLane", "RenderDocList", "RenderRecentReports", "RenderStrayFolders"],
            ["backend/Features/Analysis/SteeringDocsSummaryDriftService.cs"] =
                ["RenderSourceInventory", "RenderInventoryWarnings", "RenderReportPointers", "RenderJobsByLane"],
            ["backend/Features/Analysis/RecurringOutputPatternService.cs"] = ["RenderGroups", "RenderJobs", "RenderRecentReports"],
            ["backend/Features/Tasks/TaskCrudEndpoints.cs"] = ["AppendDeliveryAcceptanceCriteria"],
            ["runner/RemoteRunPrompt.cs"] = ["Build"],
            ["runner/RemoteReviewWorkspace.cs"] = ["AppendReviewMaterial", "RenderReviewMaterial"],
        };

    [Fact]
    public void Prompt_builders_compose_with_lf()
    {
        var violations = ScanRepository()
            .Select(site => $"{site.File}:{site.Line} {site.Method} uses {site.Call}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Prompt text is composed with LF on every host. Use PromptText.AppendLf / PromptText.NewLine "
            + "instead of AppendLine, Environment.NewLine or WriteLine in prompt builders:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Every_registered_prompt_builder_still_exists()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var stale = new List<string>();
        foreach (var (file, names) in RegisteredPromptBuilders)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            var declared = File.Exists(path)
                ? CSharpSourceScanner.Methods(CSharpSourceScanner.MaskCommentsAndLiterals(CSharpSourceScanner.Read(path)))
                    .Select(method => method.Name)
                    .ToHashSet(StringComparer.Ordinal)
                : [];
            stale.AddRange(names.Where(name => !declared.Contains(name)).Select(name => $"{file} {name}"));
        }

        Assert.True(
            stale.Count == 0,
            "Update the prompt-builder registry; these methods no longer exist:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Guard_rejects_platform_newlines_in_a_prompt_builder()
    {
        const string source = """
            internal static class Sample
            {
                public static string BuildReviewPrompt(string body)
                {
                    var text = new StringBuilder();
                    text.AppendLine("## Review");
                    return text.ToString() + $"{Environment.NewLine}{body}";
                }

                private static void WriteFollowUpNote(TextWriter writer) => writer.WriteLine("done");
            }
            """;

        var sites = ScanSource("backend/Sample.cs", source, registered: []);

        Assert.Equal(["AppendLine", "Environment.NewLine", "WriteLine"], sites.Select(site => site.Call));
        Assert.Equal(["BuildReviewPrompt", "BuildReviewPrompt", "WriteFollowUpNote"], sites.Select(site => site.Method));
    }

    [Fact]
    public void Guard_checks_registered_builders_and_ignores_other_methods_and_literals()
    {
        const string source = """
            internal static class Sample
            {
                private static string RenderDigest()
                    => new StringBuilder().AppendLine("digest").ToString();

                private static string RenderReport()
                    => new StringBuilder().AppendLine("report").ToString();

                public static string BuildPrompt()
                    => new StringBuilder()
                        .AppendLf("Never call AppendLine( or Environment.NewLine in a prompt.") // AppendLine
                        .ToString();
            }
            """;

        var sites = ScanSource("backend/Sample.cs", source, registered: ["RenderDigest"]);

        var site = Assert.Single(sites);
        Assert.Equal(("RenderDigest", "AppendLine"), (site.Method, site.Call));
    }

    private sealed record NewlineSite(string File, int Line, string Method, string Call);

    private static IReadOnlyList<NewlineSite> ScanRepository()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var sites = new List<NewlineSite>();
        foreach (var productionRoot in ProductionRoots)
        foreach (var file in CSharpSourceScanner.SourceFiles(root, productionRoot))
        {
            var relative = CSharpSourceScanner.Relative(root, file);
            var registered = RegisteredPromptBuilders.TryGetValue(relative, out var names) ? names : [];
            sites.AddRange(ScanSource(relative, CSharpSourceScanner.Read(file), registered));
        }
        return sites;
    }

    private static IReadOnlyList<NewlineSite> ScanSource(string relativePath, string source, IReadOnlyCollection<string> registered)
    {
        var code = CSharpSourceScanner.MaskCommentsAndLiterals(source.Replace("\r\n", "\n", StringComparison.Ordinal));
        var sites = new List<NewlineSite>();
        foreach (var method in CSharpSourceScanner.Methods(code))
        {
            if (!PromptBuilderName().IsMatch(method.Name) && !registered.Contains(method.Name)) continue;
            foreach (Match call in PlatformNewline().Matches(code[method.Start..method.End]))
            {
                sites.Add(new NewlineSite(
                    relativePath,
                    CSharpSourceScanner.LineAt(code, method.Start + call.Index),
                    method.Name,
                    Regex.Replace(call.Groups["call"].Value, @"\s+", "")));
            }
        }
        return sites
            .DistinctBy(site => (site.Line, site.Call))
            .OrderBy(site => site.Line)
            .ToList();
    }

    [GeneratedRegex("Prompt|FollowUp")]
    private static partial Regex PromptBuilderName();

    [GeneratedRegex(@"\b(?<call>AppendLine|Environment\s*\.\s*NewLine|WriteLine(?:Async)?)\b")]
    private static partial Regex PlatformNewline();
}
