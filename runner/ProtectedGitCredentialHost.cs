using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentRunner;

/// <summary>A protected session supplies a replacement bearer directly to its target host.</summary>
public interface IProtectedGitHubTokenSession
{
    Task<(string Username, string Bearer)?> ObtainAsync(
        RepositoryHttpsRotationRequest request, CancellationToken ct);
}

/// <summary>Stages a replacement in a host-only Git helper file before changing the active helper.</summary>
public sealed class ProtectedGitCredentialHost(
    string protectedDirectory,
    IProtectedGitHubTokenSession session,
    Func<RepositoryHttpsRotationRequest, CancellationToken, Task<bool>> transportsDrained,
    Func<RepositoryHttpsRotationRequest, CancellationToken, Task<string>> activeGeneration)
    : IRepositoryHttpsCredentialHost
{
    public async Task<string> ActiveGenerationAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
    {
        var marker = MarkerPath(request.Metadata.Origin);
        if (!File.Exists(marker)) return await activeGeneration(request, ct);
        var generation = (await File.ReadAllTextAsync(marker, ct)).Trim();
        return generation.Length > 0 ? generation
            : throw new InvalidOperationException("Active HTTPS credential generation marker is empty.");
    }

    private string MarkerPath(string origin)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))
            .ToLowerInvariant()[..32];
        return Path.Combine(protectedDirectory, "active-https-" + digest);
    }
    private string StorePath(string operationId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId)))
            .ToLowerInvariant()[..32];
        return Path.Combine(protectedDirectory, "https-" + digest + ".credentials");
    }

    public string? StoreForGeneration(string generation)
        => generation.StartsWith("https-", StringComparison.Ordinal)
            ? StorePath(generation[6..]) : null;

    public async Task<string?> StageAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Protected Git credential staging requires the Linux host adapter.");
        ProtectedHostPath.EnsureOutsideRepository(protectedDirectory);
        Directory.CreateDirectory(protectedDirectory);
        File.SetUnixFileMode(protectedDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = StorePath(request.OperationId);
        await using var operationLock = await HostFileOperationLock.AcquireAsync(path + ".lock", ct);
        if (File.Exists(path)) return "https-" + request.OperationId;
        var granted = await session.ObtainAsync(request, ct);
        if (granted is null) return null;
        if (granted.Value.Username.Contains('\n') || granted.Value.Username.Contains('\r')
            || granted.Value.Bearer.Contains('\n') || granted.Value.Bearer.Contains('\r'))
            throw new ArgumentException("GitHub credential contains an invalid line break.");
        var uri = new Uri(request.Metadata.Origin);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, string.Empty, ct);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach (var repositoryPath in RepositoryPaths(uri))
            {
                var input = $"protocol=https\nhost=github.com\npath={repositoryPath}\n"
                    + $"username={granted.Value.Username}\npassword={granted.Value.Bearer}\n\n";
                var stored = await ProcessRunner.RunAsync("git",
                    ["credential-store", "--file", temporary, "store"], stdin: input, ct: ct);
                if (!stored.Success) throw new InvalidOperationException("Protected Git credential staging failed.");
            }
            File.Move(temporary, path, false);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return "https-" + request.OperationId;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<bool> DrainTransportsAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
        => transportsDrained(request, ct);

    public async Task SwitchAsync(RepositoryHttpsRotationRequest request, string generation, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Protected Git credential switching requires Linux.");
        if (generation != "https-" + request.OperationId)
            throw new InvalidOperationException("Staged credential generation mismatch.");
        ProtectedHostPath.EnsureOutsideRepository(protectedDirectory);
        Directory.CreateDirectory(protectedDirectory);
        File.SetUnixFileMode(protectedDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = MarkerPath(request.Metadata.Origin);
        await using var switchLock = await HostFileOperationLock.AcquireAsync(marker + ".lock", ct);
        if (File.Exists(marker))
        {
            var active = (await File.ReadAllTextAsync(marker, ct)).Trim();
            if (active != request.ExpectedGeneration && active != generation)
                throw new InvalidOperationException("HTTPS credential generation changed before switch.");
        }
        var path = StorePath(request.OperationId);
        var uri = new Uri(request.Metadata.Origin);
        var pathSensitive = await ProcessRunner.RunAsync("git",
            ["config", "--global", "credential.https://github.com.useHttpPath", "true"], ct: ct);
        if (!pathSensitive.Success)
            throw new InvalidOperationException("Path-scoped Git credential lookup could not be configured.");
        foreach (var repositoryPath in RepositoryPaths(uri))
        {
            var query = $"protocol=https\nhost=github.com\npath={repositoryPath}\n\n";
            var filled = await ProcessRunner.RunAsync("git",
                ["credential-store", "--file", path, "get"], stdin: query, ct: ct);
            if (!filled.Success || !filled.StdOut.Contains("password=", StringComparison.Ordinal))
                throw new InvalidOperationException("Staged credential is unavailable.");
            var approved = await ProcessRunner.RunAsync("git", ["credential", "approve"],
                stdin: query.TrimEnd() + "\n" + filled.StdOut.Trim() + "\n\n", ct: ct);
            if (!approved.Success)
                throw new InvalidOperationException("Active Git helper did not accept the verified credential.");
            var active = await ProcessRunner.RunAsync("git", ["credential", "fill"],
                stdin: query, environment: new Dictionary<string, string?> {
                    ["GIT_TERMINAL_PROMPT"] = "0"
                }, ct: ct);
            if (!active.Success || !active.StdOut.Contains(
                    filled.StdOut.Split('\n').First(line => line.StartsWith("password=", StringComparison.Ordinal)),
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Active Git helper did not load the verified credential.");
        }
        var temporaryMarker = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryMarker, generation, ct);
            File.SetUnixFileMode(temporaryMarker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryMarker, marker, true);
        }
        finally { if (File.Exists(temporaryMarker)) File.Delete(temporaryMarker); }
    }

    public Task RetireAsync(RepositoryHttpsRotationRequest request, CancellationToken ct)
    {
        // The active helper now holds the replacement. Revocation remains a
        // separate authorized action after dependent deploy keys are inspected.
        var path = StorePath(request.OperationId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static IEnumerable<string> RepositoryPaths(Uri uri)
    {
        var path = uri.AbsolutePath.TrimStart('/');
        yield return path;
        yield return path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4] : path + ".git";
    }
}

public sealed class FileRepositoryHttpsRotationJournal(string protectedDirectory)
    : IRepositoryHttpsRotationJournal
{
    private string PathFor(string operationId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId)))
            .ToLowerInvariant();
        return Path.Combine(protectedDirectory, "https-rotation-" + digest + ".json");
    }

    public async Task<RepositoryHttpsRotationReceipt?> LoadAsync(string operationId, CancellationToken ct)
    {
        var path = PathFor(operationId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var receipt = await JsonSerializer.DeserializeAsync<RepositoryHttpsRotationReceipt>(stream, cancellationToken: ct);
        if (receipt?.OperationId != operationId)
            throw new InvalidOperationException("HTTPS rotation journal identity mismatch.");
        return receipt;
    }

    public async Task SaveAsync(RepositoryHttpsRotationReceipt receipt, CancellationToken ct)
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
