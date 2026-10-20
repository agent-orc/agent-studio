using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.DeployAdapters;

/// <summary>Operator inventory, kept outside project.yml and the release snapshot.</summary>
public sealed record DeployTarget(
    string Kind, string Name, string CredentialSecret, string? Host = null, string? User = null,
    string? KnownHostsSecret = null, string? Namespace = null, string? Project = null, string? Region = null,
    string? ManifestPath = null, string? HelmRelease = null, string? ImageResource = null,
    string? CloudService = null,
    string? IdentityPoolProjectNumber = null, string? IdentityPool = null,
    string? IdentityProvider = null, string? CloudServiceAccount = null);

/// <summary>
/// Input from a deploy job. ReleaseArtifacts and Step come from the immutable
/// release; the operator inventory supplies Target. PackagePath is the local
/// gate artifact, never a path taken from the target.
/// </summary>
public sealed record DeployTargetRequest(
    string Project, string Environment, string Artifact, IReadOnlyList<string> ReleaseArtifacts,
    ProjectDeployStep Step, DeployTarget Target, string? PackagePath = null, string? PackageArtifact = null);

public interface IDeployTargetAdapter
{
    string Kind { get; }
    Task DeployAsync(DeployTargetRequest request, CancellationToken ct);
}

/// <summary>The AGT-3233 executor can call this after claim and before health/report.</summary>
public sealed class DeployTargetExecutor(IEnumerable<IDeployTargetAdapter> adapters)
{
    public async Task DeployAsync(DeployTargetRequest request, CancellationToken ct)
    {
        DeployArtifactVerifier.Verify(request);
        var adapter = adapters.SingleOrDefault(item => item.Kind == request.Target.Kind)
            ?? throw new InvalidOperationException("Deploy target kind is not supported.");
        await adapter.DeployAsync(request, ct);
    }
}

public static partial class DeployArtifactVerifier
{
    public static string Verify(DeployTargetRequest request)
    {
        if (!request.ReleaseArtifacts.Contains(request.Artifact, StringComparer.Ordinal))
            throw new InvalidDataException("Artifact is absent from the release snapshot.");
        var match = Digest().Match(request.Artifact);
        if (!match.Success) throw new InvalidDataException("Artifact must have a SHA-256 digest.");
        if (request.Step.Step is not ("static-site" or "publish-static" or "container" or "start-container"))
            throw new InvalidDataException("Step cannot be deployed to an operator target.");
        if (string.IsNullOrWhiteSpace(request.Step.Artifact))
            throw new InvalidDataException("Deploy step does not name an artifact.");
        if (request.Step.Environments.Count > 0
            && !request.Step.Environments.Contains(request.Environment, StringComparer.Ordinal))
            throw new InvalidDataException("Deploy step is not scoped to this environment.");
        if (string.IsNullOrWhiteSpace(request.Step.Target) || request.Step.Target != request.Target.Name)
            throw new InvalidDataException("Step target does not match operator inventory.");
        var expectedType = ProjectDeploymentVocabulary.ArtifactTypeFor(request.Step.Step);
        if (expectedType == "static" && request.PackagePath is null)
            throw new InvalidDataException("Static deploy needs the verified release package.");
        if (expectedType == "container" && !request.Artifact.Contains('/', StringComparison.Ordinal))
            throw new InvalidDataException("Container deploy needs a registry image pinned by digest.");
        return match.Groups[1].Value;
    }

    public static async Task VerifyPackageAsync(DeployTargetRequest request, CancellationToken ct)
    {
        var digest = Verify(request);
        if (request.PackagePath is null) throw new InvalidDataException("Release package is missing.");
        if (request.PackageArtifact is not null)
        {
            if (!request.ReleaseArtifacts.Contains(request.PackageArtifact, StringComparer.Ordinal))
                throw new InvalidDataException("Release package is absent from the snapshot.");
            var packageMatch = Digest().Match(request.PackageArtifact);
            if (!packageMatch.Success) throw new InvalidDataException("Release package digest is invalid.");
            digest = packageMatch.Groups[1].Value;
        }
        await using var stream = File.OpenRead(request.PackagePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (actual != digest) throw new InvalidDataException("Release package digest does not match the snapshot.");
    }

    [GeneratedRegex(@"\A[A-Za-z0-9./:_-]+@sha256:([0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
}

public interface IDeploySecretResolver
{
    Task<string> ResolveAsync(string reference, CancellationToken ct);
}

/// <summary>One command result; callers must not include secret values in arguments.</summary>
public sealed record DeployCommandResult(int ExitCode, string Output);

public interface IDeployCommandRunner
{
    Task<DeployCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct);
}

public sealed class DeployCommandRunner : IDeployCommandRunner
{
    public async Task<DeployCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment?.ContainsKey("CLOUDSDK_CONFIG") == true)
        {
            start.Environment.Remove("CLOUDSDK_AUTH_CREDENTIAL_FILE_OVERRIDE");
            start.Environment.Remove("GOOGLE_APPLICATION_CREDENTIALS");
        }
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Deploy command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Deploy command {executable} failed.");
        return new(process.ExitCode, output.Result);
    }
}

internal static partial class DeployTargetValidation
{
    public static string Identifier(string? value)
        => value is not null && !value.Contains('\n') && !value.Contains('\r')
            && IdentifierPattern().IsMatch(value) ? value
            : throw new InvalidDataException("Target identifier is invalid.");

    public static string Host(string? value)
        => value is not null && HostPattern().IsMatch(value) && !value.StartsWith('-') ? value
            : throw new InvalidDataException("SSH host is invalid.");

    public static string RemotePath(string? value)
        => value is not null && !value.Contains('\n') && !value.Contains('\r')
            && RemotePathPattern().IsMatch(value) ? value
            : throw new InvalidDataException("Remote path is invalid.");

    public static string Image(string value)
        => ImagePattern().IsMatch(value) ? value
            : throw new InvalidDataException("Image reference is invalid.");

    [GeneratedRegex(ProjectDeploymentVocabulary.IdentifierPattern, RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
    [GeneratedRegex(ProjectDeploymentVocabulary.RemotePathPattern, RegexOptions.CultureInvariant)]
    private static partial Regex RemotePathPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9./:_-]*@sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();
}

internal sealed class DeployTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "agent-deploy-" + Guid.NewGuid().ToString("N"));

    public DeployTempDirectory()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Path);
        else Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task<string> SecretFileAsync(string name, string value, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(Path, name);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await file.WriteAsync(Encoding.UTF8.GetBytes(value), ct);
        return path;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
