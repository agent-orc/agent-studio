using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F5 of the playthrough: the static-web template ships schema 1 and a commented
/// schema 2 deployment section that, uncommented, is a valid deployable definition.
/// The container-service template ships schema 2 itself (M5).
/// </summary>
public sealed class ProjectTemplateDeploymentSectionTests
{
    private const string Marker = "# --- schema 2 deployment ---";
    private static readonly string TemplateRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../project-templates"));

    [Theory]
    [InlineData("static-web", "static-site")]
    public void Uncommented_section_is_valid_schema_two_with_both_environments(string template, string step)
    {
        var yaml = File.ReadAllText(Path.Combine(TemplateRoot, template, ".agent-studio", "project.yml"));
        Assert.True(ProjectDefinitionReader.Parse(yaml).IsValid);
        var marker = yaml.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(marker > 0, $"{template} has no commented schema 2 deployment section.");

        var head = yaml[..marker].Replace("schemaVersion: 1", "schemaVersion: 2", StringComparison.Ordinal);
        var section = yaml[(marker + Marker.Length)..].Split('\n')
            .Select(line => line.StartsWith("# ", StringComparison.Ordinal) ? line[2..] : line.TrimStart('#'));
        var uncommented = head + string.Join('\n', section);

        var parsed = ProjectDefinitionReader.Parse(uncommented);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        Assert.All(parsed.Definition!.Deployment!.Deploy, item => Assert.Equal(step, item.Step));
        Assert.Equal(["stage", "production"],
            ProjectEnvironmentService.ApplyDefinition([], uncommented).Select(file => file.Declaration!.Id));
    }

    [Fact]
    public void Container_template_is_schema_two_with_both_environments_and_container_steps()
    {
        var yaml = File.ReadAllText(Path.Combine(TemplateRoot, "container-service", ".agent-studio", "project.yml"));
        var parsed = ProjectDefinitionReader.Parse(yaml);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        Assert.Equal(2, parsed.Definition!.SchemaVersion);
        Assert.All(parsed.Definition.Deployment!.Deploy, item => Assert.Equal("container", item.Step));
        Assert.Equal(["stage", "production"],
            ProjectEnvironmentService.ApplyDefinition([], yaml).Select(file => file.Declaration!.Id));
    }
}
