using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F5 of the playthrough: each project template ships schema 1 and a commented
/// schema 2 deployment section that, uncommented, is a valid deployable definition.
/// </summary>
public sealed class ProjectTemplateDeploymentSectionTests
{
    private const string Marker = "# --- schema 2 deployment ---";
    private static readonly string TemplateRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../project-templates"));

    [Theory]
    [InlineData("static-web", "static-site")]
    [InlineData("container-service", "container")]
    public void Uncommented_section_is_valid_schema_two_with_both_environments(string template, string step)
    {
        var uncommented = Uncommented(template);

        var parsed = ProjectDefinitionReader.Parse(uncommented);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        Assert.All(parsed.Definition!.Deployment!.Deploy, item => Assert.Equal(step, item.Step));
        Assert.Equal(["stage", "production"],
            ProjectEnvironmentService.ApplyDefinition([], uncommented).Select(file => file.Declaration!.Id));
    }

    [Fact]
    public void Container_service_ships_a_script_for_every_runbook_step_and_documents_the_install()
    {
        // M4: runbook steps need operator-installed scripts, and the template shipped none.
        var uncommented = Uncommented("container-service");
        var runbooks = ProjectDefinitionReader.Parse(uncommented).Definition!.Deployment!.Runbooks;
        Assert.Equal(["restart", "disk-cleanup"], runbooks.Select(runbook => runbook.Name));
        var scripts = Path.Combine(TemplateRoot, "container-service", "deploy", "runbooks");
        foreach (var step in runbooks.SelectMany(runbook => runbook.Steps))
        {
            var script = Path.Combine(scripts, step + ".sh");
            Assert.True(File.Exists(script), $"No script for runbook step {step}.");
            if (OperatingSystem.IsWindows()) continue;
            using var syntax = System.Diagnostics.Process.Start("sh", ["-n", script])!;
            syntax.WaitForExit();
            Assert.Equal(0, syntax.ExitCode);
        }
        var environments = ProjectEnvironmentService.ApplyDefinition([], uncommented).Select(file => file.Declaration!).ToArray();
        foreach (var runbook in runbooks)
        {
            var verification = ProjectRunbookVerification.Parse(runbook.Verify);
            Assert.Equal(ProjectRunbookVerification.HealthCheck, verification?.Kind);
            Assert.All(environments, environment => Assert.Contains(HealthCheckDeclarations.Effective(environment),
                check => check.Id == verification!.Target));
        }
        var readme = File.ReadAllText(Path.Combine(TemplateRoot, "container-service", "docs", "deployments", "README.md"));
        Assert.Contains("/etc/agent-host/runbooks/<project>/<step>.sh", readme);
        Assert.Contains("install -m 0755 deploy/runbooks/restart.sh deploy/runbooks/disk-cleanup.sh", readme);
    }

    private static string Uncommented(string template)
    {
        var yaml = File.ReadAllText(Path.Combine(TemplateRoot, template, ".agent-studio", "project.yml"));
        Assert.True(ProjectDefinitionReader.Parse(yaml).IsValid);
        var marker = yaml.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(marker > 0, $"{template} has no commented schema 2 deployment section.");
        var head = yaml[..marker].Replace("schemaVersion: 1", "schemaVersion: 2", StringComparison.Ordinal);
        var section = yaml[(marker + Marker.Length)..].Split('\n')
            .Select(line => line.StartsWith("# ", StringComparison.Ordinal) ? line[2..] : line.TrimStart('#'));
        return head + string.Join('\n', section);
    }
}
