using System.Text;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>Response of <c>GET /api/projects/{projectName}/pipeline-definition</c> and of the validate route.</summary>
public sealed record PipelineDefinitionResponse(
    string Project,
    string Path,
    bool Exists,
    int? SchemaVersion,
    bool Valid,
    IReadOnlyList<ProjectDefinitionIssue> Issues,
    string? DefinitionSha256,
    PipelineDiagram? Diagram,
    bool MigrationAvailable);

/// <summary>Dry run of the schema 1 to schema 2 migration. Nothing is written.</summary>
public sealed record PipelineMigrationResponse(
    string Project,
    bool Available,
    string? Reason,
    string? BaseSha256,
    string? ProposedYaml,
    string? ProposalSha256,
    bool Valid,
    IReadOnlyList<ProjectDefinitionIssue> Issues,
    IReadOnlyList<string> Notes,
    PipelineDiagram? Diagram);

public sealed record ValidatePipelineDefinitionRequest(string? Yaml);

/// <summary>The operator confirms exactly the dry run they saw: both digests must still match.</summary>
public sealed record ConfirmPipelineMigrationRequest(string? BaseSha256, string? ProposalSha256);

public sealed record PipelineMigrationConfirmResponse(
    bool Changed,
    string? CommitSha,
    bool PushQueued,
    PipelineDefinitionResponse Definition);

public sealed record PipelineDefinitionResult<T>(T? Value, string? ErrorCode, string? Error)
{
    public static PipelineDefinitionResult<T> Ok(T value) => new(value, null, null);
    public static PipelineDefinitionResult<T> Fail(string code, string error) => new(default, code, error);
}

/// <summary>
/// Reads, validates, and migrates the repository-owned project.yml for the
/// settings page. Reading and validation never write. The migration writes
/// only on confirmation, only when the file and the regenerated proposal still
/// match the dry run, and only through <see cref="ManagedRepositoryMutationService"/>
/// so the write and its path-scoped commit share one repository gate.
/// </summary>
public sealed class ProjectPipelineDefinitionService
{
    public const int MaxDefinitionBytes = 256 * 1024;
    private static readonly string[] BundleCandidates = ["dist", "build", "public", "_site", "site", "out"];

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly ProjectEnvironmentService _environments;
    private readonly ManagedRepositoryMutationService _mutations;
    private readonly ILogger<ProjectPipelineDefinitionService> _logger;

    public ProjectPipelineDefinitionService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        ProjectEnvironmentService environments,
        ManagedRepositoryMutationService mutations,
        ILogger<ProjectPipelineDefinitionService> logger)
    {
        _scanner = scanner;
        _registry = registry;
        _environments = environments;
        _mutations = mutations;
        _logger = logger;
    }

    public PipelineDefinitionResult<PipelineDefinitionResponse> Read(string projectName)
    {
        if (Repository(projectName) is not { } root)
            return PipelineDefinitionResult<PipelineDefinitionResponse>.Fail("not-found", $"Unknown project '{projectName}'.");
        var (text, read) = ReadCurrent(root);
        return PipelineDefinitionResult<PipelineDefinitionResponse>.Ok(View(projectName, text is not null, read));
    }

    public PipelineDefinitionResult<PipelineDefinitionResponse> Validate(string projectName, ValidatePipelineDefinitionRequest? request)
    {
        var yaml = request?.Yaml;
        if (string.IsNullOrWhiteSpace(yaml))
            return PipelineDefinitionResult<PipelineDefinitionResponse>.Fail("validation", "yaml is required.");
        if (Encoding.UTF8.GetByteCount(yaml) > MaxDefinitionBytes)
            return PipelineDefinitionResult<PipelineDefinitionResponse>.Fail("validation", $"yaml exceeds {MaxDefinitionBytes} bytes.");
        if (Repository(projectName) is not { } root)
            return PipelineDefinitionResult<PipelineDefinitionResponse>.Fail("not-found", $"Unknown project '{projectName}'.");
        return PipelineDefinitionResult<PipelineDefinitionResponse>.Ok(
            View(projectName, true, ProjectDefinitionReader.Parse(yaml, root)));
    }

    public PipelineDefinitionResult<PipelineMigrationResponse> DryRun(string projectName)
    {
        if (Repository(projectName) is not { } root)
            return PipelineDefinitionResult<PipelineMigrationResponse>.Fail("not-found", $"Unknown project '{projectName}'.");
        var (read, proposal) = Propose(projectName, root);
        return PipelineDefinitionResult<PipelineMigrationResponse>.Ok(MigrationView(projectName, root, read, proposal));
    }

    public PipelineDefinitionResult<PipelineMigrationConfirmResponse> Confirm(
        string projectName, ConfirmPipelineMigrationRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.BaseSha256) || string.IsNullOrWhiteSpace(request.ProposalSha256))
            return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Fail(
                "validation", "baseSha256 and proposalSha256 from the dry run are required.");
        if (Repository(projectName) is not { } root)
            return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Fail("not-found", $"Unknown project '{projectName}'.");

        var (read, proposal) = Propose(projectName, root);
        if (!proposal.Available || proposal.ProposedYaml is null)
            return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Fail(
                "not-available", proposal.Notes.FirstOrDefault() ?? "No migration is available.");
        if (!string.Equals(read.DefinitionSha256, request.BaseSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(proposal.ProposalSha256, request.ProposalSha256, StringComparison.OrdinalIgnoreCase))
            return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Fail(
                "stale", "project.yml or the environment declarations changed since the dry run. Review the new proposal.");
        var proposed = ProjectDefinitionReader.Parse(proposal.ProposedYaml, root);
        if (!proposed.IsValid)
            return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Fail(
                "proposal-invalid", "The proposal does not validate; fix the reported issues in the repository first.");

        var fullPath = DefinitionPath(root);
        var bom = HasUtf8Bom(fullPath);
        var mutation = _mutations.Execute(
            projectName,
            root,
            "project-definition-schema-2",
            "chore(project): migrate project.yml to schema 2",
            [ProjectPreparationPaths.Definition],
            () => File.WriteAllText(fullPath, proposal.ProposedYaml, new UTF8Encoding(bom)));
        if (!mutation.Success)
            return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Fail(
                "write-failed", $"project.yml could not be written: {mutation.Error}");

        _logger.LogInformation(
            "project-definition-migrated project={Project} from={Base} to={Proposal} changed={Changed} sha={Sha} pushQueued={PushQueued}",
            projectName, request.BaseSha256, proposal.ProposalSha256, mutation.Changed, mutation.CommitSha, mutation.PushQueued);
        var (_, after) = ReadCurrent(root);
        return PipelineDefinitionResult<PipelineMigrationConfirmResponse>.Ok(new(
            mutation.Changed, mutation.CommitSha, mutation.PushQueued, View(projectName, true, after)));
    }

    private (ProjectDefinitionReadResult Read, PipelineMigrationProposal Proposal) Propose(
        string projectName, string root)
    {
        var (text, read) = ReadCurrent(root);
        var facts = new PipelineMigrationFacts(
            PipelineMigrationPolicy.ProjectIdFor(projectName),
            Declarations(projectName),
            File.Exists(Path.Combine(root, "Dockerfile")),
            BundleCandidates.FirstOrDefault(candidate => Directory.Exists(Path.Combine(root, candidate))));
        return (read, PipelineMigrationPolicy.Propose(text, read, facts));
    }

    private IReadOnlyList<EnvironmentDeclaration> Declarations(string projectName)
    {
        try
        {
            return _environments.Declarations(projectName) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or KeyNotFoundException)
        {
            // AGT-3176 declarations are optional input to the proposal; an unreadable
            // one leaves the proposal on the default environments and is logged.
            _logger.LogWarning(ex, "project-definition-migration declarations unreadable project={Project}", projectName);
            return [];
        }
    }

    private static PipelineDefinitionResponse View(string projectName, bool exists, ProjectDefinitionReadResult read)
    {
        var parsed = read.Parsed;
        return new(
            projectName,
            ProjectPreparationPaths.Definition,
            exists,
            parsed?.SchemaVersion,
            read.IsValid,
            read.Issues,
            read.DefinitionSha256,
            parsed is null ? null : PipelineDiagramProjection.Project(parsed),
            read.IsValid && read.Definition!.SchemaVersion == 1);
    }

    private static PipelineMigrationResponse MigrationView(
        string projectName, string root, ProjectDefinitionReadResult current, PipelineMigrationProposal proposal)
    {
        if (!proposal.Available || proposal.ProposedYaml is null)
            return new(projectName, false, proposal.Reason, current.DefinitionSha256, null, null, false, [], proposal.Notes, null);
        var proposed = ProjectDefinitionReader.Parse(proposal.ProposedYaml, root);
        return new(projectName, true, null, current.DefinitionSha256, proposal.ProposedYaml, proposal.ProposalSha256,
            proposed.IsValid, proposed.Issues, proposal.Notes,
            proposed.Parsed is null ? null : PipelineDiagramProjection.Project(proposed.Parsed));
    }

    private string? Repository(string projectName)
    {
        if (!_scanner.GetWatchPaths().Any(entry => string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase)))
            return null;
        var root = ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);
        return root is not null && Directory.Exists(root) ? root : null;
    }

    private static string DefinitionPath(string root)
        => Path.Combine(root, ProjectPreparationPaths.Definition.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads the file once so the digest, the issues, and the proposal describe the same bytes.</summary>
    private static (string? Text, ProjectDefinitionReadResult Read) ReadCurrent(string root)
    {
        var path = DefinitionPath(root);
        if (!File.Exists(path)) return (null, ProjectDefinitionReader.ReadWorkspace(root));
        var text = File.ReadAllText(path);
        return (text, ProjectDefinitionReader.Parse(text, root));
    }

    private static bool HasUtf8Bom(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }
}
