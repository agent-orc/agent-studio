using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Secrets;

namespace AgentRunner;

/// <summary>Host-local executor for claimable deploy jobs. No agent CLI is involved.</summary>
public static class DeployHostCommand
{
    private sealed record Claim(string HostId);
    private sealed record Job(string Id, string Project, string EnvironmentId, string HostId,
        string Artifact, string Template, string Destination, string? HealthUrl,
        string[] SecretRefs, int ContainerPort, string? PreviousArtifact, string Status, string LeaseToken,
        string? RetryStep = null, Dictionary<string, string>? SecretBindings = null,
        JsonElement[]? MissingSecretRefs = null)
    {
        public string Kind { get; init; } = "standard";
        public string? CardKey { get; init; }
        public string? SourceCommit { get; init; }
        public string? SourceRepository { get; init; }
        public string? SourceRef { get; init; }
        public string? ArtifactPath { get; init; }
        public string? Dockerfile { get; init; }
        public string? Address { get; init; }
        public string[] BuildCommands { get; init; } = [];
    }
    private sealed record IssuedSecret(string Value);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        HttpClient? transport = null, IPreviewRunZone? previewZone = null)
    {
        if (args.Length < 4 || args.Length > 5)
        {
            Console.Error.WriteLine("Usage: agent-host deploy-poll <server-url> <host-id> <token-file> <artifact-store>");
            return 2;
        }
        var token = (await File.ReadAllTextAsync(args[2], cancellationToken)).Trim();
        using var client = transport ?? new HttpClient();
        client.BaseAddress ??= new Uri(args[0].TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var claimed = await client.PostAsJsonAsync("api/deploy/jobs/claim", new Claim(args[1]), cancellationToken);
        claimed.EnsureSuccessStatusCode();
        var job = await claimed.Content.ReadFromJsonAsync<Job>(cancellationToken);
        if (job is null) return 0;
        var evidence = "deploy-step-failed";
        var healthy = false;
        string? screenshotBase64 = null;
        try
        {
            if (job.MissingSecretRefs is { Length: > 0 })
            {
                evidence = "secret-reference-missing";
                throw new InvalidOperationException("Secret preflight failed.");
            }
            if (SecretEnvironmentVariable.HasCollision(job.SecretRefs))
            {
                evidence = "secret-reference-collision";
                throw new InvalidOperationException("Secret references map to the same container variable.");
            }
            if (job.Kind is "preview" or "preview-remove")
            {
                if (previewZone is null)
                {
                    Console.Error.WriteLine("Preview run zone is not configured for this deploy host.");
                    return 2;
                }
                if (job.CardKey is null || job.SourceCommit is null || job.Address is null
                    || job.SourceRepository is null || job.SourceRef is null)
                    throw new InvalidDataException("Preview job has no card, repository, commit, ref, or address.");
                var preview = new PreviewRunZoneJob(job.Project, job.CardKey, job.SourceCommit,
                    job.SourceRepository, job.SourceRef, job.Address, job.Template, job.Destination,
                    job.ArtifactPath, job.Dockerfile, job.BuildCommands, job.SecretRefs, job.ContainerPort);
                if (job.Kind == "preview-remove")
                {
                    await previewZone.RemoveAsync(preview, cancellationToken);
                    healthy = true;
                }
                else
                {
                    var result = await previewZone.DeployAsync(preview, cancellationToken);
                    healthy = result.Healthy && result.ScreenshotPng is { Length: > 0 };
                    screenshotBase64 = healthy ? Convert.ToBase64String(result.ScreenshotPng!) : null;
                }
                evidence = healthy ? "health-ok" : "deploy-step-failed";
            }
            else
            {
                if (job.RetryStep is null)
                {
                    if (job.Template == "container")
                        await DeployContainer(job, client, cancellationToken);
                    else if (job.Template == "static")
                    {
                        if (job.SecretRefs.Length > 0) throw new InvalidOperationException("Static deployment cannot receive secrets.");
                        await DeployStatic(job, args[3], cancellationToken);
                    }
                    else throw new InvalidOperationException("Unknown template.");
                }
                else if (job.RetryStep != "health") throw new InvalidOperationException("Unknown retry step.");
                evidence = "health-http-failed";
                healthy = job.HealthUrl is null || await CheckHealth(job.HealthUrl, cancellationToken);
                if (healthy) evidence = "health-ok";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or HttpRequestException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Deploy job {job.Id} failed: {ex.GetType().Name}");
        }
        using var report = await client.PostAsJsonAsync(
            $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/report",
            new { job.LeaseToken, Healthy = healthy, Evidence = evidence, ScreenshotBase64 = screenshotBase64 }, cancellationToken);
        report.EnsureSuccessStatusCode();
        return healthy ? 0 : 1;
    }

    private static async Task DeployStatic(Job job, string artifactStore, CancellationToken ct)
    {
        var digest = job.Artifact.Split("@sha256:", StringSplitOptions.None).Last();
        if (digest.Length != 64 || digest.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidOperationException("Invalid digest.");
        var source = Path.Combine(artifactStore, digest);
        if (!File.Exists(source)) throw new IOException("Artifact missing.");
        await using (var stream = File.OpenRead(source))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (actual != digest) throw new IOException("Artifact digest mismatch.");
        }
        if (!Path.IsPathFullyQualified(job.Destination) || job.Destination.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid destination.");
        Directory.CreateDirectory(job.Destination);
        var target = Path.Combine(job.Destination, digest);
        if (!Directory.Exists(target))
        {
            var staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                await using var archive = File.OpenRead(source);
                using var gzip = job.Artifact.StartsWith("release-package@sha256:", StringComparison.Ordinal)
                    ? new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true) : null;
                using var reader = new TarReader((Stream?)gzip ?? archive);
                while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
                {
                    var relative = entry.Name.Replace('\\', '/');
                    if (relative.StartsWith('/') || relative.Split('/').Contains("..", StringComparer.Ordinal))
                        throw new InvalidOperationException("Artifact contains an unsafe path.");
                    var output = Path.Combine(staging, relative);
                    if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(output);
                    else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        await using var file = File.Create(output);
                        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(file, ct);
                    }
                    else throw new InvalidOperationException("Artifact contains an unsupported entry.");
                }
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        var link = Path.Combine(job.Destination, "current");
        var temporary = Path.Combine(job.Destination, ".current-" + Guid.NewGuid().ToString("N"));
        File.CreateSymbolicLink(temporary, target);
        File.Move(temporary, link, true);
    }

    private static async Task DeployContainer(Job job, HttpClient client, CancellationToken ct)
    {
        if (!int.TryParse(job.Destination, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Invalid proxy port.");
        var projectName = job.Project.Length <= 64
            && job.Project.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            ? job.Project
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.Project)))[..16].ToLowerInvariant();
        var name = "deploy-" + projectName + "-" + job.EnvironmentId;
        const string memoryRoot = "/dev/shm";
        if (!OperatingSystem.IsLinux() || !Directory.Exists(memoryRoot))
            throw new IOException("In-memory secret storage is unavailable.");
        var envDirectory = Path.Combine(memoryRoot, "deploy-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(envDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(envDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var envFile = Path.Combine(envDirectory, "environment");
        try
        {
            var lines = await ResolveEnvironmentAsync(client, job.Project, job.EnvironmentId, job.Id,
                job.LeaseToken, job.SecretRefs, job.SecretBindings, ct);
            await using (var file = File.Create(envFile))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(envFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await using var writer = new StreamWriter(file);
                foreach (var line in lines) await writer.WriteLineAsync(line.AsMemory(), ct);
            }
            await Command("docker", ["pull", job.Artifact], ct);
            await Command("docker", ["rm", "-f", name], ct, allowFailure: true);
            if (job.ContainerPort is < 1 or > 65535) throw new InvalidOperationException("Invalid container port.");
            await Command("docker", ["run", "-d", "--name", name, "--restart", "unless-stopped",
                "--publish", $"127.0.0.1:{port}:{job.ContainerPort}", "--env-file", envFile, job.Artifact], ct);
        }
        finally
        {
            if (Directory.Exists(envDirectory)) Directory.Delete(envDirectory, true);
        }
    }

    internal static async Task<IReadOnlyList<string>> ResolveEnvironmentAsync(HttpClient client, string project,
        string environment, string jobId, string leaseToken, IReadOnlyList<string> names,
        IReadOnlyDictionary<string, string>? bindings, CancellationToken ct)
    {
        var lines = new List<string>();
        if (SecretEnvironmentVariable.HasCollision(names))
            throw new InvalidOperationException("Secret references map to the same container variable.");
        foreach (var name in names)
        {
            if (name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
                throw new InvalidOperationException("Invalid secret reference.");
            if (bindings is null || !bindings.TryGetValue(name, out var reference))
                throw new InvalidOperationException("Secret binding missing.");
            var value = await ResolveSecret(client, project, environment, jobId, leaseToken, reference, ct);
            if (value.Contains('\n') || value.Contains('\r')) throw new InvalidOperationException("Secret must be single-line.");
            lines.Add(SecretEnvironmentVariable.Name(name) + "=" + value);
        }
        return lines;
    }

    private static async Task<string> ResolveSecret(HttpClient client, string project, string environment,
        string jobId, string leaseToken, string reference, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("api/runner/secrets/resolve", new
        {
            reference, purpose = "deploy:" + (environment.Length <= 57 ? environment
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment)))[..16].ToLowerInvariant()),
            workload = new { kind = "deploy-job", project, jobId, leaseToken },
        }, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Secret resolution failed.");
        var issued = await response.Content.ReadFromJsonAsync<IssuedSecret>(cancellationToken: ct);
        return issued?.Value ?? throw new InvalidOperationException("Secret resolution returned no value.");
    }

    private static async Task Command(string executable, IReadOnlyList<string> args, CancellationToken ct, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("Deploy command failed.");
    }

    private static async Task<bool> CheckHealth(string url, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }
}
