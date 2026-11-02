using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>One dependency preparation command that precedes gate verification.</summary>
/// <param name="CoveredByDependencyScopes">
/// True only when <see cref="DependencyScopes"/> cover everything the command
/// prepares, so a dependency-cache hit on every scope may skip it
/// (<see cref="AgentStudio.TaskServer.Contracts.DependencyPreparationSkipPolicy"/>).
/// </param>
public sealed record GatePreparationCommand(
    VerifyEcosystem Ecosystem,
    string WorkingSubdir,
    string Command,
    VerifyCommandShell Shell,
    IReadOnlyList<GateDependencyScope> DependencyScopes,
    IReadOnlyList<string>? PreserveGlobs = null,
    bool CoveredByDependencyScopes = false);

/// <summary>
/// One install root and its root-relative lockfiles. The gate feeds this shape
/// into <see cref="AgentStudio.TaskServer.Contracts.DependencyPreparationState"/>
/// after restoring the shared repository cache.
/// </summary>
public sealed record GateDependencyScope(
    string WorkingSubdir,
    IReadOnlyList<string> Lockfiles);

/// <summary>
/// Derives dependency preparation for a disposable gate workspace. An explicit
/// build-profile install command is authoritative. Otherwise the same concrete
/// repository and command conventions as <see cref="VerifyCommandPlanner"/>
/// yield <c>dotnet restore</c> and one <c>npm ci</c> per selected package root.
/// A conventional <c>npm ci</c> prepares exactly its lockfile root and is
/// covered by that scope. A profile install command is opaque shell and is
/// covered only when the profile says so
/// (<see cref="BuildProfile.InstallCoveredByLockfiles"/>); <c>dotnet restore</c>
/// has no scope and is never covered (AGT-3429).
/// </summary>
public static partial class GatePreparationPlanner
{
    private const string DotNetRestore = "dotnet restore";
    private const string NpmCi = "npm ci";

    public static IReadOnlyList<GatePreparationCommand> Plan(
        string repositoryPath,
        BuildProfile? profile,
        IReadOnlyList<VerifyCommand> selectedCommands)
    {
        if (!string.IsNullOrWhiteSpace(profile?.InstallCmd))
        {
            var scopes = ProfileDependencyScopes(repositoryPath, profile.Lockfiles);
            return
            [
                new GatePreparationCommand(
                    VerifyEcosystem.Custom,
                    "",
                    profile.InstallCmd.Trim(),
                    VerifyCommandShell.Bash,
                    scopes,
                    profile.PreserveGlobs,
                    CoveredByDependencyScopes: profile.InstallCoveredByLockfiles && scopes.Count > 0),
            ];
        }

        var result = new List<GatePreparationCommand>();
        var customCommands = selectedCommands
            .Where(command => command.Ecosystem == VerifyEcosystem.Custom)
            .Select(command => command.Command)
            .ToArray();

        var needsDotNetRestore = selectedCommands.Any(command => command.Ecosystem == VerifyEcosystem.DotNet)
            || customCommands.Any(command => DotNetCommand().IsMatch(command));
        if (needsDotNetRestore && VerifyCommandPlanner.HasDotNetEntryPoint(repositoryPath))
        {
            result.Add(new GatePreparationCommand(
                VerifyEcosystem.DotNet,
                "",
                DotNetRestore,
                customCommands.Length > 0 ? VerifyCommandShell.Bash : VerifyCommandShell.Platform,
                [],
                profile?.PreserveGlobs));
        }

        var nodeDirs = new HashSet<string>(
            selectedCommands
                .Where(command => command.Ecosystem == VerifyEcosystem.Node)
                .Select(command => command.WorkingSubdir),
            StringComparer.OrdinalIgnoreCase);

        foreach (var command in customCommands.Where(command => NpmCommand().IsMatch(command)))
        {
            var prefixes = NpmPrefix().Matches(command)
                .Select(match => match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["bare"].Value)
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(prefix => NormalizeRepositorySubdir(repositoryPath, prefix))
                .Where(prefix => prefix is not null)
                .Cast<string>()
                .ToArray();
            if (prefixes.Length > 0)
            {
                foreach (var prefix in prefixes) nodeDirs.Add(prefix);
                continue;
            }

            var discovered = VerifyCommandPlanner.NodePackageDirs(repositoryPath);
            if (discovered.Contains("", StringComparer.OrdinalIgnoreCase))
                nodeDirs.Add("");
            else
                foreach (var subdir in discovered) nodeDirs.Add(subdir);
        }

        foreach (var subdir in nodeDirs.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var scopes = ConventionalNodeDependencyScopes(repositoryPath, subdir);
            result.Add(new GatePreparationCommand(
                VerifyEcosystem.Node,
                subdir,
                NpmCi,
                customCommands.Length > 0 ? VerifyCommandShell.Bash : VerifyCommandShell.Platform,
                scopes,
                profile?.PreserveGlobs,
                CoveredByDependencyScopes: scopes.Count > 0));
        }

        return result;
    }

    private static IReadOnlyList<GateDependencyScope> ProfileDependencyScopes(
        string repositoryPath,
        IReadOnlyList<string>? lockfiles)
    {
        if (lockfiles is not { Count: > 0 }) return [];

        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lockfile in lockfiles)
        {
            if (!TryNormalizeLockfile(repositoryPath, lockfile, out var subdir, out var fileName))
                continue;
            if (!groups.TryGetValue(subdir, out var names))
            {
                names = [];
                groups[subdir] = names;
            }
            if (!names.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                names.Add(fileName);
        }

        return groups
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GateDependencyScope(
                group.Key,
                group.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<GateDependencyScope> ConventionalNodeDependencyScopes(
        string repositoryPath,
        string workingSubdir)
    {
        var installRoot = Path.GetFullPath(Path.Combine(
            repositoryPath,
            workingSubdir.Replace('/', Path.DirectorySeparatorChar)));
        var trackedFiles = TrackedRepositoryFiles.Read(repositoryPath);
        var lockfiles = new[] { "package-lock.json", "npm-shrinkwrap.json" }
            .Where(name => File.Exists(Path.Combine(installRoot, name)))
            .Where(name => TrackedRepositoryFiles.Contains(
                trackedFiles,
                string.IsNullOrWhiteSpace(workingSubdir) ? name : $"{workingSubdir}/{name}"))
            .ToArray();
        return lockfiles.Length == 0
            ? []
            : [new GateDependencyScope(workingSubdir, lockfiles)];
    }

    private static bool TryNormalizeLockfile(
        string repositoryPath,
        string? lockfile,
        out string workingSubdir,
        out string fileName)
    {
        workingSubdir = "";
        fileName = "";
        if (string.IsNullOrWhiteSpace(lockfile)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            var candidate = Path.GetFullPath(Path.Combine(
                root,
                lockfile.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            fileName = Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            var directory = Path.GetDirectoryName(candidate)!;
            var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            workingSubdir = relative == "." ? "" : relative;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeRepositorySubdir(string repositoryPath, string prefix)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            var candidate = Path.GetFullPath(Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            var relative = Path.GetRelativePath(root, candidate);
            var normalized = relative == "." ? "" : relative.Replace('\\', '/');
            var manifest = string.IsNullOrWhiteSpace(normalized)
                ? "package.json"
                : $"{normalized}/package.json";
            if (!File.Exists(Path.Combine(candidate, "package.json"))
                || !TrackedRepositoryFiles.Contains(
                    TrackedRepositoryFiles.Read(repositoryPath), manifest))
                return null;
            return normalized;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\bdotnet(?:\.exe)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetCommand();

    [GeneratedRegex(@"\bnpm(?:\.cmd)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmCommand();

    [GeneratedRegex("""\bnpm(?:\.cmd)?\s+--prefix(?:=|\s+)(?:["'](?<quoted>[^"']+)["']|(?<bare>[^\s;&|]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmPrefix();
}
