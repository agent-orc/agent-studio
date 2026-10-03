using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentRunner;

/// <summary>Runs on the target host. The private key never crosses this boundary.</summary>
public sealed class LocalRepositoryKeyHost(
    string protectedDirectory,
    Func<RepositoryRotationRequest, CancellationToken, Task<bool>> transportsDrained,
    Func<RepositoryRotationRequest, CancellationToken, Task<string>> activeGeneration)
    : IRepositoryKeyHost
{
    public Task<string> ActiveGenerationAsync(RepositoryRotationRequest request, CancellationToken ct)
        => activeGeneration(request, ct);
    private string KeyPath(RepositoryRotationRequest request)
    {
        var name = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(request.HostId + "/" + request.Owner + "/"
                + request.Repository + "/" + request.OperationId))).ToLowerInvariant()[..32];
        return Path.Combine(protectedDirectory, "deploy-" + name);
    }

    public async Task<RepositoryKeyCandidate> GenerateOrLoadAsync(
        RepositoryRotationRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Protected deploy-key generation requires the Linux host adapter.");
        ProtectedHostPath.EnsureOutsideRepository(protectedDirectory);
        Directory.CreateDirectory(protectedDirectory);
        File.SetUnixFileMode(protectedDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = KeyPath(request);
        await using var operationLock = await HostFileOperationLock.AcquireAsync(path + ".lock", ct);
        if (!File.Exists(path) && !File.Exists(path + ".pub"))
        {
            var created = await ProcessRunner.RunAsync("ssh-keygen",
                ["-q", "-t", "ed25519", "-N", "", "-C", "agent-studio:" + request.OperationId,
                    "-f", path], ct: ct);
            if (!created.Success) throw new InvalidOperationException("Host key generation failed.");
        }
        if (!File.Exists(path) || !File.Exists(path + ".pub"))
            throw new InvalidOperationException("Candidate key pair is incomplete; administrator repair is required.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var publicKey = (await File.ReadAllTextAsync(path + ".pub", ct)).Trim();
        return new("key-" + Path.GetFileName(path)[7..], publicKey,
            GitHubDeployKeyAdministration.Fingerprint(publicKey), path);
    }

    public Task<bool> DrainTransportsAsync(RepositoryRotationRequest request, CancellationToken ct)
        => transportsDrained(request, ct);

    public async Task SwitchAsync(
        RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Protected SSH transport switching requires Linux.");
        var expectedPath = KeyPath(request);
        if (candidate.HostLocalRef != expectedPath || !File.Exists(expectedPath))
            throw new InvalidOperationException("Candidate is not the operation-owned host key.");
        if (expectedPath.Any(char.IsWhiteSpace) || expectedPath.Contains('"'))
            throw new InvalidOperationException("Protected SSH key path must not contain spaces or quotes.");
        var alias = "agent-studio-key-" + Path.GetFileName(expectedPath)[7..];
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Directory.CreateDirectory(sshDirectory);
        File.SetUnixFileMode(sshDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var configPath = Path.Combine(sshDirectory, "config");
        var existing = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, ct) : "";
        var marker = "# agent-studio-repository-key " + alias;
        var block = $"{marker}\nHost {alias}\n  HostName github.com\n  User git\n"
            + $"  IdentityFile {expectedPath}\n  IdentitiesOnly yes\n  IdentityAgent none\n";
        if (!existing.Contains(marker, StringComparison.Ordinal))
        {
            var temporary = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, block + existing, ct);
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, configPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var from = $"https://github.com/{request.Owner}/{request.Repository}.git";
        var to = $"git@{alias}:{request.Owner}/{request.Repository}.git";
        var rewrite = await ProcessRunner.RunAsync("git",
            ["config", "--global", "--replace-all", $"url.{to}.insteadOf", from], ct: ct);
        if (!rewrite.Success)
            throw new InvalidOperationException("Exact repository transport rewrite failed.");
        var shortForm = await ProcessRunner.RunAsync("git",
            ["config", "--global", "--add", $"url.{to}.insteadOf", from[..^4]], ct: ct);
        if (!shortForm.Success)
            throw new InvalidOperationException("Second repository URL form could not be configured.");
    }

    public Task RetireAsync(RepositoryRotationRequest request, CancellationToken ct)
    {
        if (request.OldGitHubKeyId is null) return Task.CompletedTask;
        var old = request.OldHostLocalRef;
        if (old is null || !Path.GetFullPath(old).StartsWith(
                Path.GetFullPath(protectedDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.GetFullPath(old) == KeyPath(request))
            throw new InvalidOperationException("Old host key path requires guided retirement.");
        if (File.Exists(old)) File.Delete(old);
        if (File.Exists(old + ".pub")) File.Delete(old + ".pub");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Uses an HttpClient already authenticated by a protected workstation administration
/// session. No bearer is accepted or stored in a task request or receipt.
/// </summary>
public sealed class GitHubDeployKeyAdministration : IRepositoryKeyAdministration
{
    private readonly HttpClient _session;

    public GitHubDeployKeyAdministration(HttpClient session)
    {
        if (session.BaseAddress?.AbsoluteUri != "https://api.github.com/")
            throw new ArgumentException("Protected GitHub administration session must target api.github.com.");
        _session = session;
    }
    public static string Fingerprint(string publicKey)
    {
        var words = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] != "ssh-ed25519")
            throw new ArgumentException("An ed25519 public key is required.");
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(words[1])))
            .TrimEnd('=');
    }

    private static string PathFor(RepositoryRotationRequest request)
    {
        if (!SafeSlug(request.Owner) || !SafeSlug(request.Repository))
            throw new ArgumentException("Repository owner and name must be plain GitHub slugs.");
        return $"repos/{request.Owner}/{request.Repository}/keys";
    }

    private static bool SafeSlug(string value) => value.Length is > 0 and <= 100
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    public async Task<RegisteredRepositoryKey?> FindByFingerprintAsync(
        RepositoryRotationRequest request, string fingerprint, CancellationToken ct)
    {
        for (var page = 1; page <= 20; page++)
        {
            using var response = await _session.GetAsync(PathFor(request) + $"?per_page=100&page={page}", ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new RepositoryAdminAuthorityException();
            response.EnsureSuccessStatusCode();
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var entries = body.RootElement.EnumerateArray().ToArray();
            foreach (var entry in entries)
            {
                var key = entry.GetProperty("key").GetString();
                if (key is not null && key.StartsWith("ssh-ed25519 ", StringComparison.Ordinal)
                    && Fingerprint(key) == fingerprint)
                    return new(entry.GetProperty("id").GetInt64(), fingerprint,
                        entry.GetProperty("read_only").GetBoolean());
            }
            if (entries.Length < 100) break;
        }
        return null;
    }

    public async Task<RegisteredRepositoryKey?> RegisterAsync(
        RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct)
    {
        using var response = await _session.PostAsJsonAsync(PathFor(request), new {
            title = "agent-studio:" + request.HostId + ":" + request.OperationId,
            key = candidate.PublicKey, read_only = !request.RequiresPush
        }, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            or HttpStatusCode.UnprocessableEntity)
            return null;
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return new(body.RootElement.GetProperty("id").GetInt64(), candidate.Fingerprint,
            body.RootElement.GetProperty("read_only").GetBoolean());
    }

    public async Task DeleteAsync(RepositoryRotationRequest request, long keyId, CancellationToken ct)
    {
        using var response = await _session.DeleteAsync(PathFor(request) + "/" + keyId, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
}

public sealed class RepositoryAdminAuthorityException()
    : Exception("Repository administrator authorization is required for deploy-key registration.");

/// <summary>Durable host-local receipt, without a private key or administration bearer.</summary>
public sealed class FileRepositoryRotationJournal(string protectedDirectory) : IRepositoryRotationJournal
{
    private string PathFor(string operationId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant();
        return Path.Combine(protectedDirectory, "rotation-" + digest + ".json");
    }

    public async Task<RepositoryRotationReceipt?> LoadAsync(string operationId, CancellationToken ct)
    {
        var path = PathFor(operationId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var receipt = await JsonSerializer.DeserializeAsync<RepositoryRotationReceipt>(stream, cancellationToken: ct);
        if (receipt?.OperationId != operationId)
            throw new InvalidOperationException("Rotation journal identity mismatch.");
        return receipt;
    }

    public async Task SaveAsync(RepositoryRotationReceipt receipt, CancellationToken ct)
    {
        Directory.CreateDirectory(protectedDirectory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(protectedDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = PathFor(receipt.OperationId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, receipt, cancellationToken: ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Uses only the exact configured origin and the selected SSH identity.</summary>
public sealed class PlatformRepositoryGitProof(
    Func<string, string?> keyPathForGeneration, string workDirectory,
    Func<string, string?>? credentialStoreForGeneration = null)
    : IRepositoryGitProof
{
    public async Task<RepositoryAccessProof> ProveAsync(
        RepositoryAccessBinding binding, string? generation, CancellationToken ct)
    {
        var sshOrigin = binding.Origin.StartsWith("git@github.com:", StringComparison.Ordinal);
        var keyPath = generation is not null ? keyPathForGeneration(generation) : null;
        var credentialStore = generation is not null && keyPath is null && !sshOrigin
            ? credentialStoreForGeneration?.Invoke(generation) : null;
        if (generation is not null && (keyPath is not null || sshOrigin || credentialStoreForGeneration is null
                ? keyPath is null || !File.Exists(keyPath)
                : credentialStore is null || !File.Exists(credentialStore)))
            return new(binding.Purpose, binding.Origin, false, false, binding.RequiresPush, "identity-missing");
        if (keyPath is not null && keyPath.Contains('\'') || credentialStore is not null && credentialStore.Contains('\''))
            throw new ArgumentException("Credential path contains an unsupported quote.");
        var env = CandidateGitEnvironment(binding.Origin, keyPath);
        if (credentialStore is not null)
        {
            env["GIT_CONFIG_NOSYSTEM"] = "1";
            env["GIT_CONFIG_GLOBAL"] = "/dev/null";
        }
        var directory = Path.Combine(workDirectory, "repository-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Task<ProcessResult> Git(params string[] args) => ProcessRunner.RunAsync(
                "git", credentialStore is null ? args :
                    ["-c", "credential.helper=", "-c",
                        $"credential.helper=store --file='{credentialStore}'",
                        "-c", "credential.https://github.com.useHttpPath=true", .. args],
                directory, environment: env, ct: ct);
            if (!(await Git("init", "-q")).Success)
                return new(binding.Purpose, binding.Origin, false, false, binding.RequiresPush, "git-init-failed");
            var fetch = await Git("fetch", "--no-tags", binding.Origin, "HEAD");
            if (!fetch.Success)
                return new(binding.Purpose, binding.Origin, false, false, binding.RequiresPush, "fetch-failed");
            if (!binding.RequiresPush)
                return new(binding.Purpose, binding.Origin, true, false, false, "verified");
            var probeRef = "refs/heads/agent-studio-access-probe/" + Guid.NewGuid().ToString("N");
            var push = await Git("push", binding.Origin, "FETCH_HEAD:" + probeRef);
            if (!push.Success)
                return new(binding.Purpose, binding.Origin, true, false, true, "push-denied");
            var delete = await Git("push", binding.Origin, ":" + probeRef);
            return new(binding.Purpose, binding.Origin, true, delete.Success, true,
                delete.Success ? "verified" : "probe-cleanup-required");
        }
        finally
        {
            ResilientDirectory.Delete(directory);
        }
    }

    internal static Dictionary<string, string?> CandidateGitEnvironment(string origin, string? keyPath)
    {
        var env = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" };
        if (keyPath is null) return env;
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = "/dev/null";
        env["GIT_SSH_COMMAND"] =
            $"ssh -i '{keyPath}' -o IdentitiesOnly=yes -o IdentityAgent=none -o BatchMode=yes";
        if (origin.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            var uri = new Uri(origin);
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] =
                $"url.git@github.com:{uri.AbsolutePath.TrimStart('/')}.insteadOf";
            env["GIT_CONFIG_VALUE_0"] = origin;
        }
        return env;
    }
}
