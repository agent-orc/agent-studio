using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Host-local executor for claimable deploy jobs. No agent CLI is involved.</summary>
public static class DeployHostCommand
{
    private sealed record Claim(string HostId);
    private sealed record Job(string Id, string Project, string EnvironmentId, string HostId,
        string Artifact, string Template, string Destination, string? HealthUrl,
        string[] SecretRefs, int ContainerPort, string? PreviousArtifact, string Status, string LeaseToken,
        string? RetryStep)
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
        public ProjectDeployStep[] SshSteps { get; init; } = [];
        public ProjectVariableDefinition[] Variables { get; init; } = [];
        public ProjectArtifactDefinition[] Artifacts { get; init; } = [];
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        HttpClient? transport = null, IPreviewRunZone? previewZone = null,
        SshDeployExecutor? sshExecutor = null,
        Func<string, CancellationToken, Task<bool>>? checkHealth = null, TimeProvider? clock = null)
    {
        if (args.Length < 4 || args.Length > 5)
        {
            Console.Error.WriteLine("Usage: agent-host deploy-poll <server-url> <host-id> <token-file> <artifact-store> [secret-store]");
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
        var evidence = "deploy-step-failed";
        var healthy = false;
        string? screenshotBase64 = null;
        var stepResults = new List<object>();
        try
        {
            var secretRoot = args.Length == 5 ? args[4] : "/etc/agent-host/secrets";
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
                if (job.SshSteps.Length > 0)
                {
                    var executor = sshExecutor ?? new SshDeployExecutor();
                    foreach (var (step, index) in job.SshSteps.Select((value, index) => (value, index)))
                    {
                        var name = $"deploy[{index}]:{step.Step}";
                        try
                        {
                            var variables = await ResolveVariables(job, client, lease.Token);
                            var bundlePath = step.Step == "static-site"
                                ? job.Artifacts.FirstOrDefault(item => item.Name == step.Artifact && item.Type == "static")?.Path
                                    ?? throw new InvalidDataException("Static artifact is missing from release snapshot.")
                                : null;
                            await executor.ExecuteAsync(job.HostId, step, job.Artifact, args[3], variables,
                                message => AppendLog(client, job, name, message, lease.Token), lease.Token, bundlePath);
                            if (step.HealthUrl is { } stepHealth)
                            {
                                using var healthLimit = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                                using var timer = (clock ?? TimeProvider.System).CreateTimer(_ => healthLimit.Cancel(), null,
                                    TimeSpan.FromSeconds(step.HealthTimeoutSeconds ?? 60), Timeout.InfiniteTimeSpan);
                                var passed = false;
                                try { passed = await (checkHealth ?? CheckHealth)(stepHealth, healthLimit.Token); }
                                catch (OperationCanceledException) when (!lease.IsCancellationRequested) { }
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
                            var detail = new string($"Step failed: {ex.GetType().Name}: {ex.Message}"
                                .Where(ch => !char.IsControl(ch)).ToArray());
                            await AppendLog(client, job, name, detail[..Math.Min(detail.Length, 480)], lease.Token);
                            throw;
                        }
                    }
                }
                else if (job.RetryStep is null)
                {
                    if (job.Template == "container")
                        await DeployContainer(job, secretRoot, lease.Token);
                    else if (job.Template == "static")
                        await DeployStatic(job, args[3], lease.Token);
                    else throw new InvalidOperationException("Unknown template.");
                }
                else if (job.RetryStep != "health") throw new InvalidOperationException("Unknown retry step.");
                evidence = "health-http-failed";
                var healthUrl = job.SshSteps.Length > 0 ? null : job.HealthUrl;
                if (healthUrl is null) healthy = true;
                else
                {
                    using var healthLimit = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                    using var timer = (clock ?? TimeProvider.System).CreateTimer(_ => healthLimit.Cancel(), null,
                        TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                    try { healthy = await (checkHealth ?? CheckHealth)(healthUrl, healthLimit.Token); }
                    catch (OperationCanceledException) when (!lease.IsCancellationRequested) { healthy = false; }
                }
                if (healthy) evidence = "health-ok";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or HttpRequestException or System.ComponentModel.Win32Exception or OperationCanceledException
            or KeyNotFoundException or JsonException)
        {
            Console.Error.WriteLine($"Deploy job {job.Id} failed: {ex.GetType().Name}");
        }
        lease.Cancel();
        await renewal;
        if (leaseLost) return 1;
        using var report = await client.PostAsJsonAsync(
            $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/report",
            new { job.LeaseToken, Healthy = healthy, Evidence = evidence, ScreenshotBase64 = screenshotBase64,
                Steps = stepResults }, cancellationToken);
        report.EnsureSuccessStatusCode();
        return healthy ? 0 : 1;
    }

    private static async Task RenewWhileRunning(HttpClient client, Job job, TimeProvider clock,
        Action onLost, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        var lastSuccess = clock.GetUtcNow();
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
                    if (response.StatusCode == System.Net.HttpStatusCode.Conflict) { onLost(); return; }
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
                    if (response.IsSuccessStatusCode) { lastSuccess = clock.GetUtcNow(); continue; }
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                if (clock.GetUtcNow() - lastSuccess >= TimeSpan.FromMinutes(4)) { onLost(); return; }
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
            if (body is null || !body.RootElement.TryGetProperty("value", out var secretValue)
                || secretValue.ValueKind != JsonValueKind.String || secretValue.GetString() is not { } secret)
                throw new InvalidDataException("Secret provider returned no value.");
            resolved.Add(variable.Name, secret);
        }
        return resolved;
    }

    private static async Task AppendLog(HttpClient client, Job job, string step, string message, CancellationToken ct)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/log",
                new { job.LeaseToken, Step = step, Message = message }, ct);
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"Deploy job {job.Id} step log was not accepted: {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (HttpRequestException or TaskCanceledException))
        {
            Console.Error.WriteLine($"Deploy job {job.Id} step log could not be sent.");
        }
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

    private static async Task DeployContainer(Job job, string secretRoot, CancellationToken ct)
    {
        if (!int.TryParse(job.Destination, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Invalid proxy port.");
        var projectName = job.Project.Length <= 64
            && job.Project.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            ? job.Project
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.Project)))[..16].ToLowerInvariant();
        var name = "deploy-" + projectName + "-" + job.EnvironmentId;
        var envFile = Path.Combine(Path.GetTempPath(), "deploy-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lines = new List<string>();
            foreach (var secret in job.SecretRefs)
            {
                if (secret.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
                    throw new InvalidOperationException("Invalid secret reference.");
                var value = (await File.ReadAllTextAsync(Path.Combine(secretRoot, secret), ct)).TrimEnd('\r', '\n');
                if (value.Contains('\n') || value.Contains('\r')) throw new InvalidOperationException("Secret must be single-line.");
                lines.Add(secret.Replace('-', '_').Replace('.', '_').ToUpperInvariant() + "=" + value);
            }
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
            if (File.Exists(envFile)) File.Delete(envFile);
        }
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
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                // A transient transport failure gets another probe within the outer health budget.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // The per-request timeout expired; the outer health budget still controls the job.
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
