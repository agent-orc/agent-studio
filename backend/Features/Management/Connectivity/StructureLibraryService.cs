using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Git;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

public sealed record LibraryChannel(string Name, string Version, DateTime? PublishedAt);
public sealed record LibraryRegistry(string ProjectId, string Artifact, string Provider, string Package,
    string? Repository, bool Private, string State, DateTime? CheckedAt, DateTime? LastPublishAt,
    IReadOnlyList<LibraryChannel> Channels);
public sealed record LibraryConsumer(string ProjectId, string Project, string Package, string Version,
    string Source, string? LatestStable, int? ReleasesBehind, bool OwnershipMapped);
public sealed record StructureLibraryResponse(IReadOnlyList<LibraryRegistry> Registries,
    IReadOnlyList<LibraryConsumer> Consumers);

/// <summary>Control-plane registry reads and bounded workspace lock-file inventory. Never serializes resolved secrets.</summary>
public sealed class StructureLibraryService(ProjectRegistry projects, GitService git,
    IHttpClientFactory clients, ISecretProvider secrets, TimeProvider clock, ILogger<StructureLibraryService> logger)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LibraryRegistry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _publish = new(StringComparer.Ordinal);
    private readonly string _publishPath = LocalStateRoot.Combine("AgentStudio", "registry-publishes.jsonl");
    private bool _publishLoaded;

    public void RecordPublish(string projectName, string package, string provider, string version, DateTime at)
    {
        var project = projects.FindByIdOrDisplayName(projectName);
        if (project is null) return;
        lock (_publish)
        {
            LoadPublishes();
            foreach (var declaration in ReadDeclarations(project).Where(item => item.Package == package
                && item.Provider == provider))
            {
                var key = $"{project.Id}|{declaration.Artifact}|{declaration.Provider}|{package}|{version}";
                if (_publish.ContainsKey(key)) continue;
                _publish[key] = at;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_publishPath)!);
                    File.AppendAllText(_publishPath, JsonSerializer.Serialize(new PublishRecord(key, at)) + "\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Registry publish record could not be persisted"); }
            }
        }
    }

    public async Task<StructureLibraryResponse> ReadAsync(bool refresh, CancellationToken ct)
    {
        var records = projects.List().Where(project => !project.Archived).ToArray();
        var declarations = records.SelectMany(project => ReadDeclarations(project).Select(registry => (project, registry))).ToArray();
        var registryRows = new List<LibraryRegistry>();
        foreach (var (project, registry) in declarations)
            registryRows.Add(await LookupAsync(project, registry, refresh, ct));
        var consumers = FindConsumers(records, registryRows);
        return new(registryRows, consumers);
    }

    private IEnumerable<ProjectRegistryDeclaration> ReadDeclarations(ProjectRecord project)
    {
        var root = Root(project);
        if (root is null) return [];
        var path = Path.Combine(root, ProjectPreparationPaths.Definition);
        if (!File.Exists(path)) return [];
        try { return ProjectDefinitionReader.Parse(File.ReadAllText(path)).Definition?.Structure?.Registries ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Registry declaration unavailable for {Project}", project.Id); return []; }
    }

    private string? Root(ProjectRecord project) => git.ResolveRepoRootForProject(project.DisplayName)
        ?? project.RepositoryPath ?? project.RootPath;

    private async Task<LibraryRegistry> LookupAsync(ProjectRecord project, ProjectRegistryDeclaration declaration,
        bool force, CancellationToken ct)
    {
        var key = $"{project.Id}|{declaration.Artifact}|{declaration.Provider}|{declaration.Package}|{declaration.Repository}";
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!force && _attempted.TryGetValue(key, out var attempted) && now - attempted < RefreshInterval
                && _cache.TryGetValue(key, out var cached))
                return cached with { State = cached.CheckedAt is { } checkedAt && now - checkedAt >= RefreshInterval
                    ? "stale" : cached.State };
            _attempted[key] = now;
            _cache.TryGetValue(key, out var previous);
            try
            {
                string? credential = null;
                if (declaration.Provider is "jfrog" or "internal" && declaration.SecretRef is null)
                    throw new InvalidOperationException("registry-secret-unavailable");
                if (declaration.SecretRef is { } reference)
                {
                    if (!SecretReference.TryParse(reference, out var parsed)) throw new InvalidOperationException("invalid-secret-reference");
                    if (parsed.Scope.Kind != SecretScopeKind.Project
                        || !string.Equals(parsed.Scope.Project, project.DisplayName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("registry-secret-scope-unavailable");
                    var workload = new SecretWorkload("registry-lookup", $"registry:{project.Id}", project.DisplayName,
                        null, [parsed.Name], new Dictionary<string, string> { [parsed.Name] = parsed.ToString() });
                    credential = (await secrets.GetForWorkloadAsync(parsed, workload, "registry-lookup", ct)).Value;
                }
                var channels = await FetchAsync(declaration, credential, ct);
                DateTime? last = channels.Where(item => item.PublishedAt is not null)
                    .Select(item => item.PublishedAt).Max();
                var recorded = LastRecordedPublish(project.Id, declaration);
                if (recorded is { } published && (last is null || published > last)) last = published;
                var row = new LibraryRegistry(project.Id, declaration.Artifact, declaration.Provider,
                    declaration.Package, declaration.Repository, IsPrivate(declaration), "current", now,
                    last, channels);
                _cache[key] = row;
                return row;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Errors are state only: provider messages can include a request URI or credential.
                logger.LogWarning("Registry lookup unavailable for {Project} {Provider} {Package}",
                    project.Id, declaration.Provider, declaration.Package);
                var row = previous is null
                    ? new LibraryRegistry(project.Id, declaration.Artifact, declaration.Provider,
                        declaration.Package, declaration.Repository, IsPrivate(declaration),
                        "unavailable", null, LastRecordedPublish(project.Id, declaration), [])
                    : previous with { State = "unavailable", LastPublishAt =
                        LastRecordedPublish(project.Id, declaration) is { } published
                            && (previous.LastPublishAt is null || published > previous.LastPublishAt.Value)
                            ? published : previous.LastPublishAt };
                _cache[key] = row;
                return row;
            }
        }
        finally { _gate.Release(); }
    }

    private static bool IsPrivate(ProjectRegistryDeclaration declaration)
        => declaration.SecretRef is not null || declaration.Provider is "jfrog" or "internal";

    private async Task<IReadOnlyList<LibraryChannel>> FetchAsync(ProjectRegistryDeclaration declaration,
        string? credential, CancellationToken ct)
    {
        var provider = declaration.Provider;
        var package = Uri.EscapeDataString(declaration.Package);
        var url = provider switch
        {
            "npm" => $"https://registry.npmjs.org/{package}",
            "nuget" => $"https://api.nuget.org/v3-flatcontainer/{package.ToLowerInvariant()}/index.json",
            "docker-hub" => $"https://hub.docker.com/v2/repositories/{declaration.Package}/tags?page_size=100",
            "github" => $"https://api.github.com/repos/{declaration.Package}/releases?per_page=100",
            "jfrog" or "internal" when declaration.BaseUrl is { } baseUrl &&
                Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                => $"{uri.ToString().TrimEnd('/')}/api/storage/{Uri.EscapeDataString(declaration.Repository ?? "")}/{package}?list&deep=1",
            _ => throw new InvalidOperationException("registry-configuration-unavailable")
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AgentStudio-Structure/1");
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await clients.CreateClient("structure-registry").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        var rows = new List<LibraryChannel>();
        if (provider == "npm")
        {
            var times = root.TryGetProperty("time", out var time) ? time : default;
            foreach (var tag in root.GetProperty("dist-tags").EnumerateObject())
                rows.Add(new(tag.Name, tag.Value.GetString() ?? "", ParseDate(times, tag.Value.GetString())));
            if (root.TryGetProperty("versions", out var versions))
                foreach (var version in versions.EnumerateObject())
                    rows.Add(new("version", version.Name, ParseDate(times, version.Name)));
        }
        else if (provider == "nuget")
        {
            foreach (var version in root.GetProperty("versions").EnumerateArray())
                rows.Add(new("version", version.GetString() ?? "", null));
            if (rows.Count > 0)
            {
                var latest = rows[^1].Version;
                try
                {
                    var metadataUrl = $"https://api.nuget.org/v3/registration5-semver2/{package.ToLowerInvariant()}/{Uri.EscapeDataString(latest)}.json";
                    using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
                    metadataRequest.Headers.UserAgent.ParseAdd("AgentStudio-Structure/1");
                    using var metadataResponse = await clients.CreateClient("structure-registry").SendAsync(metadataRequest, ct);
                    metadataResponse.EnsureSuccessStatusCode();
                    using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStreamAsync(ct));
                    var catalog = metadata.RootElement.TryGetProperty("catalogEntry", out var entry) ? entry : default;
                    rows.Add(new("latest", latest, ParseDate(catalog, "published")));
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException
                    || ex is TaskCanceledException && !ct.IsCancellationRequested)
                { rows.Add(new("latest", latest, null)); }
            }
        }
        else if (provider == "docker-hub")
            foreach (var tag in root.GetProperty("results").EnumerateArray())
                rows.Add(new(tag.GetProperty("name").GetString() ?? "tag", tag.GetProperty("name").GetString() ?? "",
                    ParseDate(tag, "last_updated")));
        else if (provider == "github")
            foreach (var release in root.EnumerateArray())
                rows.Add(new(release.GetProperty("prerelease").GetBoolean() ? "next" : "latest",
                    release.GetProperty("tag_name").GetString() ?? "", ParseDate(release, "published_at")));
        else if (root.TryGetProperty("files", out var files))
            foreach (var file in files.EnumerateArray())
            {
                var name = file.GetProperty("uri").GetString()?.TrimStart('/') ?? "";
                if (name.Length > 0) rows.Add(new("version", name, ParseDate(file, "lastModified")));
            }
        return rows;
    }

    private static DateTime? ParseDate(JsonElement element, string? key)
        => key is not null && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out var value)
            && DateTime.TryParse(value.GetString(), out var parsed) ? parsed.ToUniversalTime() : null;

    private sealed record PublishRecord(string Key, DateTime At);

    private DateTime? LastRecordedPublish(string projectId, ProjectRegistryDeclaration declaration)
    {
        lock (_publish)
        {
            LoadPublishes();
            var prefix = $"{projectId}|{declaration.Artifact}|{declaration.Provider}|{declaration.Package}|";
            return _publish.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(item => (DateTime?)item.Value).Max();
        }
    }

    private void LoadPublishes()
    {
        if (_publishLoaded) return;
        _publishLoaded = true;
        if (!File.Exists(_publishPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_publishPath))
            {
                try
                {
                    if (JsonSerializer.Deserialize<PublishRecord>(line) is { } record)
                        _publish[record.Key] = record.At;
                }
                catch (JsonException)
                { logger.LogWarning("An incomplete registry publish record was skipped"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning("Registry publish records could not be loaded"); }
    }

    private IReadOnlyList<LibraryConsumer> FindConsumers(IReadOnlyList<ProjectRecord> records,
        IReadOnlyList<LibraryRegistry> registries)
    {
        var packages = registries.Select(item => item.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<LibraryConsumer>();
        foreach (var project in records)
        {
            var root = Root(project);
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var file in LockFiles(root))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var package in packages)
                    {
                        var version = LockedVersion(json.RootElement, file, package);
                        if (version is null) continue;
                        var candidateRegistries = registries.Where(item =>
                            item.Package.Equals(package, StringComparison.OrdinalIgnoreCase)).ToArray();
                        var hint = RegistryHint(json.RootElement, file, package);
                        var registry = candidateRegistries.FirstOrDefault(item => item.Provider == hint
                            || hint == "jfrog" && item.Provider == "internal")
                            ?? candidateRegistries[0];
                        var stable = StableVersions(registry.Channels);
                        var behind = ReleasesBehind(stable, version);
                        var mapped = records.Any(owner => owner.OwnershipMappings.Any(mapping =>
                            (mapping.PackageOrModule == package || mapping.ReleaseArtifact == package)
                            && mapping.ConsumerProjectIds.Contains(project.Id)));
                        rows.Add(new(project.Id, project.DisplayName, package, version,
                            Path.GetRelativePath(root, file).Replace('\\', '/'), stable.LastOrDefault(), behind, mapped));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                    or InvalidOperationException)
                { logger.LogWarning("Consumer lock file unavailable for {Project}", project.Id); }
            }
        }
        foreach (var owner in records)
            foreach (var mapping in owner.OwnershipMappings)
            {
                var package = mapping.PackageOrModule ?? mapping.ReleaseArtifact;
                if (package is null || !packages.Contains(package)) continue;
                foreach (var consumerId in mapping.ConsumerProjectIds)
                {
                    var consumer = records.FirstOrDefault(item => item.Id == consumerId);
                    if (consumer is null || rows.Any(item => item.ProjectId == consumerId && item.Package == package)) continue;
                    rows.Add(new(consumer.Id, consumer.DisplayName, package, "unavailable",
                        "ownership mapping", null, null, true));
                }
            }
        return rows.DistinctBy(item => (item.ProjectId, item.Package, item.Version)).ToArray();
    }

    private static IEnumerable<string> LockFiles(string root)
    {
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Dequeue();
            foreach (var name in new[] { "package-lock.json", "packages.lock.json" })
            { var file = Path.Combine(dir, name); if (File.Exists(file)) yield return file; }
            if (depth >= 4) continue;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children.Where(path => !new[] { ".git", "node_modules", "bin", "obj", "dist", "coverage" }
                         .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
                pending.Enqueue((child, depth + 1));
        }
    }

    private static string? LockedVersion(JsonElement root, string file, string package)
    {
        if (Path.GetFileName(file) == "packages.lock.json")
        {
            if (!root.TryGetProperty("dependencies", out var frameworks)) return null;
            foreach (var framework in frameworks.EnumerateObject())
                if (framework.Value.ValueKind == JsonValueKind.Object)
                    foreach (var dependency in framework.Value.EnumerateObject())
                        if (dependency.Name.Equals(package, StringComparison.OrdinalIgnoreCase)
                            && dependency.Value.TryGetProperty("resolved", out var resolved))
                            return resolved.GetString();
            return null;
        }
        if (root.TryGetProperty("packages", out var packages)
            && packages.TryGetProperty($"node_modules/{package}", out var entry)
            && entry.TryGetProperty("version", out var version)) return version.GetString();
        if (root.TryGetProperty("dependencies", out var dependencies)
            && dependencies.TryGetProperty(package, out entry)
            && entry.TryGetProperty("version", out version)) return version.GetString();
        return null;
    }

    private static string? RegistryHint(JsonElement root, string file, string package)
    {
        if (Path.GetFileName(file) == "packages.lock.json") return "nuget";
        if (!root.TryGetProperty("packages", out var packages)
            || !packages.TryGetProperty($"node_modules/{package}", out var entry)
            || !entry.TryGetProperty("resolved", out var resolved)) return "npm";
        var url = resolved.GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "npm";
        return uri.Host.Equals("registry.npmjs.org", StringComparison.OrdinalIgnoreCase) ? "npm" : "jfrog";
    }

    public static int? ReleasesBehind(IReadOnlyList<string> stableVersions, string pinnedVersion)
    {
        var index = stableVersions.ToList().IndexOf(pinnedVersion);
        return index < 0 ? null : stableVersions.Count - index - 1;
    }

    private static List<string> StableVersions(IReadOnlyList<LibraryChannel> channels)
    {
        var latest = channels.FirstOrDefault(item => item.Name == "latest")?.Version;
        return channels.Where(item => item.Name == "version" && !item.Version.Contains('-')
                && (latest is null || CompareVersions(item.Version, latest) <= 0))
            .Select(item => item.Version).Distinct().OrderBy(item => Version.TryParse(item, out var version)
                ? version : new Version(0, 0)).ToList();
    }

    private static int CompareVersions(string left, string right)
        => Version.TryParse(left, out var a) && Version.TryParse(right, out var b)
            ? a.CompareTo(b) : string.CompareOrdinal(left, right);
}

public sealed class StructureRegistryRefreshService(StructureLibraryService library,
    ILogger<StructureRegistryRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await library.ReadAsync(true, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Scheduled registry metadata refresh failed"); }
        }
    }
}
