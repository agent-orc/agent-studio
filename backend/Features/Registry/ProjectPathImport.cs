using System.Text.RegularExpressions;

namespace AgentStudio.Registry;

public sealed record ProjectPathMapping(string WindowsPrefix, string ServerPrefix);
public sealed record ProjectPathImportRequest(IReadOnlyList<ProjectPathMapping> Mappings, bool Apply = false);
public sealed record ProjectPathImportRow(
    string ProjectId, string ProjectName, string Status, string? RepositoryPath,
    string? RootPath, string? StorageLocation, string? Reason);

/// <summary>Plans a workspace move without ever treating a Windows path as a Linux path.</summary>
public static class ProjectPathImport
{
    private static readonly Regex WindowsAbsolute = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    public static ProjectPathImportRow Plan(ProjectRecord project, IReadOnlyList<ProjectPathMapping> mappings)
    {
        var repository = Map(project.RepositoryPath, mappings);
        var root = Map(project.RootPath, mappings);
        var storage = Map(project.StorageLocation, mappings);
        if ((!string.IsNullOrWhiteSpace(project.RepositoryPath) && repository is null)
            || (!string.IsNullOrWhiteSpace(project.RootPath) && root is null)
            || (!string.IsNullOrWhiteSpace(project.StorageLocation) && storage is null))
            return new(project.Id, project.DisplayName, "unmapped", repository, root, storage,
                "A Windows path has no server mapping.");
        root ??= repository;
        var checkout = repository ?? root;
        if (checkout is null)
            return new(project.Id, project.DisplayName, "missing-checkout", repository, root, storage,
                "No server checkout path matched this project.");
        if (!Directory.Exists(checkout))
            return new(project.Id, project.DisplayName, "missing-checkout", repository, root, storage,
                $"Server checkout does not exist: {checkout}");
        if (root is not null && !Directory.Exists(root))
            return new(project.Id, project.DisplayName, "missing-checkout", repository, root, storage,
                $"Server working directory does not exist: {root}");
        if (!Directory.Exists(Path.Combine(checkout, ".git"))
            && !File.Exists(Path.Combine(checkout, ".git")))
            return new(project.Id, project.DisplayName, "missing-checkout", repository, root, storage,
                $"Server checkout is not a git repository: {checkout}");
        if (storage is not null && !Directory.Exists(storage))
            return new(project.Id, project.DisplayName, "missing-storage", repository, root, storage,
                $"Task storage does not exist: {storage}");
        return new(project.Id, project.DisplayName, "ready", checkout, root, storage, null);
    }

    public static string? Map(string? source, IReadOnlyList<ProjectPathMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (!WindowsAbsolute.IsMatch(source)) return Path.IsPathRooted(source) ? source : null;
        var normalized = source.Replace('/', '\\').TrimEnd('\\');
        foreach (var mapping in mappings.OrderByDescending(row => row.WindowsPrefix?.Length ?? 0))
        {
            var prefix = mapping.WindowsPrefix?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(prefix) || !WindowsAbsolute.IsMatch(prefix)
                || !Path.IsPathRooted(mapping.ServerPrefix)) continue;
            if (!normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = normalized[prefix.Length..].TrimStart('\\');
            var segments = suffix.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment is "." or "..")) return null;
            return Path.GetFullPath(Path.Combine([mapping.ServerPrefix, .. segments]));
        }
        return null;
    }
}
