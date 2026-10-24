using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Host-local executor for claimable deploy jobs. No agent CLI is involved.</summary>
public static class DeployHostCommand
{
    private sealed class RetryableRegistryException : Exception;
    private sealed record Claim(string HostId);
    private sealed record Job(string Id, string Project, string EnvironmentId, string HostId,
        string Artifact, string Template, string Destination, string? HealthUrl,
        string[] SecretRefs, int ContainerPort, string? PreviousArtifact, string Status, string LeaseToken,
        bool IsStop = false, string? RetryStep = null,
        Dictionary<string, string>? SecretBindings = null, JsonElement[]? MissingSecretRefs = null)
    {
        public ProjectDeployStep[] SshSteps { get; init; } = [];
        public ProjectVariableDefinition[] Variables { get; init; } = [];
        public ProjectArtifactDefinition[] Artifacts { get; init; } = [];
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
    private sealed record Provenance(string Project, string Commit, string Artifact, string Digest,
        string GateRunId, string Template, string BuilderIdentity, string Location, string SourceDigest);
    private sealed record IssuedSecret(string Value);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        HttpClient? transport = null, SshDeployExecutor? sshExecutor = null,
        Func<string, CancellationToken, Task<bool>>? checkHealth = null, TimeProvider? clock = null,
        IPreviewRunZone? previewZone = null)
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
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = false;
        var renewal = RenewWhileRunning(client, job, clock ?? TimeProvider.System, () =>
        {
            leaseLost = true;
            lease.Cancel();
        }, lease.Token);
        if (job.IsStop)
        {
            var stopped = await StopEnvironment(client, job, lease.Token);
            lease.Cancel();
            await renewal;
            return leaseLost ? 1 : stopped;
        }
        var evidence = "deploy-step-failed";
        var healthy = false;
        var stepResults = new List<object>();
        string? screenshotBase64 = null;
        try
        {
            if (job.RetryStep is null)
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
                if (job.Kind is not ("preview" or "preview-remove"))
                {
                    var provenance = await VerifyArtifact(client, job, cancellationToken);
                    if (job.Template == "static")
                    {
                        if (job.SecretRefs.Length > 0)
                            throw new InvalidOperationException("Static deployment cannot receive secrets.");
                        await DownloadArtifact(client, job, provenance, args[3], cancellationToken);
                    }
                }
            }
            if (job.Kind is "preview" or "preview-remove")
            {
                if (previewZone is null)
                {
                    throw new InvalidOperationException("Preview run zone is not configured for this deploy host.");
                }
                if (job.CardKey is null || job.SourceCommit is null || job.Address is null
                    || job.SourceRepository is null || job.SourceRef is null)
                    throw new InvalidDataException("Preview job has no card, repository, commit, ref, or address.");
                var preview = new PreviewRunZoneJob(job.Project, job.CardKey, job.SourceCommit,
                    job.SourceRepository, job.SourceRef, job.Address, job.Template, job.Destination,
                    job.ArtifactPath, job.Dockerfile, job.BuildCommands, job.SecretRefs, job.ContainerPort);
                if (job.Kind == "preview-remove")
                {
                    await previewZone.RemoveAsync(preview, lease.Token);
                    healthy = true;
                }
                else
                {
                    var result = await previewZone.DeployAsync(preview, lease.Token);
                    healthy = result.Healthy && result.ScreenshotPng is { Length: > 0 };
                    screenshotBase64 = healthy ? Convert.ToBase64String(result.ScreenshotPng!) : null;
                }
                evidence = healthy ? "health-ok" : "deploy-step-failed";
            }
            else
            {
                if (job.RetryStep is null && job.SshSteps.Length > 0)
                {
                    var executor = sshExecutor ?? new SshDeployExecutor();
                    for (var index = 0; index < job.SshSteps.Length; index++)
                    {
                        var step = job.SshSteps[index];
                        var name = $"deploy[{index}]:{step.Step}";
                        try
                        {
                            var variables = await ResolveVariables(job, client, lease.Token);
                            var bundlePath = step.Step == "static-site"
                                ? job.Artifacts.FirstOrDefault(item => item.Name == step.Artifact && item.Type == "static")?.Path
                                    ?? throw new InvalidDataException("Static artifact is missing from the release snapshot.")
                                : null;
                            await executor.ExecuteAsync(job.HostId, step, job.Artifact, args[3], variables,
                                message => AppendLog(client, job, name, message, lease.Token), lease.Token,
                                bundlePath);
                            if (step.HealthUrl is { } stepHealth)
                            {
                                using var limit = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                                using var timeout = (clock ?? TimeProvider.System).CreateTimer(_ => limit.Cancel(), null,
                                    TimeSpan.FromSeconds(step.HealthTimeoutSeconds ?? 60), Timeout.InfiniteTimeSpan);
                                var passed = false;
                                try { passed = await (checkHealth ?? CheckHealth)(stepHealth, limit.Token); }
                                catch (OperationCanceledException) when (!lease.IsCancellationRequested) { passed = false; }
                                if (!passed)
                                {
                                    evidence = "health-http-failed";
                                    throw new InvalidOperationException("Step health check failed.");
                                }
                            }
                            stepResults.Add(new { name, succeeded = true });
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                            or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception)
                        {
                            stepResults.Add(new { name, succeeded = false });
                            await AppendLog(client, job, name, $"Step failed: {ex.GetType().Name}.", lease.Token);
                            throw;
                        }
                    }
                }
                else if (job.RetryStep is null && job.Template == "container")
                    await DeployContainer(job, client, lease.Token);
                else if (job.RetryStep is null && job.Template == "static")
                    await DeployStatic(job, args[3], lease.Token);
                else if (job.RetryStep is not "health") throw new InvalidOperationException("Unknown template or retry step.");
                evidence = "health-http-failed";
                var healthUrl = job.SshSteps.Length > 0 && job.RetryStep is null ? null : job.HealthUrl;
                using var healthLimit = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                using var legacyTimeout = (clock ?? TimeProvider.System).CreateTimer(_ => healthLimit.Cancel(), null,
                    TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                try { healthy = healthUrl is null || await (checkHealth ?? CheckHealth)(healthUrl, healthLimit.Token); }
                catch (OperationCanceledException) when (!lease.IsCancellationRequested) { healthy = false; }
                if (healthy) evidence = "health-ok";
            }
        }
        catch (RetryableRegistryException)
        {
            // Keep the claim for a later lease retry. No bytes were deployed.
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or HttpRequestException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Console.Error.WriteLine($"Deploy job {job.Id} failed: {ex.GetType().Name}");
        }
        lease.Cancel();
        await renewal;
        if (leaseLost) return 1;
        using var report = await client.PostAsJsonAsync(
            $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/report",
            new { job.LeaseToken, Healthy = healthy, Evidence = evidence, Steps = stepResults,
                ScreenshotBase64 = screenshotBase64 }, cancellationToken);
        report.EnsureSuccessStatusCode();
        return healthy ? 0 : 1;
    }

    private static string ArtifactUrl(Job job)
        => $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/artifact";

    private static HttpRequestMessage ArtifactRequest(Job job, bool content)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ArtifactUrl(job) + (content ? "?content=1" : ""));
        request.Headers.Add("X-Deploy-Lease", job.LeaseToken);
        return request;
    }

    private static async Task<Provenance> VerifyArtifact(HttpClient client, Job job, CancellationToken ct)
    {
        using var request = ArtifactRequest(job, content: false);
        using var response = await client.SendAsync(request, ct);
        EnsureArtifactAvailable(response);
        var record = await response.Content.ReadFromJsonAsync<Provenance>(cancellationToken: ct);
        if (record is null || record.Project != job.Project || record.Artifact != job.Artifact
            || record.Template != job.Template || string.IsNullOrWhiteSpace(record.Commit)
            || string.IsNullOrWhiteSpace(record.GateRunId) || string.IsNullOrWhiteSpace(record.BuilderIdentity)
            || !job.Artifact.EndsWith("@sha256:" + record.Digest, StringComparison.Ordinal)
            || record.Location != (job.Template == "static" ? "built-in" : "external"))
            throw new InvalidDataException("Artifact provenance does not match the deploy job.");
        return record;
    }

    private static async Task DownloadArtifact(HttpClient client, Job job, Provenance record,
        string artifactStore, CancellationToken ct)
    {
        Directory.CreateDirectory(artifactStore);
        var target = Path.Combine(artifactStore, record.Digest);
        if (File.Exists(target))
        {
            await using var existing = File.OpenRead(target);
            if (Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).Equals(record.Digest,
                    StringComparison.OrdinalIgnoreCase)) return;
        }
        using var request = ArtifactRequest(job, content: true);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureArtifactAvailable(response);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
                await response.Content.CopyToAsync(output, ct);
            await using var stream = File.OpenRead(temporary);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(record.Digest,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artifact digest mismatch.");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureArtifactAvailable(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            throw new RetryableRegistryException();
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Stops the environment on this host and acknowledges it, so the server releases its slot.</summary>
    private static async Task<int> StopEnvironment(HttpClient client, Job job, CancellationToken ct)
    {
        var stopped = false;
        try
        {
            if (job.Template == "container")
                await Command("docker", ["rm", "-f", ContainerName(job)], ct);
            else if (job.Template == "static")
            {
                if (!Path.IsPathFullyQualified(job.Destination) || job.Destination.Contains("..", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid destination.");
                var link = Path.Combine(job.Destination, "current");
                if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
            }
            else throw new InvalidOperationException("Unknown template.");
            stopped = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Stop job {job.Id} failed: {ex.GetType().Name}");
        }
        using var report = await client.PostAsJsonAsync(
            $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/report",
            new { job.LeaseToken, Healthy = stopped, Evidence = stopped ? "stop-ok" : "stop-failed" }, ct);
        report.EnsureSuccessStatusCode();
        return stopped ? 0 : 1;
    }

    private static string ContainerName(Job job)
    {
        var projectName = job.Project.Length <= 64
            && job.Project.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            ? job.Project
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.Project)))[..16].ToLowerInvariant();
        return "deploy-" + projectName + "-" + job.EnvironmentId;
    }

    private static async Task RenewWhileRunning(HttpClient client, Job job, TimeProvider clock,
        Action onLost, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    using var response = await client.PostAsJsonAsync(
                        $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/renew",
                        new { job.LeaseToken }, timeout.Token);
                    if (!response.IsSuccessStatusCode && !ct.IsCancellationRequested) { onLost(); return; }
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested) { onLost(); return; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { onLost(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
    }

    private static async Task<Dictionary<string, string>> ResolveVariables(Job job, HttpClient client, CancellationToken ct)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in job.Variables)
        {
            if (variable.Value is { } value) { resolved.Add(variable.Name, value); continue; }
            if (variable.Secret is null) throw new InvalidDataException("Deploy variable has no source.");
            var reference = $"secret://{job.Project}/{job.EnvironmentId}/{variable.Secret}";
            using var response = await client.PostAsJsonAsync("api/runner/secrets/resolve", new
            {
                reference, purpose = "deploy", workload = new
                {
                    kind = "deploy-job", project = job.Project, jobId = job.Id, leaseToken = job.LeaseToken,
                },
            }, ct);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
            resolved.Add(variable.Name, body?.RootElement.GetProperty("value").GetString()
                ?? throw new InvalidDataException("Secret provider returned no value."));
        }
        return resolved;
    }

    private static async Task AppendLog(HttpClient client, Job job, string step, string message, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/log",
            new { job.LeaseToken, Step = step, Message = message }, ct);
        response.EnsureSuccessStatusCode();
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
        var name = ContainerName(job);
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
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        while (!ct.IsCancellationRequested)
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
