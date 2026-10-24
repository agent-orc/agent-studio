using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>Repository facts the schema 2 proposal needs beyond project.yml itself.</summary>
/// <param name="StaticBundlePath">An existing bundle directory such as <c>dist</c>, or null when none was found.</param>
public sealed record PipelineMigrationFacts(
    string ProjectId,
    IReadOnlyList<EnvironmentDeclaration> Environments,
    bool HasDockerfile,
    string? StaticBundlePath);

public sealed record PipelineMigrationProposal(
    bool Available,
    string? Reason,
    string? ProposedYaml,
    string? ProposalSha256,
    IReadOnlyList<string> Notes);

/// <summary>
/// Proposes the schema 2 form of a valid schema 1 project.yml. The proposal is
/// text the operator reviews: the schema 1 content stays byte-identical apart
/// from the <c>schemaVersion</c> line, new sections are appended with the file's
/// own line ending, and every default the policy had to choose is named in
/// <see cref="PipelineMigrationProposal.Notes"/>. Pure: no filesystem, clock, or git.
/// </summary>
public static partial class PipelineMigrationPolicy
{
    public const int DefaultHealthTimeoutSeconds = 120;
    public const string DefaultStaticBundlePath = "dist";

    public static PipelineMigrationProposal Propose(
        string? currentYaml, ProjectDefinitionReadResult current, PipelineMigrationFacts facts)
    {
        if (currentYaml is null)
            return Unavailable("definition-missing", "The project has no .agent-studio/project.yml to migrate.");
        if (current.Parsed is { SchemaVersion: 2 })
            return Unavailable("already-schema-2", "project.yml already uses schema 2.");
        if (!current.IsValid || current.Definition is not { SchemaVersion: 1 })
            return Unavailable("current-definition-invalid", "Fix the schema 1 issues before migrating.");
        if (!SchemaVersionLine().IsMatch(currentYaml))
            return Unavailable("schema-version-line-missing", "schemaVersion: 1 must be a top-level line of its own.");

        var notes = new List<string>();
        var ending = currentYaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var yaml = new StringBuilder(SchemaVersionLine().Replace(currentYaml, "schemaVersion: 2", 1).TrimEnd('\r', '\n'));
        void Line(string text) => yaml.Append(text).Append(ending);
        yaml.Append(ending);
        Line(string.Empty);
        Line("# Schema 2 deployment definition (proposed by Agent Studio from schema 1).");
        Line("project:");
        Line($"  id: {facts.ProjectId}");

        var targets = Targets(facts.Environments, notes);
        var artifacts = Artifacts(targets, facts, notes);
        if (artifacts.Count > 0)
        {
            Line("artifacts:");
            foreach (var artifact in artifacts)
            {
                Line($"  - name: {artifact.Name}");
                Line($"    type: {artifact.Type}");
                Line(artifact.Type == "static" ? $"    path: {artifact.Source}" : $"    dockerfile: {artifact.Source}");
            }
        }

        Line("environments:");
        Line("  - name: staging");
        Line("    protected: false");
        Line("  - name: production");
        Line("    protected: true");
        Line("lifecycles:");
        Line("  standard:");
        Line("    - phase: staging");
        Line("      automatic: true");
        Line("    - phase: production");
        Line("      approval: [Owner, Maintainer]");
        Line("      requires: staging");

        if (targets.Count > 0)
        {
            Line("deploy:");
            foreach (var target in targets)
                AppendStep(Line, target, artifacts.First(artifact => artifact.Type == target.Declaration.Template), facts.ProjectId, notes);
        }
        else
        {
            notes.Add("No Staging or Production declaration exists under docs/deployments/environments, so the proposal "
                      + "has the default environments and lifecycle but no artifacts or deploy steps yet.");
        }

        var variables = Variables(targets);
        if (variables.Count > 0)
        {
            Line("variables:");
            foreach (var variable in variables)
            {
                Line($"  - name: {variable.Name}");
                Line($"    secret: {variable.Secret}");
                if (variable.Environments.Count < ProjectDeploymentVocabulary.EnvironmentNames.Count)
                    Line($"    environments: [{string.Join(", ", variable.Environments)}]");
            }
        }

        var proposed = yaml.ToString();
        return new(true, null, proposed, Sha256(proposed), notes);
    }

    public static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Project ids in project.yml are lower-case identifiers derived from the display name.</summary>
    public static string ProjectIdFor(string projectName)
    {
        var slug = NonIdentifier().Replace(projectName.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "project" : slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    private static List<DeployTarget> Targets(IReadOnlyList<EnvironmentDeclaration> declarations, List<string> notes)
    {
        var targets = new List<DeployTarget>();
        foreach (var declaration in declarations)
        {
            var environment = EnvironmentFor(declaration.Kind);
            if (environment is null)
            {
                notes.Add($"Environment declaration '{declaration.Id}' has kind '{declaration.Kind}'; P1 deploys only Staging and Production, so it is not migrated.");
                continue;
            }
            targets.Add(new(environment, declaration));
        }
        return targets.OrderBy(target => target.Environment == "staging" ? 0 : 1)
            .ThenBy(target => target.Declaration.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The P1 environment an AGT-3176 declaration kind deploys, or null for kinds P1 does not deploy.</summary>
    public static string? EnvironmentFor(string kind) => kind switch
    {
        "stage" => "staging",
        "production" => "production",
        _ => null,
    };

    private static List<ProposedArtifact> Artifacts(List<DeployTarget> targets, PipelineMigrationFacts facts, List<string> notes)
    {
        var artifacts = new List<ProposedArtifact>();
        if (targets.Any(target => target.Declaration.Template == "static"))
        {
            var path = facts.StaticBundlePath ?? DefaultStaticBundlePath;
            if (facts.StaticBundlePath is null)
                notes.Add($"No built bundle directory was found; the static artifact uses '{DefaultStaticBundlePath}'. Change it to the directory your build writes.");
            artifacts.Add(new("site", "static", path));
        }
        if (targets.Any(target => target.Declaration.Template == "container"))
        {
            if (!facts.HasDockerfile)
                notes.Add("No Dockerfile exists at the repository root; the container artifact names 'Dockerfile'. Add it or change the path.");
            artifacts.Add(new("app", "container", "Dockerfile"));
        }
        return artifacts;
    }

    private static void AppendStep(Action<string> line, DeployTarget target, ProposedArtifact artifact,
        string projectId, List<string> notes)
    {
        var declaration = target.Declaration;
        line(declaration.Template == "static" ? "  - step: static-site" : "  - step: container");
        line($"    artifact: {artifact.Name}");
        line($"    target: {declaration.TargetHostId}");
        if (declaration.Template == "static")
        {
            line($"    directory: {JsonSerializer.Serialize(declaration.Destination)}");
            if (!declaration.Destination.StartsWith("/srv/", StringComparison.Ordinal))
                notes.Add($"'{declaration.Id}' deploys to '{declaration.Destination}' outside /srv; change the destination to a path below /srv before confirming.");
        }
        else
        {
            var compose = $"/srv/agent-studio/{projectId}/{target.Environment}/compose.yml";
            line($"    composeFile: {compose}");
            line($"    service: {projectId}");
            notes.Add($"'{declaration.Id}' declares proxy port {declaration.Destination} but no compose file; the proposal uses {compose} with service '{projectId}'.");
        }
        line($"    healthUrl: {declaration.HealthUrl}");
        line($"    healthTimeoutSeconds: {DefaultHealthTimeoutSeconds}");
        line($"    environments: [{target.Environment}]");
    }

    private static List<ProposedVariable> Variables(List<DeployTarget> targets)
        => targets
            .SelectMany(target => target.Declaration.SecretRefs.Select(secret => (secret, target.Environment)))
            .GroupBy(item => item.secret, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ProposedVariable(
                VariableName(group.Key), group.Key,
                group.Select(item => item.Environment).Distinct(StringComparer.Ordinal)
                    .OrderBy(environment => environment == "staging" ? 0 : 1).ToArray()))
            .ToList();

    private static string VariableName(string secret)
    {
        var name = NonVariable().Replace(secret, "_").ToUpperInvariant();
        return char.IsDigit(name[0]) ? "_" + name : name;
    }

    private static PipelineMigrationProposal Unavailable(string reason, string note)
        => new(false, reason, null, null, [note]);

    [GeneratedRegex(@"(?m)^schemaVersion:[ ]*1[ ]*(?=\r?$)", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaVersionLine();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifier();

    [GeneratedRegex("[^A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex NonVariable();

    private sealed record DeployTarget(string Environment, EnvironmentDeclaration Declaration);
    private sealed record ProposedArtifact(string Name, string Type, string Source);
    private sealed record ProposedVariable(string Name, string Secret, IReadOnlyList<string> Environments);
}
