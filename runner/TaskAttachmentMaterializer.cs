using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Places continuation attachments beside the source tree before the CLI starts.</summary>
internal static class TaskAttachmentMaterializer
{
    private static readonly Regex Reference = new(
        @"(?<![A-Za-z0-9._/-])attachments/(?<name>[A-Za-z0-9][A-Za-z0-9._-]{0,254})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static async Task<IReadOnlyList<string>> MaterializeAsync(
        TaskServerClient client, string taskKey,
        string? continuationPrompt, string workspacePath, long maxBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(continuationPrompt)) return [];
        var names = Reference.Matches(continuationPrompt)
            .Select(match => match.Groups["name"].Value.TrimEnd('.'))
            .Where(name => name is not ("" or "." or ".."))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0) return [];

        var folder = Path.Combine(workspacePath, "attachments");
        Directory.CreateDirectory(folder);
        var delivered = new List<string>(names.Length);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(folder, name);
            if (File.Exists(destination))
                throw new IOException($"Task attachment destination already exists: {destination}");
            var bytes = await client.ReadTaskAttachmentAsync(taskKey, name, maxBytes, ct);
            await ExcludeFromGitAsync(workspacePath, $"attachments/{name}", ct);
            await File.WriteAllBytesAsync(destination, bytes, ct);
            delivered.Add(destination);
        }
        return delivered;
    }

    private static async Task ExcludeFromGitAsync(string workspacePath, string relativePath, CancellationToken ct)
    {
        var dotGit = Path.Combine(workspacePath, ".git");
        var gitDirectory = Directory.Exists(dotGit)
            ? dotGit
            : Path.GetFullPath(Path.Combine(workspacePath,
                File.ReadAllText(dotGit).Trim()["gitdir: ".Length..]));
        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        var commonDirectory = File.Exists(commonDirectoryFile)
            ? Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonDirectoryFile).Trim()))
            : gitDirectory;
        var exclude = Path.Combine(commonDirectory, "info", "exclude");
        await GitWorkspace.GitMetadataGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            var line = "/" + relativePath + "\n";
            var existing = File.Exists(exclude) ? await File.ReadAllTextAsync(exclude, ct) : string.Empty;
            if (!existing.Split('\n').Contains(line.TrimEnd('\n'), StringComparer.Ordinal))
                await File.AppendAllTextAsync(exclude, "\n" + line, ct);
        }
        finally
        {
            GitWorkspace.GitMetadataGate.Release();
        }
    }
}
