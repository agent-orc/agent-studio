using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Retention;

public sealed record FullBackupFile(string RelativePath, long Size, string Sha256);

public sealed record FullBackupInventory
{
    public int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset CreatedAt { get; init; }
    public required string WorkspaceName { get; init; }
    public required IReadOnlyList<FullBackupFile> Files { get; init; }
    public int TaskCount { get; init; }
    public int ColdPayloadCount { get; init; }
    public long TotalBytes { get; init; }
    public required string SetSha256 { get; init; }
}

public sealed record FullBackupComplete(int SchemaVersion, DateTimeOffset CompletedAt, string SetSha256);

public sealed class FileTreeFullBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> CreateAsync(
        string workspacePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        workspacePath = Path.GetFullPath(workspacePath);
        outputPath = Path.GetFullPath(outputPath);
        if (IsInside(outputPath, workspacePath))
            throw new ArgumentException("Full backup output must be outside the workspace repository.", nameof(outputPath));
        var backup = Path.Combine(outputPath, "full", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(backup);
        try
        {
            var bundle = Path.Combine(backup, "workspace.bundle");
            await RunGitAsync(workspacePath, ["bundle", "create", bundle, "--all"], cancellationToken);

            var untrackedRoot = Path.Combine(backup, "untracked");
            var untracked = (await RunGitAsync(workspacePath, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var relative in untracked)
                CopyFileSafe(workspacePath, relative, untrackedRoot, relative);

            var coldRoot = Path.Combine(backup, "cold");
            var coldSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pointerPath in Directory.EnumerateFiles(workspacePath, "archive-manifest.json", SearchOption.AllDirectories))
            {
                var pointer = JsonSerializer.Deserialize<ArchivePointer>(await File.ReadAllTextAsync(pointerPath, cancellationToken), JsonOptions);
                if (pointer is null) continue;
                foreach (var transition in pointer.Archives)
                {
                    coldSources.Add(Path.GetFullPath(transition.ManifestPath));
                    coldSources.Add(Path.GetFullPath(transition.PayloadPath));
                }
            }
            foreach (var source in coldSources)
            {
                if (!File.Exists(source))
                    throw new InvalidDataException($"Referenced cold archive file is missing: {source}");
                var relative = ArchiveRelativePath(source);
                CopyFileSafe(Path.GetDirectoryName(source)!, Path.GetFileName(source), coldRoot, relative);
            }

            var files = await InventoryAsync(backup, cancellationToken);
            var setHash = SetHash(files);
            var inventory = new FullBackupInventory
            {
                CreatedAt = DateTimeOffset.UtcNow,
                WorkspaceName = Path.GetFileName(workspacePath),
                Files = files,
                TaskCount = Directory.Exists(Path.Combine(workspacePath, "projects"))
                    ? Directory.EnumerateFiles(Path.Combine(workspacePath, "projects"), "task.json", SearchOption.AllDirectories).Count()
                    : 0,
                ColdPayloadCount = files.Count(file => file.RelativePath.StartsWith("cold/", StringComparison.Ordinal)
                                                      && file.RelativePath.EndsWith("payload.zip", StringComparison.Ordinal)),
                TotalBytes = files.Sum(file => file.Size),
                SetSha256 = setHash,
            };
            await File.WriteAllTextAsync(Path.Combine(backup, "inventory.json"),
                JsonSerializer.Serialize(inventory, JsonOptions) + Environment.NewLine, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(backup, "complete.json"),
                JsonSerializer.Serialize(new FullBackupComplete(1, DateTimeOffset.UtcNow, setHash), JsonOptions) + Environment.NewLine,
                cancellationToken);
            return backup;
        }
        catch
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            throw;
        }
    }

    public async Task<FullBackupInventory> VerifyAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        backupPath = Path.GetFullPath(backupPath);
        var completePath = Path.Combine(backupPath, "complete.json");
        if (!File.Exists(completePath)) throw new InvalidDataException("Backup is incomplete: complete.json is missing.");
        var inventory = JsonSerializer.Deserialize<FullBackupInventory>(
                            await File.ReadAllTextAsync(Path.Combine(backupPath, "inventory.json"), cancellationToken), JsonOptions)
                        ?? throw new InvalidDataException("Backup inventory is invalid.");
        var complete = JsonSerializer.Deserialize<FullBackupComplete>(
                           await File.ReadAllTextAsync(completePath, cancellationToken), JsonOptions)
                       ?? throw new InvalidDataException("Backup completion marker is invalid.");
        var actual = await InventoryAsync(backupPath, cancellationToken);
        if (!actual.SequenceEqual(inventory.Files) || SetHash(actual) != inventory.SetSha256
            || complete.SetSha256 != inventory.SetSha256)
            throw new InvalidDataException("Backup inventory hash or file set does not match.");
        return inventory;
    }

    public async Task RestoreAsync(
        string backupPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await VerifyAsync(backupPath, cancellationToken);
        destinationPath = Path.GetFullPath(destinationPath);
        if (Directory.Exists(destinationPath) && Directory.EnumerateFileSystemEntries(destinationPath).Any())
            throw new InvalidOperationException("Full backup restore destination must be empty.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await RunGitAsync(Path.GetDirectoryName(destinationPath)!,
            ["clone", Path.Combine(Path.GetFullPath(backupPath), "workspace.bundle"), destinationPath], cancellationToken);

        var untracked = Path.Combine(backupPath, "untracked");
        if (Directory.Exists(untracked)) CopyTree(untracked, destinationPath);
        var coldSource = Path.Combine(backupPath, "cold");
        var coldDestination = Path.Combine(Directory.GetParent(destinationPath)!.FullName, "agent-taskboard-archive");
        if (Directory.Exists(coldSource)) CopyTree(coldSource, coldDestination);
        await RewritePointersAsync(destinationPath, coldSource, coldDestination, cancellationToken);
    }

    private static async Task RewritePointersAsync(
        string workspace,
        string oldColdRoot,
        string newColdRoot,
        CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(workspace, "archive-manifest.json", SearchOption.AllDirectories))
        {
            var pointer = JsonSerializer.Deserialize<ArchivePointer>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            if (pointer is null) continue;
            var rewritten = pointer.Archives.Select(transition => transition with
            {
                ManifestPath = ResolveRestoredColdPath(transition.ManifestPath, oldColdRoot, newColdRoot),
                PayloadPath = ResolveRestoredColdPath(transition.PayloadPath, oldColdRoot, newColdRoot),
            }).ToList();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(pointer with { Archives = rewritten }, JsonOptions) + Environment.NewLine,
                cancellationToken);
        }
    }

    private static string ResolveRestoredColdPath(
        string original,
        string oldColdRoot,
        string newColdRoot)
    {
        var relative = ArchiveRelativePath(original);
        var backedUp = Path.Combine(oldColdRoot, relative);
        if (!File.Exists(backedUp))
            throw new InvalidDataException($"Cold archive reference was not included in backup: {original}");
        return Path.Combine(newColdRoot, relative);
    }

    private static async Task<List<FullBackupFile>> InventoryAsync(string root, CancellationToken cancellationToken)
    {
        var result = new List<FullBackupFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith("inventory.json", StringComparison.OrdinalIgnoreCase)
                                    && !path.EndsWith("complete.json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            result.Add(new FullBackupFile(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length, hash));
        }
        return result;
    }

    private static string SetHash(IEnumerable<FullBackupFile> files)
    {
        var content = string.Concat(files.Select(file => $"{file.RelativePath}:{file.Size}:{file.Sha256}\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static void CopyFileSafe(string sourceRoot, string sourceRelative, string targetRoot, string targetRelative)
    {
        var source = Path.GetFullPath(Path.Combine(sourceRoot, sourceRelative));
        var target = Path.GetFullPath(Path.Combine(targetRoot, targetRelative));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: false);
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string ArchiveRelativePath(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindLastIndex(parts, part => string.Equals(part, "agent-taskboard-archive", StringComparison.OrdinalIgnoreCase));
        var start = marker >= 0 ? marker + 1 : Math.Max(0, parts.Length - 4);
        return Path.Combine(parts[start..]);
    }

    private static bool IsInside(string path, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var value = Path.GetFullPath(path);
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} failed: {error.Trim()}");
        return output;
    }
}
