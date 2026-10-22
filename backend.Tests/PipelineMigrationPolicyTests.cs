using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The schema 1 to schema 2 proposal and the diagram projection are pure:
/// text and declarations in, proposal or diagram out.
/// </summary>
public sealed class PipelineMigrationPolicyTests
{
    private const string SchemaOne = """
        schemaVersion: 1
        stack: [node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build: [npm run build]
          test: [npm test]
          lint: []
        testSuites:
          - id: unit
            category: routine
            expectedDurationSeconds: 60
            command: npm test
        cachePaths: []
        capabilities: [git]
        environment:
          CI: "true"
        """;

    private static readonly EnvironmentDeclaration Staging = new(
        "staging", "Staging", "stage", "web-vm", "https://agent-orchestrator.dev/_staging/studio/", "static",
        "/srv/www/_staging/studio", "https://agent-orchestrator.dev/_staging/studio/", "person", ["MAIL_KEY"], 8080);

    private static readonly EnvironmentDeclaration Production = new(
        "production", "Production", "production", "web-vm", "https://agent-orchestrator.dev/", "static",
        "/srv/www/studio", "https://agent-orchestrator.dev/", "person", ["MAIL_KEY", "db.url"], 8080);

    [Fact]
    public void Schema_one_without_declarations_gets_default_environments_and_lifecycle()
    {
        var proposal = Propose(SchemaOne, []);

        Assert.True(proposal.Available);
        var parsed = ProjectDefinitionReader.Parse(proposal.ProposedYaml!);
        Assert.True(parsed.IsValid, Describe(parsed));
        var deployment = parsed.Definition!.Deployment!;
        Assert.Equal(2, parsed.Definition.SchemaVersion);
        Assert.Equal("website", parsed.Definition.Project!.Id);
        Assert.Equal([("staging", false), ("production", true)],
            deployment.Environments.Select(environment => (environment.Name, environment.Protected)));
        Assert.Empty(deployment.Deploy);
        Assert.Contains(proposal.Notes, note => note.Contains("docs/deployments/environments", StringComparison.Ordinal));
    }

    [Fact]
    public void Declarations_become_static_site_steps_and_secret_variables()
    {
        var proposal = Propose(SchemaOne, [Production, Staging], staticBundle: "public");

        var parsed = ProjectDefinitionReader.Parse(proposal.ProposedYaml!);
        Assert.True(parsed.IsValid, Describe(parsed));
        var deployment = parsed.Definition!.Deployment!;
        var artifact = Assert.Single(deployment.Artifacts);
        Assert.Equal(("site", "static", "public"), (artifact.Name, artifact.Type, artifact.Path));
        Assert.Equal(["staging", "production"], deployment.Deploy.Select(step => Assert.Single(step.Environments)));
        Assert.All(deployment.Deploy, step =>
        {
            Assert.Equal(("static-site", "web-vm", PipelineMigrationPolicy.DefaultHealthTimeoutSeconds),
                (step.Step, step.Target, step.HealthTimeoutSeconds));
        });
        Assert.Equal("/srv/www/_staging/studio", deployment.Deploy[0].Directory);
        Assert.Equal("https://agent-orchestrator.dev/", deployment.Deploy[1].HealthUrl);
        Assert.Equal([("MAIL_KEY", "MAIL_KEY", 0), ("DB_URL", "db.url", 1)],
            deployment.Variables.Select(variable => (variable.Name, variable.Secret!, variable.Environments.Count)));
        Assert.DoesNotContain(proposal.Notes, note => note.Contains("dist", StringComparison.Ordinal));
    }

    [Fact]
    public void Migrated_schema_two_declarations_execute_project_yml_and_refuse_divergence()
    {
        var yaml = Propose(SchemaOne, [Staging, Production]).ProposedYaml!;
        var files = new[]
        {
            new EnvironmentDeclarationFile(Staging.Id, Staging, null),
            new EnvironmentDeclarationFile(Production.Id, Production, null),
        };

        var effective = ProjectEnvironmentService.ApplyDefinition(files, yaml);

        Assert.All(effective, file => Assert.Null(file.Error));
        Assert.Equal("static-site", Assert.Single(effective[0].Declaration!.SshSteps).Step);
        Assert.Equal(["MAIL_KEY"], effective[0].Declaration!.Variables.Select(item => item.Name));
        Assert.False(effective[0].Declaration!.Protected);
        Assert.True(effective[1].Declaration!.Protected);
        Assert.Equal("staging", effective[1].Declaration!.Requires);

        var changed = ProjectEnvironmentService.ApplyDefinition(
            [files[0] with { Declaration = Staging with { Destination = "/srv/www/other" } }, files[1]], yaml);
        var brokenStage = Assert.Single(changed, item => item.EnvironmentId == Staging.Id);
        Assert.Contains("diverges", brokenStage.Error);
        Assert.Null(brokenStage.Declaration);
    }

    [Fact]
    public void Container_declaration_gets_a_compose_default_and_names_every_assumption()
    {
        var container = Production with { Template = "container", Destination = "18080" };

        var proposal = Propose(SchemaOne, [container], hasDockerfile: false);

        var parsed = ProjectDefinitionReader.Parse(proposal.ProposedYaml!);
        Assert.True(parsed.IsValid, Describe(parsed));
        var step = Assert.Single(parsed.Definition!.Deployment!.Deploy);
        Assert.Equal(("container", "/srv/agent-studio/website/production/compose.yml", "website"),
            (step.Step, step.ComposeFile, step.Service));
        Assert.Contains(proposal.Notes, note => note.Contains("18080", StringComparison.Ordinal));
        Assert.Contains(proposal.Notes, note => note.Contains("Dockerfile", StringComparison.Ordinal));
    }

    [Fact]
    public void Other_kinds_are_skipped_with_a_note()
    {
        var preview = Staging with { Id = "preview", Kind = "other" };

        var proposal = Propose(SchemaOne, [preview]);

        Assert.Empty(ProjectDefinitionReader.Parse(proposal.ProposedYaml!).Definition!.Deployment!.Deploy);
        Assert.Contains(proposal.Notes, note => note.Contains("'preview'", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_one_text_is_kept_and_crlf_stays_crlf()
    {
        var crlf = SchemaOne.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

        var proposal = Propose(crlf, [Staging, Production]);

        var proposed = proposal.ProposedYaml!;
        Assert.Equal(proposed.Split("\r\n").Length - 1, proposed.Split('\n').Length - 1);
        Assert.StartsWith(crlf.Replace("schemaVersion: 1", "schemaVersion: 2", StringComparison.Ordinal).TrimEnd(), proposed,
            StringComparison.Ordinal);
        Assert.True(ProjectDefinitionReader.Parse(proposed).IsValid);
    }

    [Fact]
    public void Proposal_digest_is_deterministic_and_follows_the_input()
    {
        var first = Propose(SchemaOne, [Staging]);
        var second = Propose(SchemaOne, [Staging]);
        var changed = Propose(SchemaOne, [Staging, Production]);

        Assert.Equal(first.ProposalSha256, second.ProposalSha256);
        Assert.Equal(PipelineMigrationPolicy.Sha256(first.ProposedYaml!), first.ProposalSha256);
        Assert.NotEqual(first.ProposalSha256, changed.ProposalSha256);
    }

    [Theory]
    [InlineData(null, "definition-missing")]
    [InlineData("schemaVersion: 2\nstack: [node]\n", "already-schema-2")]
    [InlineData("schemaVersion: 1\nstack: []\n", "current-definition-invalid")]
    public void Unavailable_cases_name_their_reason(string? yaml, string reason)
    {
        var read = yaml is null
            ? new ProjectDefinitionReadResult(null, [new(ProjectPreparationPaths.Definition, "definition-missing", "missing")], null)
            : ProjectDefinitionReader.Parse(yaml);

        var proposal = PipelineMigrationPolicy.Propose(yaml, read, Facts([]));

        Assert.False(proposal.Available);
        Assert.Equal(reason, proposal.Reason);
        Assert.Null(proposal.ProposedYaml);
        Assert.NotEmpty(proposal.Notes);
    }

    [Theory]
    [InlineData("Agent Studio Website", "agent-studio-website")]
    [InlineData("  QS/2 ", "qs-2")]
    [InlineData("***", "project")]
    public void Project_id_is_a_lower_case_identifier(string name, string id)
        => Assert.Equal(id, PipelineMigrationPolicy.ProjectIdFor(name));

    [Fact]
    public void Diagram_runs_from_gate_through_artifacts_to_each_environment()
    {
        var proposal = Propose(SchemaOne, [Staging, Production]);
        var definition = ProjectDefinitionReader.Parse(proposal.ProposedYaml!).Definition!;

        var diagram = PipelineDiagramProjection.Project(definition);

        Assert.Equal(("commands", "npm run build", "npm test", "unit"),
            (diagram.Gate.Source, Assert.Single(diagram.Gate.Build), Assert.Single(diagram.Gate.Test), Assert.Single(diagram.Gate.TestSuites)));
        Assert.Equal("site", Assert.Single(diagram.Artifacts).Name);
        Assert.Equal(["staging", "production"], diagram.Environments.Select(environment => environment.Name));
        var staging = diagram.Environments[0];
        var production = diagram.Environments[1];
        Assert.True(staging.Automatic);
        Assert.False(staging.Protected);
        Assert.True(production.Protected);
        Assert.Equal(["Owner", "Maintainer"], production.Approval);
        Assert.Equal("staging", production.Requires);
        Assert.Equal(("deploy[0]", "static-site", "/srv/www/_staging/studio"),
            (Assert.Single(staging.Steps).Path, staging.Steps[0].Kind, staging.Steps[0].Destination));
        Assert.Equal(["MAIL_KEY", "DB_URL"], production.Variables.Select(variable => variable.Name));
        Assert.All(production.Variables, variable => Assert.True(variable.IsSecret));
        Assert.Equal(["MAIL_KEY"], staging.Variables.Select(variable => variable.Name));
    }

    [Fact]
    public void Diagram_of_schema_one_is_the_gate_alone()
    {
        var diagram = PipelineDiagramProjection.Project(ProjectDefinitionReader.Parse(SchemaOne).Definition!);

        Assert.Equal(1, diagram.SchemaVersion);
        Assert.Empty(diagram.Artifacts);
        Assert.Empty(diagram.Environments);
    }

    private static PipelineMigrationProposal Propose(
        string yaml, IReadOnlyList<EnvironmentDeclaration> declarations, bool hasDockerfile = true, string? staticBundle = null)
        => PipelineMigrationPolicy.Propose(yaml, ProjectDefinitionReader.Parse(yaml), Facts(declarations, hasDockerfile, staticBundle));

    private static PipelineMigrationFacts Facts(
        IReadOnlyList<EnvironmentDeclaration> declarations, bool hasDockerfile = true, string? staticBundle = null)
        => new("website", declarations, hasDockerfile, staticBundle);

    private static string Describe(ProjectDefinitionReadResult result)
        => string.Join("; ", result.Issues.Select(issue => $"{issue.Path} {issue.Code} {issue.Message}"));
}
