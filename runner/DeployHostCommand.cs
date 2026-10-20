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
    private sealed record StepResult(string Name, bool Succeeded);
    private sealed record Job(string Id, string Project, string EnvironmentId, string HostId,
        string Artifact, string Template, string Destination, string? HealthUrl,
        string[] SecretRefs, int ContainerPort, string? PreviousArtifact, string Status, string LeaseToken)
    {
        public ProjectDeployStep[] SshSteps { get; init; } = [];
        public ProjectVariableDefinition[] Variables { get; init; } = [];
        public ProjectArtifactDefinition[] Artifacts { get; init; } = [];
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        HttpClient? transport = null, SshDeployExecutor? sshExecutor = null,
        HttpClient? healthTransport = null, TimeProvider? timeProvider = null)
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
        var evidence = "deploy-step-failed";
        var healthy = false;
        var stepResults = new List<StepResult>();
        using var claim = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clock = timeProvider ?? TimeProvider.System;
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = MaintainLease(client, job, clock, claim, heartbeatStop.Token);
        try
        {
            try
            {
                var secretRoot = args.Length == 5 ? args[4] : "/etc/agent-host/secrets";
                if (job.SshSteps.Length > 0)
                {
                    var executor = sshExecutor ?? new SshDeployExecutor();
                    for (var index = 0; index < job.SshSteps.Length; index++)
                    {
                        var step = job.SshSteps[index];
                        var name = $"deploy[{index}]:{step.Step}";
                        try
                        {
                            claim.Token.ThrowIfCancellationRequested();
                            var variables = await ResolveVariables(job, client, claim.Token);
                            var bundlePath = step.Step == "static-site"
                                ? job.Artifacts.FirstOrDefault(item => item.Name == step.Artifact && item.Type == "static")?.Path
                                    ?? throw new InvalidDataException("Static artifact is missing from the release snapshot.")
                                : null;
                            await executor.ExecuteAsync(job.HostId, step, job.Artifact, args[3], variables,
                                message => AppendLog(client, job, name, message, claim.Token), claim.Token,
                                bundlePath);
                            evidence = "health-http-failed";
                            if (step.HealthUrl is not null && !await CheckStepHealth(step.HealthUrl,
                                    step.HealthTimeoutSeconds, healthTransport, clock, claim.Token))
                            {
                                await AppendLog(client, job, name, "Step health check failed.", claim.Token);
                                stepResults.Add(new(name, false));
                                break;
                            }
                            stepResults.Add(new(name, true));
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                            or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception)
                        {
                            stepResults.Add(new(name, false));
                            await AppendLog(client, job, name, $"Step failed: {ex.GetType().Name}.", claim.Token);
                            throw;
                        }
                    }
                    healthy = stepResults.Count == job.SshSteps.Length && stepResults.All(item => item.Succeeded);
                    if (healthy) evidence = "health-ok";
                }
                else if (job.Template == "container")
                    await DeployContainer(job, secretRoot, claim.Token);
                else if (job.Template == "static")
                    await DeployStatic(job, args[3], claim.Token);
                else throw new InvalidOperationException("Unknown template.");
                if (job.SshSteps.Length == 0)
                {
                    evidence = "health-http-failed";
                    healthy = job.HealthUrl is null || await CheckStepHealth(job.HealthUrl, 60,
                        healthTransport, clock, claim.Token);
                    if (healthy) evidence = "health-ok";
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                or HttpRequestException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"Deploy job {job.Id} failed: {ex.GetType().Name}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && claim.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Deploy job {job.Id} stopped after losing its claim.");
            }
            try
            {
                await heartbeatStop.CancelAsync();
                await heartbeat;
                claim.Token.ThrowIfCancellationRequested();
                using var reportLimit = CancellationTokenSource.CreateLinkedTokenSource(claim.Token);
                using var reportTimeout = CancelAfter(reportLimit, clock, TimeSpan.FromSeconds(30));
                using var report = await client.PostAsJsonAsync(
                    $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/report",
                    new { job.LeaseToken, Healthy = healthy, Evidence = evidence, Steps = stepResults }, reportLimit.Token);
                report.EnsureSuccessStatusCode();
                return healthy ? 0 : 1;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && claim.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Deploy job {job.Id} lost its claim.");
                return 1;
            }
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
    }

    private static async Task MaintainLease(HttpClient client, Job job, TimeProvider clock,
        CancellationTokenSource claim, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                using var renewLimit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                using var renewTimeout = CancelAfter(renewLimit, clock, TimeSpan.FromSeconds(15));
                using var response = await client.PostAsJsonAsync(
                    $"api/deploy/jobs/{Uri.EscapeDataString(job.Project)}/{Uri.EscapeDataString(job.Id)}/renew",
                    new { job.LeaseToken }, renewLimit.Token);
                if (response.IsSuccessStatusCode) continue;
                Console.Error.WriteLine($"Deploy job {job.Id} lease renewal was refused.");
                await claim.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Deploy job {job.Id} lease renewal timed out.");
            await claim.CancelAsync();
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Deploy job {job.Id} lease renewal failed: {ex.GetType().Name}");
            await claim.CancelAsync();
        }
    }

    private static async Task<bool> CheckStepHealth(string url, int? seconds, HttpClient? transport,
        TimeProvider clock, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timeout = CancelAfter(limit, clock, TimeSpan.FromSeconds(Math.Clamp(seconds ?? 60, 1, 900)));
        try { return await CheckHealth(url, transport, clock, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static ITimer CancelAfter(CancellationTokenSource source, TimeProvider clock, TimeSpan delay)
        => clock.CreateTimer(state => ((CancellationTokenSource)state!).Cancel(), source,
            delay, Timeout.InfiniteTimeSpan);

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

    private static async Task<bool> CheckHealth(string url, HttpClient? transport, TimeProvider clock,
        CancellationToken ct)
    {
        using var owned = transport is null ? new HttpClient() : null;
        var client = transport ?? owned!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var attemptTimeout = CancelAfter(attempt, clock, TimeSpan.FromSeconds(10));
                using var response = await client.GetAsync(url, attempt.Token);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { /* A failed probe can recover before the configured deadline. */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Retry a timed-out probe. */ }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, ct);
        }
        return false;
    }
}
