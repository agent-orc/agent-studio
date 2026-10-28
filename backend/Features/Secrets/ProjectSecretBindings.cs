using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Projects;

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
public sealed class ProjectSecretBindings(IConfiguration configuration, ISecretProvider provider,
    ProjectEnvironmentService environments, TimeProvider clock)
{
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
        var declared = Declared(project, environment);
        if (environment != "preview" && !declared.Names.Contains(name, StringComparer.Ordinal))
            throw new SecretValidationException("reference-not-declared", "The environment does not declare this secret name.");
        var parsed = SecretReference.Parse(reference);
        if (parsed.Name != name || parsed.Scope.Kind == SecretScopeKind.Environment
                && (!string.Equals(parsed.Scope.Project, project, StringComparison.OrdinalIgnoreCase)
                    || parsed.Scope.Environment != environment)
            || parsed.Scope.Kind == SecretScopeKind.Project
                && !string.Equals(parsed.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            throw new SecretValidationException("binding-scope-denied", "Binding must use the same name and this project's scope.");
        if (declared.Kind == "production" && approvedBy is null)
            throw new SecretAccessDeniedException("approval-required", "Production binding requires an approver.");
        var binding = new ProjectSecretBinding(name, parsed.ToString(), actor, clock.GetUtcNow().UtcDateTime, approvedBy);
        lock (_gate)
        {
            var entries = Read(project, environment).Where(item => item.Name != name).Append(binding).ToArray();
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
        }
        return binding;
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
            var requested = binding?.Reference ?? new SecretReference(SecretScope.ForEnvironment(project, environment), name).ToString();
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
            var scopes = reference.Scope.InheritanceChain();
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

    private IReadOnlyList<ProjectSecretBinding> Read(string project, string environment)
    {
        var path = PathFor(project, environment);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ProjectSecretBinding[]>(File.ReadAllText(path), _json) ?? []
            : [];
    }

    private string PathFor(string project, string environment)
    {
        SecretScope.ForEnvironment(project, environment);
        var root = configuration["TaskRepository"] ?? throw new InvalidOperationException("TaskRepository is required.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project + "\n" + environment))).ToLowerInvariant();
        return Path.Combine(root, ".security", "secret-bindings", key + ".json");
    }
}
