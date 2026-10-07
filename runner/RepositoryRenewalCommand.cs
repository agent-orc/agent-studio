using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRunner;

/// <summary>Explicit host maintenance entry point. Requests contain metadata only.</summary>
public static class RepositoryRenewalCommand
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<int> RunAsync(string kind, string requestPath, bool transportsDrained,
        TextReader input, TextWriter output, CancellationToken ct,
        string? protectedDirectoryOverride = null,
        Func<CancellationToken, Task<HttpClient?>>? administrationSession = null,
        HttpMessageHandler? discoveryHandler = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        ProtectedHostPath.EnsureOutsideRepository(requestPath);
        var protectedDirectory = protectedDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "agent-host", "repository-renewal");
        ProtectedHostPath.EnsureOutsideRepository(protectedDirectory);
        var json = await File.ReadAllTextAsync(requestPath, ct);
        if (kind == "discover-https")
        {
            var request = JsonSerializer.Deserialize<GitHubDiscoveryRequest>(json, RequestJson)
                ?? throw new ArgumentException("GitHub discovery request is empty.");
            using var discovery = new GitHubCredentialDiscovery(
                new StdinInspectionSession(input, request), discoveryHandler);
            var metadata = await discovery.DiscoverAsync(request.CredentialId,
                request.RepositoryPurpose, request.Origin, request.RequiresPush, ct);
            await output.WriteLineAsync(JsonSerializer.Serialize(metadata));
            return metadata.FetchGrant is not null
                && (!metadata.RequiresPush || metadata.PushGrant is not null) ? 0 : 3;
        }
        if (kind == "https")
        {
            var request = JsonSerializer.Deserialize<RepositoryHttpsRotationRequest>(json, RequestJson)
                ?? throw new ArgumentException("HTTPS renewal request is empty.");
            var session = new StdinTokenSession(input);
            var host = new ProtectedGitCredentialHost(protectedDirectory, session,
                (_, _) => Task.FromResult(transportsDrained),
                (r, _) => Task.FromResult(r.ExpectedGeneration));
            var renewal = new RepositoryHttpsRenewal(host,
                new PlatformRepositoryGitProof(_ => null, protectedDirectory, host.StoreForGeneration),
                new FileRepositoryHttpsRotationJournal(protectedDirectory));
            var receipt = await renewal.RotateAsync(request, ct);
            await output.WriteLineAsync($"status={receipt.Status} operation={receipt.OperationId} "
                + $"fetch={receipt.FetchVerified} push={receipt.PushVerified}");
            return receipt.Completed ? 0 : 3;
        }
        if (kind != "deploy-key") throw new ArgumentException("Unknown repository renewal kind.");
        var keyRequest = JsonSerializer.Deserialize<RepositoryRotationRequest>(json, RequestJson)
            ?? throw new ArgumentException("Deploy-key renewal request is empty.");
        var keyHost = new LocalRepositoryKeyHost(protectedDirectory,
            (_, _) => Task.FromResult(transportsDrained),
            (r, _) => Task.FromResult(r.ExpectedGeneration));
        IRepositoryKeyAdministration? administration = null;
        var sshHost = RunnerOptions.Env("RUNNER_GITHUB_ADMIN_SSH_HOST");
        using var sessionClient = administrationSession is not null
            ? await administrationSession(ct)
            : string.IsNullOrWhiteSpace(sshHost)
                && (RunnerOptions.Env("RUNNER_WORKSTATION") is "1" or "true")
                ? await OpenWorkstationAdministrationSessionAsync(ct) : null;
        if (!string.IsNullOrWhiteSpace(sshHost) && administrationSession is null)
            administration = new WorkstationGitHubAdministration(sshHost);
        else if (sessionClient is not null)
            administration = new GitHubDeployKeyAdministration(sessionClient);
        if (administration is null)
        {
            await output.WriteLineAsync("status=administrator-action-required: authorize GitHub CLI on the provisioning workstation");
            return 3;
        }
        var rotation = new RepositoryAccessRenewal(keyHost,
            administration,
            new PlatformRepositoryGitProof(generation =>
                generation.StartsWith("key-", StringComparison.Ordinal)
                    ? Path.Combine(protectedDirectory, "deploy-" + generation[4..]) : null,
                protectedDirectory),
            new FileRepositoryRotationJournal(protectedDirectory));
        var result = await rotation.RotateAsync(keyRequest, ct);
        await output.WriteLineAsync($"status={result.Status} operation={result.OperationId} "
            + $"keyId={result.NewGitHubKeyId?.ToString() ?? "unknown"} "
            + $"fingerprint={result.Candidate?.Fingerprint ?? "unknown"} "
            + $"fetch={result.FetchVerified} push={result.PushVerified}");
        return result.Completed ? 0 : 3;
    }

    public static async Task<int> RunAdminAsync(TextReader input, TextWriter output,
        CancellationToken ct, Func<CancellationToken, Task<HttpClient?>>? session = null)
    {
        if (session is null && RunnerOptions.Env("RUNNER_WORKSTATION") is not ("1" or "true"))
        {
            await output.WriteAsync(JsonSerializer.Serialize(new WorkstationAdminReply("authority-required", null)));
            return 3;
        }
        var request = JsonSerializer.Deserialize<WorkstationAdminRequest>(await input.ReadToEndAsync(ct), RequestJson)
            ?? throw new ArgumentException("Administration request is empty.");
        if (session is null && !WorkstationRepositoryAllowed(
                RunnerOptions.Env("RUNNER_GITHUB_ADMIN_REPOSITORIES"),
                request.Owner, request.Repository))
        {
            await output.WriteAsync(JsonSerializer.Serialize(new WorkstationAdminReply("authority-required", null)));
            return 3;
        }
        using var client = await (session ?? OpenWorkstationAdministrationSessionAsync)(ct);
        if (client is null)
        {
            await output.WriteAsync(JsonSerializer.Serialize(new WorkstationAdminReply("authority-required", null)));
            return 3;
        }
        var administration = new GitHubDeployKeyAdministration(client);
        var target = new RepositoryRotationRequest(request.OperationId, request.HostId, "admin-only",
            request.Owner, request.Repository, "product",
            $"git@github.com:{request.Owner}/{request.Repository}.git", request.RequiresPush,
            null, "workstation-session");
        try
        {
            RegisteredRepositoryKey? key = request.Action switch
            {
                "find" when request.Fingerprint is not null
                    => await administration.FindByFingerprintAsync(target, request.Fingerprint, ct),
                "register" when request.PublicKey is not null && request.Fingerprint is not null
                    && GitHubDeployKeyAdministration.Fingerprint(request.PublicKey) == request.Fingerprint
                    => await administration.RegisterAsync(target,
                        new("admin-only", request.PublicKey, request.Fingerprint, "host-only"), ct),
                "delete" when request.KeyId is > 0
                    => await DeleteAsync(administration, target, request.KeyId.Value, ct),
                _ => throw new ArgumentException("Unsupported administration action.")
            };
            await output.WriteAsync(JsonSerializer.Serialize(
                new WorkstationAdminReply(key is null ? "missing" : "ok", key)));
            return key is null ? 3 : 0;
        }
        catch (RepositoryAdminAuthorityException)
        {
            await output.WriteAsync(JsonSerializer.Serialize(new WorkstationAdminReply("authority-required", null)));
            return 3;
        }
    }

    private static async Task<RegisteredRepositoryKey> DeleteAsync(
        GitHubDeployKeyAdministration administration, RepositoryRotationRequest request,
        long keyId, CancellationToken ct)
    {
        await administration.DeleteAsync(request, keyId, ct);
        return new RegisteredRepositoryKey(keyId, "deleted", true);
    }

    internal static bool WorkstationRepositoryAllowed(string allowlist, string owner, string repository)
        => allowlist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(owner + "/" + repository, StringComparer.Ordinal);

    private static async Task<HttpClient?> OpenWorkstationAdministrationSessionAsync(CancellationToken ct)
    {
        ProcessResult auth;
        try
        {
            auth = await ProcessRunner.RunAsync("gh", ["auth", "token"],
                environment: new Dictionary<string, string?> {
                    ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null, ["GH_HOST"] = "github.com"
                }, ct: ct);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (!auth.Success || string.IsNullOrWhiteSpace(auth.StdOut)) return null;
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) {
            BaseAddress = new Uri("https://api.github.com/")
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.StdOut.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-host-repository-renewal");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed class StdinTokenSession(TextReader input) : IProtectedGitHubTokenSession
    {
        public async Task<(string Username, string Bearer)?> ObtainAsync(
            RepositoryHttpsRotationRequest request, CancellationToken ct)
        {
            var token = (await input.ReadToEndAsync(ct)).Trim();
            return token.Length == 0 ? null : ("x-access-token", token);
        }
    }

    private sealed class StdinInspectionSession(TextReader input, GitHubDiscoveryRequest request)
        : IGitHubCredentialInspectorSession
    {
        public async Task<(string Bearer, string? IssuerSubtype, DateTimeOffset? IssuerExpiresAt)?>
            OpenAsync(string credentialId, CancellationToken ct)
        {
            if (credentialId != request.CredentialId) return null;
            var token = (await input.ReadToEndAsync(ct)).Trim();
            return token.Length == 0 ? null : (token, request.IssuerSubtype, request.IssuerExpiresAt);
        }
    }
}

public sealed record GitHubDiscoveryRequest(string CredentialId, string RepositoryPurpose,
    string Origin, bool RequiresPush, string? IssuerSubtype, DateTimeOffset? IssuerExpiresAt);
