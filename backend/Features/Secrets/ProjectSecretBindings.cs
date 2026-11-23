using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Secrets;

public sealed record ProjectSecretBinding(string Name, string Reference, string BoundBy, DateTime BoundAt,
    string? ApprovedBy);

public sealed record MissingSecretReference(string Name, string Reference, string Scope, string Reason);

public sealed record ProjectSecretPreflight(string Project, string Environment, IReadOnlyList<ProjectSecretBinding> Bindings,
    IReadOnlyList<MissingSecretReference> Missing)
{
    public bool Ready => Missing.Count == 0;
}

/// <summary>Environment bindings are control metadata. Values stay in ISecretProvider.</summary>
/// <remarks>
/// A binding file and a new reference name the project by its registry id
/// (AGT-3987), so a rename keeps both. A binding file written under the display
/// name before then is read and moved to the id key on its next write.
/// </remarks>
public sealed class ProjectSecretBindings(IConfiguration configuration, ISecretProvider provider,
    ProjectEnvironmentService environments, TimeProvider clock, SecretProjectKeys? keys = null)
{
    private readonly SecretProjectKeys _keys = keys ?? SecretProjectKeys.Identity;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<ProjectSecretBinding> List(string project, string environment)
    {
        lock (_gate) return Read(project, environment);
    }

    public IReadOnlyDictionary<string, string> ApprovedReferences(string project, string environment)
    {
        (string Kind, IReadOnlyList<string> Names) declared;
        try { declared = Declared(project, environment); }
        catch (Exception ex) when (ex is SecretValidationException or JsonException)
        {
            return new Dictionary<string, string>();
        }
        var names = environment == "preview" ? List(project, environment).Select(item => item.Name).ToArray() : declared.Names;
        return ApprovedReferences(project, environment, names, declared.Kind);
    }

    public IReadOnlyDictionary<string, string> ApprovedReferences(string project, string environment,
        IReadOnlyList<string> secretNames, string environmentKind)
    {
        var production = environmentKind == "production";
        return List(project, environment)
            .Where(item => secretNames.Contains(item.Name, StringComparer.Ordinal)
                && (!production || item.ApprovedBy is not null))
            .ToDictionary(item => item.Name, item => item.Reference, StringComparer.Ordinal);
    }

    public ProjectSecretBinding Bind(string project, string environment, string name, string reference,
        string actor, string? approvedBy)
    {
        if (SecretAccessPolicy.IsPlatformOnly(name))
            throw new SecretAccessDeniedException("platform-secret", "This secret belongs to the platform; no workload receives it.");
        var declared = Declared(project, environment);
        if (environment != "preview" && !declared.Names.Contains(name, StringComparer.Ordinal))
            throw new SecretValidationException("reference-not-declared", "The environment does not declare this secret name.");
        var parsed = SecretReference.Parse(reference);
        if (parsed.Name != name || parsed.Scope.Kind == SecretScopeKind.Environment
                && (!_keys.Owns(project, parsed.Scope.Project) || parsed.Scope.Environment != environment)
            || parsed.Scope.Kind == SecretScopeKind.Project && !_keys.Owns(project, parsed.Scope.Project))
            throw new SecretValidationException("binding-scope-denied", "Binding must use the same name and this project's scope.");
        if (declared.Kind == "production" && approvedBy is null)
            throw new SecretAccessDeniedException("approval-required", "Production binding requires an approver.");
        var binding = new ProjectSecretBinding(name, parsed.ToString(), actor, clock.GetUtcNow().UtcDateTime, approvedBy);
        lock (_gate) Write(project, environment, Read(project, environment).Where(item => item.Name != name).Append(binding));
        return binding;
    }

    /// <summary>Studio binds the credential it generated for a managed database (AGT-3877); no person chose the value.</summary>
    public const string ManagedDatabaseActor = "studio:managed-database";

    /// <summary>
    /// Binds a managed database credential in the environment's own scope. The
    /// repository approved the database through project.yml and a deploy, and
    /// the value never left Studio, so production needs no separate approver.
    /// </summary>
    public ProjectSecretBinding BindManagedDatabase(string project, string environment, string name)
    {
        if (!name.StartsWith(ProjectDatabaseVocabulary.CredentialPrefix, StringComparison.Ordinal))
            throw new SecretValidationException("binding-scope-denied", "Only managed database credentials bind without a declaration.");
        var reference = new SecretReference(SecretScope.ForEnvironment(project, environment), name).ToString();
        var binding = new ProjectSecretBinding(name, reference, ManagedDatabaseActor, clock.GetUtcNow().UtcDateTime,
            ManagedDatabaseActor);
        lock (_gate) Write(project, environment, Read(project, environment).Where(item => item.Name != name).Append(binding));
        return binding;
    }

    /// <summary>Studio binds the ingest token it minted for deployed workloads (M5); no person chose the value.</summary>
    public const string TelemetryIngestActor = "studio:telemetry";

    /// <summary>
    /// Binds the deploy ingest token in the environment's own scope. The
    /// repository enabled telemetry for the environment in project.yml, and the
    /// token is scoped to that environment's intake, so production needs no
    /// separate approver.
    /// </summary>
    public ProjectSecretBinding BindTelemetryIngest(string project, string environment)
    {
        var name = DeployTelemetryVocabulary.SecretName;
        var reference = new SecretReference(SecretScope.ForEnvironment(project, environment), name).ToString();
        var binding = new ProjectSecretBinding(name, reference, TelemetryIngestActor, clock.GetUtcNow().UtcDateTime,
            TelemetryIngestActor);
        lock (_gate) Write(project, environment, Read(project, environment).Where(item => item.Name != name).Append(binding));
        return binding;
    }

    /// <summary>Removes the binding of a managed database credential after its data was deleted.</summary>
    public void UnbindManagedDatabase(string project, string environment, string name)
    {
        lock (_gate)
        {
            var entries = Read(project, environment);
            if (entries.Any(item => item.Name == name && item.BoundBy == ManagedDatabaseActor))
                Write(project, environment, entries.Where(item => item.Name != name));
        }
    }

    /// <summary>
    /// The project keys a workload of this environment may read: the project's
    /// id and display name, plus the project segment of each reference Studio
    /// bound for it. A binding made before a rename keeps its old display name.
    /// </summary>
    public IReadOnlyCollection<string> ProjectKeys(string project, IReadOnlyDictionary<string, string> bound)
        => _keys.Keys(project)
            .Concat(bound.Values.Select(reference => SecretReference.TryParse(reference, out var parsed) ? parsed.Scope.Project : null)
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void Write(string project, string environment, IEnumerable<ProjectSecretBinding> bindings)
    {
        var entries = bindings.ToArray();
        var path = PathFor(project, environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, _json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        foreach (var legacy in LegacyPaths(project, environment))
            if (File.Exists(legacy)) File.Delete(legacy);
    }

    public async Task<ProjectSecretPreflight> CheckAsync(string project, string environment, CancellationToken ct = default)
    {
        var declared = Declared(project, environment);
        var names = environment == "preview" ? List(project, environment).Select(item => item.Name).ToArray() : declared.Names;
        return await CheckAsync(project, environment, names, declared.Kind, ct);
    }

    public async Task<ProjectSecretPreflight> CheckAsync(string project, string environment,
        IReadOnlyList<string> secretNames, string environmentKind, CancellationToken ct = default)
    {
        var bindings = List(project, environment)
            .Where(item => secretNames.Contains(item.Name, StringComparer.Ordinal)).ToArray();
        var listing = await provider.ListAsync(null, ct);
        var records = listing.Records;
        var unreadable = listing.Unreadable;
        var missing = new List<MissingSecretReference>();
        foreach (var name in secretNames.Distinct(StringComparer.Ordinal))
        {
            var binding = bindings.FirstOrDefault(item => item.Name == name);
            var requested = binding?.Reference
                ?? new SecretReference(SecretScope.ForEnvironment(_keys.Canonical(project), environment), name).ToString();
            if (binding is null)
            {
                missing.Add(new(name, requested, project + "/" + environment, "not-bound"));
                continue;
            }
            if (environmentKind == "production" && binding.ApprovedBy is null)
            {
                missing.Add(new(name, requested, project + "/" + environment, "approval-required"));
                continue;
            }
            var reference = SecretReference.Parse(requested);
            var scopes = _keys.LookupChain(reference.Scope);
            SecretRecord? record = null;
            var blocked = false;
            foreach (var scope in scopes)
            {
                if (unreadable.Any(item => item.Name == name && item.Scope == scope.Key))
                {
                    blocked = true;
                    break;
                }
                record = records.FirstOrDefault(item => item.ParsedScope == scope && item.Name == name);
                if (record is not null) break;
            }
            var reason = blocked ? "unreadable" : record is null ? "not-found"
                : reference.Version is { } version
                    ? record.Versions.Any(item => item.Version == version && item.Active) ? null : "revoked"
                    : SecretVersionPolicy.CurrentVersion(record) is null ? "revoked" : null;
            if (reason is not null) missing.Add(new(name, requested, reference.Scope.Key, reason));
        }
        return new(project, environment, bindings, missing);
    }

    private (string Kind, IReadOnlyList<string> Names) Declared(string project, string environment)
    {
        var declarations = environments.Declarations(project)
            ?? throw new SecretValidationException("unknown-project", "The project does not exist.");
        if (environment == "preview") return ("preview", []);
        var declaration = declarations.FirstOrDefault(item => item.Id == environment)
            ?? throw new SecretValidationException("unknown-environment", "The project environment does not exist.");
        return (declaration.Kind, declaration.SecretRefs);
    }

    /// <summary>Callers hold <see cref="_gate"/>. A display-name file is moved to the id key when it is read.</summary>
    private IReadOnlyList<ProjectSecretBinding> Read(string project, string environment)
    {
        var canonical = PathFor(project, environment);
        if (File.Exists(canonical)) return Parse(canonical);
        if (LegacyPaths(project, environment).FirstOrDefault(File.Exists) is not { } legacy) return [];
        var entries = Parse(legacy);
        try { Write(project, environment, entries); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The display-name file still answers; the move is retried on the next read.
            SilentCatch.Note(exception, "ProjectSecretBindings: binding file not moved to the project id");
        }
        return entries;
    }

    private ProjectSecretBinding[] Parse(string path)
        => JsonSerializer.Deserialize<ProjectSecretBinding[]>(File.ReadAllText(path), _json) ?? [];

    /// <summary>The binding file of an environment, keyed by the project's registry id.</summary>
    private string PathFor(string project, string environment) => FileFor(_keys.Canonical(project), environment);

    /// <summary>Files written under the project's display name before AGT-3987.</summary>
    private IEnumerable<string> LegacyPaths(string project, string environment)
    {
        var canonical = PathFor(project, environment);
        return new[] { _keys.DisplayName(project), project }
            .Select(name => FileFor(name, environment))
            .Where(path => path != canonical).Distinct(StringComparer.Ordinal);
    }

    private string FileFor(string projectKey, string environment)
    {
        SecretScope.ForEnvironment(projectKey, environment);
        var root = configuration["TaskRepository"] ?? throw new InvalidOperationException("TaskRepository is required.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectKey + "\n" + environment))).ToLowerInvariant();
        return Path.Combine(root, ".security", "secret-bindings", key + ".json");
    }
}
