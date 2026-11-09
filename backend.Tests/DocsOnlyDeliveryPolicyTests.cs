using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The AGT-2417 docs rule classifier: docs-only deliveries (documentation and
/// task evidence, no product code) take the light release gate. Unknown or
/// empty diffs fail closed to the strict path.
/// </summary>
public sealed class DocsOnlyDeliveryPolicyTests
{
    [Theory]
    [InlineData("docs/operations/research-deliverables/index.html")]
    [InlineData("docs/research/report.html")]
    [InlineData("README.md")]
    [InlineData("backend/Features/Pipeline/NOTES.md")]
    [InlineData("results/AGT-2442/report.html")]
    [InlineData("attachments/screenshot.png")]
    public void DocsPaths_AreDocsOnly(string path)
        => Assert.True(DocsOnlyDeliveryPolicy.IsDocsOnly([path]));

    [Theory]
    [InlineData("backend/Features/Pipeline/MergeIntoDevelopRunner.cs")]
    [InlineData("prompts/runtime/mode-framing-research.md.meta.json")]
    [InlineData("frontend/src/app/app.component.ts")]
    [InlineData("scripts/deploy.sh")]
    [InlineData("appsettings.json")]
    public void CodeAndConfigPaths_AreNotDocsOnly(string path)
        => Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly([path]));

    // AGT-3414: a documentation-only delivery skips every test command, so a
    // Markdown or documentation path that feeds code, a test, a build, a
    // template or the agent configuration is product behaviour.
    [Theory]
    [InlineData(".agent-studio/project.yml")]
    [InlineData(".agent-studio/notes.md")]
    [InlineData(".github/ISSUE_TEMPLATE/bug.md")]
    [InlineData(".agents/skills/task-api/SKILL.md")]
    [InlineData("prompts/runtime/mode-framing-research.md")]
    [InlineData("project-templates/static-web/docs/deployments/README.md")]
    [InlineData("backend/Templates/welcome.md")]
    [InlineData("backend.Tests/Fixtures/summary-assessment/AGT-2798/status.md")]
    [InlineData("frontend/e2e/README.md")]
    [InlineData("frontend/src/app/concept-docs/orchestrator.md")]
    [InlineData("testdata/cli-fixtures/README.md")]
    [InlineData("scripts/supervisor/system-review.md")]
    [InlineData("agent-rules/core.md")]
    [InlineData("docs/deployment-structure-view/src/main.ts")]
    [InlineData("docs/tools/render.mjs")]
    [InlineData("docs/tools/package.json")]
    [InlineData("docs/compose/stack.yaml")]
    [InlineData("results/AGT-1/rerun.sh")]
    [InlineData("Directory.Build.props")]
    [InlineData("Dockerfile")]
    public void CodeTestBuildTemplateAndAgentPaths_AreNotDocsOnly(string path)
        => Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly([path]));

    [Theory]
    [InlineData("docs/concepts/batch-gate.html")]
    [InlineData("docs/operations/testing/drill.md")]
    [InlineData("docs/quality/report/raw/run.jsonl")]
    [InlineData("CHANGELOG.md")]
    public void PlainDocumentation_StaysDocsOnly(string path)
        => Assert.True(DocsOnlyDeliveryPolicy.IsDocsOnly([path]));

    [Fact]
    public void MixedDiff_IsNotDocsOnly()
        => Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly(
            ["docs/note.md", "backend/Program.cs"]));

    [Theory]
    [InlineData("backend.Tests/DocsOnlyDeliveryPolicyTests.cs")]
    [InlineData("backend/OrchestratorApi.csproj")]
    [InlineData("prompts/runtime/review.md")]
    [InlineData(".agent-studio/prepare")]
    public void DocumentationWithOneProductPath_IsNotDocsOnly(string product)
        => Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly(
            ["docs/concepts/batch-gate.html", "README.md", product]));

    [Fact]
    public void UnknownOrEmptyDiff_FailsClosed()
    {
        Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly(null));
        Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly([]));
    }
}
