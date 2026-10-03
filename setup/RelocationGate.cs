using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Setup;

/// <summary>
/// Certifies an authority restored by the Task Server full-backup workflow.
/// The relocation journey never bootstraps a fresh authority over restored data.
/// </summary>
internal static class RelocationGate
{
    internal static async Task<InstallationManifest> VerifyAsync(
        string? sourceManifestPath, string? recoverySetPath, string destinationRoot, bool authorityFrozen)
    {
        if (!authorityFrozen)
            throw new InvalidOperationException("Freeze the source authority in Maintenance and resolve its attempts before relocation.");
        if (string.IsNullOrWhiteSpace(sourceManifestPath) || string.IsNullOrWhiteSpace(recoverySetPath))
            throw new InvalidOperationException(
                "Relocation requires --source-manifest and --recovery-checkpoint pointing to the verified full backup set.");
        if (Path.GetFullPath(sourceManifestPath) == Path.GetFullPath(
                Path.Combine(destinationRoot, InstallationManifest.FileName)))
            throw new InvalidOperationException("The source manifest must be retained separately from the restored destination manifest.");
        var source = JsonSerializer.Deserialize<InstallationManifest>(
            await File.ReadAllTextAsync(sourceManifestPath))
            ?? throw new InvalidDataException("Source installation manifest is invalid.");
        var restored = await ManifestStore.ReadAsync(destinationRoot)
            ?? throw new InvalidOperationException(
                "No restored installation manifest exists at the destination. Restore into an empty target first; a new install would create another authority.");
        if (source.InstallationId != restored.InstallationId
            || source.ReleaseVersion != restored.ReleaseVersion
            || restored.Phase != InstallationManifest.PhaseComplete
            || !source.Principals.Order(StringComparer.Ordinal).SequenceEqual(
                restored.Principals.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || source.ProjectOrigin != restored.ProjectOrigin)
            throw new InvalidDataException("Restored installation identity, release, principals or project origin differ from the frozen source.");

        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recoverySetPath, "inventory.json")));
        using var complete = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recoverySetPath, "complete.json")));
        var expectedHash = inventory.RootElement.GetProperty("setSha256").GetString();
        if (string.IsNullOrWhiteSpace(expectedHash)
            || !string.Equals(expectedHash, complete.RootElement.GetProperty("setSha256").GetString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Full recovery set inventory and completion hashes differ.");
        var entries = inventory.RootElement.GetProperty("files").EnumerateArray().ToArray();
        var listed = new List<string>();
        var content = new StringBuilder();
        foreach (var entry in entries)
        {
            var relative = entry.GetProperty("relativePath").GetString()
                ?? throw new InvalidDataException("Recovery set contains a pathless file.");
            var root = Path.GetFullPath(recoverySetPath);
            var file = Path.GetFullPath(Path.Combine(root, relative));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(file)
                || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Recovery set file is missing or escapes its root: {relative}");
            var size = entry.GetProperty("size").GetInt64();
            if (new FileInfo(file).Length != size)
                throw new InvalidDataException($"Recovery set file has the wrong size: {relative}");
            await using var stream = File.OpenRead(file);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!string.Equals(hash, entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Recovery set file failed SHA-256 verification: {relative}");
            listed.Add(relative.Replace('\\', '/'));
            content.Append(relative.Replace('\\', '/')).Append(':').Append(size).Append(':')
                .Append(hash.ToLowerInvariant()).Append('\n');
        }
        var actualFiles = Directory.EnumerateFiles(recoverySetPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is not ("inventory.json" or "complete.json"))
            .Select(path => Path.GetRelativePath(recoverySetPath, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        if (!listed.SequenceEqual(actualFiles, StringComparer.Ordinal)
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))),
                expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Full recovery set inventory does not match its files or set hash.");
        var receiptPath = Path.TrimEndingDirectorySeparator(recoverySetPath) + ".rehearsal.json";
        if (!File.Exists(receiptPath))
            throw new InvalidOperationException(
                "Empty-target restore rehearsal receipt is missing. Verify and rehearse the full backup set before relocation.");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
        var proof = receipt.RootElement;
        if (proof.GetProperty("backupId").GetString() != Path.GetFileName(Path.TrimEndingDirectorySeparator(recoverySetPath))
            || proof.GetProperty("setSha256").GetString() != expectedHash
            || proof.GetProperty("installationId").GetString() != source.InstallationId
            || !proof.GetProperty("verified").GetBoolean()
            || !proof.GetProperty("restoredIntoEmptyTarget").GetBoolean())
            throw new InvalidDataException("The recovery rehearsal receipt does not match this set and installation.");
        return restored;
    }

    internal static InstallationManifest RelocatedManifest(InstallationManifest restored, string target)
        => restored with
        {
            Journey = "relocate-authority",
            Mode = "control-plane",
            Target = target,
            UpdatedUtc = DateTime.UtcNow,
        };

    internal static async Task RestoreAsync(string authorityUrl, string tokenFile, string recoverySetPath,
        HttpClient? client = null)
    {
        ProductSetup.ValidateUpstream(authorityUrl);
        SetupSecrets.RequireProtected(tokenFile, "Authority management token file");
        var backupId = Path.GetFileName(Path.TrimEndingDirectorySeparator(recoverySetPath));
        using var localInventory = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(recoverySetPath, "inventory.json")));
        var localSetHash = localInventory.RootElement.GetProperty("setSha256").GetString();
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(15) } : null;
        var http = client ?? owned!;
        var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
        if (token.Length == 0) throw new InvalidDataException("Authority management token is empty.");
        var endpoint = new Uri(new Uri(authorityUrl),
            $"/api/v1/management/backups/full/{Uri.EscapeDataString(backupId)}/");
        async Task<JsonDocument> PostAsync(string action)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, action));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        using var verification = await PostAsync("verify");
        if (!verification.RootElement.GetProperty("verified").GetBoolean()
            || verification.RootElement.GetProperty("backupId").GetString() != backupId
            || !string.Equals(verification.RootElement.GetProperty("summary")
                    .GetProperty("setSha256").GetString(), localSetHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Target Task Server did not verify the selected full backup set.");
        using var restore = await PostAsync("restore");
        if (!restore.RootElement.GetProperty("restored").GetBoolean()
            || restore.RootElement.GetProperty("backupId").GetString() != backupId)
            throw new InvalidDataException("Target Task Server did not restore the selected full backup set.");
    }
}
