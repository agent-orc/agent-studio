using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

/// <summary>Latest owner attestation for each caller instance and checkout or job slot.</summary>
public sealed partial class GitHubOperationReceiptStore(string path, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal)
    {
        "anonymous", "unknown", "github-app-installation", "github-app-private-key", "github-app-owner-authorization",
        "github-app-attempt-token", "github-actions-job-token", "https-pat", "https-oauth",
        "ssh-deploy-key", "ssh-account-key", "https-helper", "gh-environment", "gh-cli-store",
        "provisioning-grant", "registry-grant", "studio-service-identity",
    };

    public async Task<IReadOnlyList<GitHubCallerReceipt>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        // The writer atomically replaces this file. Allow rename while reading on Windows.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<GitHubCallerReceipt[]>(stream, Json, ct)
            ?? throw new JsonException("GitHub receipt inventory is empty.");
    }

    /// <returns>False when the receipt is invalid.</returns>
    public async Task<bool> RecordAsync(GitHubCallerReceipt receipt, CancellationToken ct)
    {
        if (!Valid(receipt)) return false;
        await _gate.WaitAsync(ct);
        try
        {
            var existing = (await ReadAsync(ct)).ToList();
            var index = existing.FindIndex(value =>
                (value.Caller, value.Host, value.Slot) == (receipt.Caller, receipt.Host, receipt.Slot));
            if (index < 0)
            {
                existing.Add(receipt);
            }
            else
            {
                var previous = existing[index];
                var current = receipt.ObservedAt > previous.ObservedAt ? receipt : previous;
                var other = ReferenceEquals(current, receipt) ? previous : receipt;
                var merged = current with
                {
                    LastRead = current.Fetch == other.Fetch ? Latest(current.LastRead, other.LastRead) : current.LastRead,
                    LastWrite = current.Push == other.Push ? Latest(current.LastWrite, other.LastWrite) : current.LastWrite,
                };
                if (merged == previous) return true;
                existing[index] = merged;
            }
            OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = OwnerOnlyAccess.CreateNewFile(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream, existing, Json, ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        }
        finally { _gate.Release(); }
    }

    internal static GitHubAppCoverageSnapshot Coverage(string broker, GitHubAppStatus app)
    {
        var registered = app.Repositories.Select(item => (item, target: GitHubOperationTarget.FromGitRemote(item.RepositoryUrl)))
            .Where(entry => entry.target is not null)
            .Select(entry => new GitHubRepositoryCoverageEntry(entry.target!.Repository, entry.item.State, entry.item.InstallationId));
        return new(broker, app.Connected, registered.Concat(app.AdoptedRepositories ?? [])
            .DistinctBy(entry => entry.Repository).OrderBy(entry => entry.Repository, StringComparer.Ordinal).ToArray());
    }

    private static GitHubOperationSuccess? Latest(GitHubOperationSuccess? current, GitHubOperationSuccess? previous)
        => current is null || previous is not null && previous.SucceededAt > current.SucceededAt ? previous : current;

    private bool Valid(GitHubCallerReceipt value)
        => GitHubCallerCatalogue.All.Any(caller => caller.Id == value.Caller &&
            (!caller.NoBuiltInGitHub || value.State is "unused" or "disabled"))
            && SafeAlias(value.Host) && SafeAlias(value.Slot) && SafeAlias(value.CheckoutOwner)
            && SafeAlias(value.Role) && SafeAlias(value.Binding)
            && value.State is "active" or "disabled" or "unused" or "unverified"
            && value.ReceiptId != Guid.Empty && value.ObservedAt != default && value.ObservedAt <= clock.GetUtcNow()
            && ValidTarget(value.Fetch) && ValidTarget(value.Push)
            && (value.ActiveCredential is null || ValidGrant(value.ActiveCredential))
            && ValidSuccess(value.LastRead, value.ObservedAt, workspaceWrite: false)
            && ValidSuccess(value.LastWrite, value.ObservedAt, workspaceWrite: value.Caller == "workspace-export");

    private static bool ValidTarget(GitHubOperationTarget? target)
        => target is null || target is { Transport: "https" or "ssh" or "api" or "ghcr", Repository: { } path }
            && GitHubRepositoryName.TryParse("https://" + path) is { } repository && repository.Canonical == path
            && CredentialRedactor.Redact(path) == path;

    private static bool ValidGrant(GitHubGrantReference? grant)
        => grant is { SourceClass: { } source } && Sources.Contains(source)
            && (source is "anonymous" or "unknown"
                ? grant.GrantId is null && grant.Fingerprint is null
                : grant.GrantId is not null || grant.Fingerprint is not null)
            && (grant.GrantId is null || Guid.TryParseExact(grant.GrantId, "D", out var id) && id != Guid.Empty
                || long.TryParse(grant.GrantId, out var number) && number > 0 && grant.GrantId == number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && (grant.Fingerprint is null || FingerprintPattern().IsMatch(grant.Fingerprint));

    private static bool ValidSuccess(GitHubOperationSuccess? proof, DateTimeOffset observedAt, bool workspaceWrite)
        => proof is null || proof.ReceiptId != Guid.Empty && proof.SucceededAt != default && proof.SucceededAt <= observedAt
            && ValidGrant(proof.Credential)
            && (workspaceWrite ? proof.CommitsPublished > 0 && ValidCommit(proof.PublishedCommit)
                : proof.CommitsPublished is null && (proof.PublishedCommit is null || ValidCommit(proof.PublishedCommit)));

    private static bool ValidCommit(string? value)
        => value is { Length: 40 or 64 } && value.All(ch => char.IsAsciiHexDigitLower(ch));

    private static bool SafeAlias(string? value)
        => value is { Length: > 0 and <= 80 } && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
            && CredentialRedactor.Redact(value) == value;

    [GeneratedRegex(@"\ASHA256:[A-Za-z0-9+/]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
}
