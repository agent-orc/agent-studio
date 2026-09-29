using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner;

public sealed record WorkstationRepositoryRoot(string Name, string Path)
{
    public string CapabilityKey => $"local-repo:{Name}";
}

public sealed record WorkstationPreview(Uri Origin, string Reachability, int MaxLifetimeSeconds)
{
    public string CapabilityKey => $"preview-url:{Reachability}";
}

/// <summary>
/// Local sources and preview links are bounded host capabilities. This policy
/// runs before a claimed task creates a worker; the Task Server still owns the
/// claim, lease and result authority.
/// </summary>
public static class WorkstationProfile
{
    private static readonly Regex NamePattern = new(
        "^[a-z][a-z0-9-]{0,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<WorkstationRepositoryRoot> ParseRoots(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var roots = new List<WorkstationRepositoryRoot>();
        foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator < 1)
                throw new ArgumentException("RUNNER_WORKSTATION_ROOTS entries must be name=absolute-path.");
            var name = entry[..separator].Trim().ToLowerInvariant();
            var path = entry[(separator + 1)..].Trim();
            if (!NamePattern.IsMatch(name) || !System.IO.Path.IsPathFullyQualified(path))
                throw new ArgumentException("RUNNER_WORKSTATION_ROOTS requires a bounded name and absolute path.");
            if (roots.Any(root => root.Name == name))
                throw new ArgumentException($"Duplicate workstation repository root name '{name}'.");
            roots.Add(new WorkstationRepositoryRoot(name, System.IO.Path.GetFullPath(path)));
        }
        if (roots.Count > 16)
            throw new ArgumentException("RUNNER_WORKSTATION_ROOTS allows at most 16 roots.");
        return roots;
    }

    public static IReadOnlyList<string> ParseTools(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var tools = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (tools.Length > 16 || tools.Any(tool => !NamePattern.IsMatch(tool)))
            throw new ArgumentException("RUNNER_WORKSTATION_TOOLS allows at most 16 executable names.");
        return tools;
    }

    public static WorkstationPreview? ParsePreview(string origin, string reachability, string lifetime)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new ArgumentException("RUNNER_PREVIEW_ORIGIN must be an HTTPS origin or loopback HTTP origin without credentials or a path.");
        var scope = reachability.Trim().ToLowerInvariant();
        if (scope is not ("operator-browser" or "host-only" or "public"))
            throw new ArgumentException("RUNNER_PREVIEW_REACHABILITY must be operator-browser, host-only, or public.");
        if (!int.TryParse(lifetime, out var seconds) || seconds is < 1 or > 86400)
            throw new ArgumentException("RUNNER_PREVIEW_LIFETIME_SECONDS must be between 1 and 86400.");
        return new WorkstationPreview(uri, scope, seconds);
    }

    public static bool RootAvailable(WorkstationRepositoryRoot root)
        => Directory.Exists(root.Path) && !ContainsReparsePoint(root.Path);

    public static bool TryAdmitSource(
        string? source,
        IReadOnlyList<WorkstationRepositoryRoot> roots,
        out string reason)
    {
        reason = "ready";
        if (string.IsNullOrWhiteSpace(source)) return true;
        string? localPath = null;
        if (System.IO.Path.IsPathFullyQualified(source)) localPath = source;
        else if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.IsFile
                || uri.IsUnc || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                reason = "local-repository-url-invalid";
                return false;
            }
            localPath = uri.LocalPath;
        }
        if (localPath is null)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var remote)
                && remote.Scheme is "https" or "http" or "ssh" or "git"
                && remote.UserInfo.Length == 0)
                return true;
            if (Regex.IsMatch(source, @"^[^/\\:@]+@[^/\\:]+:.+$", RegexOptions.CultureInvariant))
                return true;
            reason = "repository-source-not-an-authorized-remote-or-local-root";
            return false;
        }
        if (localPath.StartsWith("\\\\", StringComparison.Ordinal)
            || localPath.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "local-repository-network-share-not-authorized";
            return false;
        }
        if (!System.IO.Path.IsPathFullyQualified(localPath))
        {
            reason = "local-repository-path-not-absolute";
            return false;
        }
        var fullPath = System.IO.Path.GetFullPath(localPath);
        if (!Directory.Exists(fullPath) || ContainsReparsePoint(fullPath))
        {
            reason = "local-repository-unavailable-or-reparse-point";
            return false;
        }
        foreach (var root in roots.Where(RootAvailable))
        {
            var relative = System.IO.Path.GetRelativePath(root.Path, fullPath);
            if (relative == "." || relative != ".."
                && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !System.IO.Path.IsPathRooted(relative))
                return true;
        }
        reason = "local-repository-root-not-authorized";
        return false;
    }

    public static bool TryAdmitClaim(
        RunnerOptions options,
        string? repositoryUrl,
        IReadOnlyList<string>? requiredCapabilities,
        out string reason)
    {
        reason = "ready";
        if (!options.IsWorkstation) return true;
        if (!TryAdmitSource(repositoryUrl, options.WorkstationRepositoryRoots, out reason)) return false;
        foreach (var rootKey in (requiredCapabilities ?? [])
                     .Where(key => key.StartsWith("local-repo:", StringComparison.Ordinal)))
        {
            if (options.WorkstationRepositoryRoots.Any(root => root.CapabilityKey == rootKey
                                                              && RootAvailable(root)))
                continue;
            reason = $"workstation-root-unavailable:{rootKey}";
            return false;
        }
        var requiredTools = new[] { "git" }.Concat(options.WorkstationRequiredTools).Concat(
            (requiredCapabilities ?? [])
            .Where(key => key.StartsWith("toolchain:", StringComparison.Ordinal))
            .Select(key => key["toolchain:".Length..]));
        var missing = requiredTools.FirstOrDefault(tool => !RunnerCapabilityProbe.OnPath(tool));
        if (missing is null) return true;
        reason = $"workstation-tool-unavailable:{missing}";
        return false;
    }

    public static bool TryValidatePreviewArtifact(
        ReadOnlySpan<byte> bytes,
        WorkstationPreview? preview,
        DateTimeOffset now,
        out string reason)
    {
        reason = "preview-capability-unavailable";
        if (preview is null || bytes.Length is 0 or > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("url", out var urlProperty)
                || !root.TryGetProperty("reachableFrom", out var reachabilityProperty)
                || !root.TryGetProperty("createdAtUtc", out var createdProperty)
                || !root.TryGetProperty("expiresAtUtc", out var expiresProperty)
                || !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var url)
                || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0
                || url.GetLeftPart(UriPartial.Authority) != preview.Origin.GetLeftPart(UriPartial.Authority)
                || reachabilityProperty.GetString() != preview.Reachability
                || !DateTimeOffset.TryParse(createdProperty.GetString(), out var created)
                || !DateTimeOffset.TryParse(expiresProperty.GetString(), out var expires)
                || created > now.AddMinutes(1) || expires <= now
                || expires <= created || expires - created > TimeSpan.FromSeconds(preview.MaxLifetimeSeconds))
            {
                reason = "preview-artifact-outside-declared-reachability-or-lifetime";
                return false;
            }
            reason = "ready";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            reason = "preview-artifact-invalid-json";
            return false;
        }
    }

    private static bool ContainsReparsePoint(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(System.IO.Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}
