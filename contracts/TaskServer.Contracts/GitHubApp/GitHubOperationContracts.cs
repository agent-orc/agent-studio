using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts.GitHubApp;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitHubOperationTarget(string Transport, string Repository)
{
    public static GitHubOperationTarget? FromGitRemote(string? remote)
    {
        if (GitHubRepositoryName.TryParse(remote) is not { } repository) return null;
        var transport = remote!.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "https" : "ssh";
        return new(transport, repository.Canonical);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitHubGrantReference(string SourceClass, string? GrantId = null, string? Fingerprint = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitHubOperationSuccess(DateTimeOffset SucceededAt, Guid ReceiptId,
    GitHubGrantReference Credential, int? CommitsPublished = null, string? PublishedCommit = null);

/// <summary>Owner-attested metadata for one caller instance and checkout or job slot.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitHubCallerReceipt(string Caller, string Host, string Slot, string CheckoutOwner,
    string Role, string Binding, string State, DateTimeOffset ObservedAt, Guid ReceiptId,
    GitHubOperationTarget? Fetch, GitHubOperationTarget? Push, GitHubGrantReference? ActiveCredential,
    GitHubOperationSuccess? LastRead, GitHubOperationSuccess? LastWrite);

public sealed record GitHubCallerDefinition(string Id, string Label, string Source, string Dependency, bool NoBuiltInGitHub = false);

public sealed record GitHubWriteObservation(string Caller, GitHubOperationTarget Target,
    DateTimeOffset SucceededAt, GitHubOperationTarget? Fetch = null, int? CommitsToPublish = null);

public sealed record GitHubOperationManifest(IReadOnlyList<GitHubCallerDefinition> Callers,
    IReadOnlyList<GitHubCallerReceipt> Receipts, IReadOnlyList<GitHubWriteObservation> Writes,
    IReadOnlyDictionary<string, string>? ServiceStates,
    IReadOnlyList<GitHubAppCoverageSnapshot> AppCoverage);

public sealed record GitHubAppCoverageSnapshot(string Broker, bool Connected,
    IReadOnlyList<GitHubRepositoryCoverageEntry> Repositories);

public sealed record GitHubRepositoryCoverageEntry(string Repository, string State, long? InstallationId);

/// <summary>Git porcelain reports the destination of the actual update, including push URL rewriting.</summary>
public static class GitHubPushPorcelain
{
    public static bool TryFindUpdate(string output, string destinationRef, char[] updateFlags,
        out IReadOnlyList<GitHubOperationTarget> targets)
    {
        var destinations = new HashSet<GitHubOperationTarget>();
        GitHubOperationTarget? remote = null;
        var updated = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("To ", StringComparison.Ordinal))
            {
                remote = GitHubOperationTarget.FromGitRemote(line[3..].TrimEnd());
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 3 || fields[0].Length != 1 || !updateFlags.Contains(fields[0][0])) continue;
            if (!fields[1].EndsWith(":" + destinationRef, StringComparison.Ordinal)) continue;
            updated = true;
            if (remote is not null) destinations.Add(remote);
        }
        targets = destinations.ToArray();
        return updated;
    }
}
