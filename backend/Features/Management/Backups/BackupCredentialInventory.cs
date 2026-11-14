namespace AgentStudio.Management;

/// <summary>Counts archive members that carry each credential class. A member may count in more than one class.</summary>
public static class BackupCredentialInventory
{
    public static void RejectPlaintextTotp(string member, string snapshotPath, string? secretKeyId)
    {
        if (!IsAccessState(member)) return;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(snapshotPath));
            if (!document.RootElement.TryGetProperty("Users", out var users)) return;
            foreach (var user in users.EnumerateArray())
            {
                if (!user.TryGetProperty("TotpSecret", out var factor)
                    || factor.ValueKind != System.Text.Json.JsonValueKind.String
                    || factor.GetString() is not { Length: > 0 } value) continue;
                if (!value.StartsWith("sealed:v1:", StringComparison.Ordinal))
                    throw new BackupException("backup-plaintext-factor", "An access-state file still holds a plaintext authenticator seed.");
                var envelope = System.Text.Json.JsonSerializer.Deserialize<AgentStudio.Secrets.SealedSecretVersion>(
                    Convert.FromBase64String(value["sealed:v1:".Length..]));
                if (envelope is null || envelope.Format != AgentStudio.Secrets.SealedSecretVersion.CurrentFormat
                    || !string.Equals(envelope.KeyId, secretKeyId, StringComparison.Ordinal))
                    throw new BackupException("backup-factor-key-mismatch", "An authenticator seed is not sealed under the configured secret store key.");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BackupException("backup-access-state-unreadable", "An access-state file cannot be checked: " + ex.GetType().Name + ".");
        }
        catch (FormatException ex)
        {
            throw new BackupException("backup-access-state-unreadable", "An access-state file cannot be checked: " + ex.GetType().Name + ".");
        }
    }

    private static readonly string[] CredentialClasses =
    [
        "access-state-hashes-and-sessions", "totp-seeds-sealed", "secret-store-values-sealed",
        "stack-environment", "backup-target-credentials", "home-ssh-keys",
        "provider-cli-and-git-logins", "github-app-git-credentials", "caddy-tls-and-acme",
        "host-certificates", "origin-pull-ca"
    ];

    public static IReadOnlyDictionary<string, int> Count(IEnumerable<BackupContentEntry> files)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var credentialClass in CredentialClasses) counts[credentialClass] = 0;
        foreach (var file in files)
        {
            foreach (var credentialClass in Classes(file.Path))
                counts[credentialClass] = counts.GetValueOrDefault(credentialClass) + 1;
        }
        return counts;
    }

    private static IEnumerable<string> Classes(string path)
    {
        if (IsAccessState(path))
        {
            yield return "access-state-hashes-and-sessions";
            yield return "totp-seeds-sealed";
        }
        if (path.StartsWith("workspace/.security/secrets/items/", StringComparison.Ordinal)
            && path.EndsWith(".sealed.json", StringComparison.Ordinal))
            yield return "secret-store-values-sealed";
        if (path == "stack/.env") yield return "stack-environment";
        if (path.StartsWith("stack/backup-target/", StringComparison.Ordinal))
            yield return "backup-target-credentials";
        if (path.StartsWith("home/.ssh/", StringComparison.Ordinal)) yield return "home-ssh-keys";
        if (path.Contains("/github-app/git-credentials", StringComparison.OrdinalIgnoreCase))
            yield return "github-app-git-credentials";
        if (path.StartsWith("home/", StringComparison.Ordinal)
            && (path.Contains("/.config/claude/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.config/codex/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.claude/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.codex/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.config/gh/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.config/gcloud/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.gemini/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/.git-credentials", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.aws/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.kube/", StringComparison.OrdinalIgnoreCase)))
            yield return "provider-cli-and-git-logins";
        if (path.StartsWith("caddy-data/", StringComparison.Ordinal)) yield return "caddy-tls-and-acme";
        if (path.StartsWith("host-certificates/", StringComparison.Ordinal)) yield return "host-certificates";
        if (path.StartsWith("origin-pull/", StringComparison.Ordinal)) yield return "origin-pull-ca";
    }

    private static bool IsAccessState(string path)
        => path.StartsWith("workspace/.security/access-state", StringComparison.Ordinal);
}
