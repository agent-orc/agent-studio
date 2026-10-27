using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentStudio.Pipeline;

/// <summary>Outbound only gate role for the existing backend release binary.</summary>
public static class PullGateDaemon
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string serverUrl, CancellationToken shutdown = default)
    {
        var token = Environment.GetEnvironmentVariable("GATE_WORKER_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("GATE_WORKER_TOKEN is required.");
            return 2;
        }
        var name = Environment.GetEnvironmentVariable("GATE_WORKER_NAME") ?? Environment.MachineName;
        using var client = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var active = new List<Task>();
        var serverSlots = 1;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using (var registration = await client.PostAsJsonAsync("api/pull-gates/register",
                    new PullGateRegistration(name), Json, shutdown))
                {
                    registration.EnsureSuccessStatusCode();
                    serverSlots = (await registration.Content.ReadFromJsonAsync<PullGateHost>(Json, shutdown))?.Slots ?? 1;
                }
                active.RemoveAll(task => task.IsCompleted);
                if (active.Count < serverSlots)
                {
                    using var response = await client.PostAsync("api/pull-gates/claim", null, shutdown);
                    response.EnsureSuccessStatusCode();
                    var claim = await response.Content.ReadFromJsonAsync<PullGateClaim>(Json, shutdown);
                    if (claim is not null)
                        active.Add(ExecuteAsync(client, claim, shutdown));
                }
                await Task.Delay(TimeSpan.FromSeconds(active.Count == serverSlots ? 5 : 2), shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Pull gate poll failed: " + exception.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), shutdown); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            }
        }
        await Task.WhenAll(active);
        return 0;
    }

    private static async Task ExecuteAsync(HttpClient client, PullGateClaim claim, CancellationToken shutdown)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstudio-pull-gate-worker", claim.RunId);
        Directory.CreateDirectory(directory);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var progress = "claimed";
        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var progressSender = SendProgressAsync(client, claim, lines.Reader, lifetime.Token);
        var heartbeat = HeartbeatAsync(client, claim, () => Volatile.Read(ref progress), lifetime);
        FileStream? cacheLease = null;
        try
        {
            using (var request = LeaseRequest(HttpMethod.Get,
                $"api/pull-gates/runs/{claim.RunId}/request.json", claim.Lease))
            {
                using var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token);
                await using var output = File.Create(Path.Combine(directory, "request.json"));
                await input.CopyToAsync(output, lifetime.Token);
            }
            var requestPath = Path.Combine(directory, "request.json");
            var invocation = JsonSerializer.Deserialize<RemoteGateInvocation>(
                await File.ReadAllTextAsync(requestPath, lifetime.Token), Json)
                ?? throw new InvalidDataException("Pull gate request is empty.");
            RemoteGateWorker.Validate(invocation, claim.RunId);
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agentstudio", "source", "cache");
            var cachePath = invocation.SourceCacheKey is { } key
                ? Path.Combine(cacheRoot, key) : null;
            if (cachePath is not null)
                cacheLease = await GateSourceCacheRetention.LockAsync(cachePath + ".lock", lifetime.Token);
            var sourceTimer = Stopwatch.StartNew();
            long workerSourceReadyMs = -1;
            var cacheState = cachePath is null ? GateSourceCacheState.Missing
                : await InspectCacheAsync(cachePath, invocation.Request.ExpectedSha!,
                    directory, lifetime.Token);
            var warm = cacheState == GateSourceCacheState.Ready;
            long sourceBytes = 0;
            if (!warm)
            {
                if (cacheState == GateSourceCacheState.Corrupt && cachePath is not null)
                {
                    var corrupt = cachePath + ".corrupt-" + claim.RunId;
                    Directory.Move(cachePath, corrupt);
                    Directory.Delete(corrupt, recursive: true);
                }
                var tip = cacheState == GateSourceCacheState.HealthyWithoutSubject
                    ? await CacheTipAsync(cachePath!, directory, lifetime.Token) : null;
                using var request = LeaseRequest(HttpMethod.Post,
                    $"api/pull-gates/runs/{claim.RunId}/source-bundle"
                    + (tip is null ? "" : "?base=" + Uri.EscapeDataString(tip)), claim.Lease);
                using var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                response.EnsureSuccessStatusCode();
                var transferDigest = response.Headers.GetValues("X-Source-Bundle-Sha256").Single();
                invocation = invocation with { TransferBundleSha256 = transferDigest };
                await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token);
                await using var output = File.Create(Path.Combine(directory, "source.bundle"));
                await input.CopyToAsync(output, lifetime.Token);
                sourceBytes = output.Length;
            }
            invocation = invocation with { CachePath = cachePath, SourceFromCache = warm };
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(invocation, Json), lifetime.Token);
            sourceTimer.Stop();
            Console.WriteLine($"pull_gate_source_ready run_id={claim.RunId} bytes={sourceBytes} ready_ms={sourceTimer.ElapsedMilliseconds} cache_hit={warm}");
            Volatile.Write(ref progress, "materialized");
            lines.Writer.TryWrite("materialized");
            await RemoteGateWorker.RunAsync(requestPath,
                line =>
                {
                    if (line == "REMOTE_GATE_OBJECTS_READY " + claim.RunId)
                    {
                        cacheLease?.Dispose();
                        cacheLease = null;
                    }
                    if (line.StartsWith("REMOTE_GATE_SOURCE_READY " + claim.RunId + " ", StringComparison.Ordinal)
                        && long.TryParse(line[("REMOTE_GATE_SOURCE_READY " + claim.RunId + " ").Length..],
                            out var readyMs)) workerSourceReadyMs = readyMs;
                    Volatile.Write(ref progress, line);
                    lines.Writer.TryWrite(line);
                }, lifetime.Token);
            lines.Writer.TryComplete();
            await progressSender;
            var artifacts = Path.Combine(directory, "artifacts");
            if (Directory.Exists(artifacts))
            {
                foreach (var file in Directory.EnumerateFiles(artifacts, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    await using var input = File.OpenRead(file);
                    using var request = LeaseRequest(HttpMethod.Put,
                        $"api/pull-gates/runs/{claim.RunId}/artifacts/{Uri.EscapeDataString(name)}",
                        claim.Lease);
                    request.Content = new StreamContent(input);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var uploaded = await client.SendAsync(request, lifetime.Token);
                    uploaded.EnsureSuccessStatusCode();
                }
            }
            var responsePath = Path.Combine(directory, "response.json");
            var result = JsonSerializer.Deserialize<RemoteGateResponse>(
                await File.ReadAllTextAsync(responsePath, lifetime.Token), Json)
                ?? throw new InvalidDataException("Gate worker produced no result.");
            result = result with { Result = result.Result with { Reason = result.Result.Reason
                + $" Source bundle {sourceBytes} bytes transferred in {sourceTimer.Elapsed.TotalSeconds:F1}s."
                + (workerSourceReadyMs < 0 ? " Source ready unavailable."
                    : $" Source ready {(sourceTimer.Elapsed + TimeSpan.FromMilliseconds(workerSourceReadyMs)).TotalSeconds:F1}s.") } };
            using var complete = LeaseRequest(HttpMethod.Post,
                $"api/pull-gates/runs/{claim.RunId}/result", claim.Lease);
            complete.Content = JsonContent.Create(result, options: Json);
            using var receipt = await client.SendAsync(complete, lifetime.Token);
            receipt.EnsureSuccessStatusCode();
            try
            {
                GateSourceCacheRetention.Sweep(cacheRoot, cachePath,
                    TimeProvider.System.GetUtcNow().UtcDateTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Gate source cache sweep failed: " + exception.Message);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Pull gate lease or shutdown cancelled run {claim.RunId}.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Pull gate run {claim.RunId} failed: {exception.Message}");
        }
        finally
        {
            cacheLease?.Dispose();
            lines.Writer.TryComplete();
            lifetime.Cancel();
            try { await progressSender; }
            catch (OperationCanceledException exception) when (lifetime.IsCancellationRequested)
            {
                Console.Error.WriteLine("Gate progress stopped: " + exception.Message);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Gate progress failed: " + exception.Message);
            }
            try { await heartbeat; }
            catch (OperationCanceledException exception) when (lifetime.IsCancellationRequested)
            {
                Console.Error.WriteLine("Gate heartbeat stopped: " + exception.Message);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Gate heartbeat failed at shutdown: " + exception.Message);
            }
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Gate cleanup failed: " + exception.Message);
            }
        }
    }

    internal static async Task<bool> CacheHasSubjectAsync(string cache, string sha, string cwd, CancellationToken ct)
        => await InspectCacheAsync(cache, sha, cwd, ct) == GateSourceCacheState.Ready;

    internal enum GateSourceCacheState { Missing, HealthyWithoutSubject, Ready, Corrupt }

    internal static async Task<GateSourceCacheState> InspectCacheAsync(string cache, string sha,
        string cwd, CancellationToken ct)
    {
        if (!Directory.Exists(cache)) return GateSourceCacheState.Missing;
        try
        {
            await RemoteGateProcess.RunAsync("git", ["-C", cache, "fsck", "--full", "--no-reflogs",
                "--no-progress"], cwd, TimeSpan.FromMinutes(2), ct);
            try
            {
                await RemoteGateProcess.RunAsync("git", ["-C", cache, "cat-file", "-e", sha + "^{commit}"],
                    cwd, TimeSpan.FromMinutes(2), ct);
                return GateSourceCacheState.Ready;
            }
            catch (IOException) { return GateSourceCacheState.HealthyWithoutSubject; }
        }
        catch (IOException)
        {
            return GateSourceCacheState.Corrupt;
        }
    }

    internal static async Task<string?> CacheTipAsync(string cache, string cwd, CancellationToken ct)
    {
        var tips = new List<string>();
        await RemoteGateProcess.RunAsync("git", ["-C", cache, "for-each-ref",
            "--sort=-committerdate", "--format=%(objectname)", "refs/heads/gate-cache"],
            cwd, TimeSpan.FromMinutes(2), ct, tips.Add);
        return tips.FirstOrDefault(RemoteBuildTestGateRunner.ValidSha);
    }

    private static async Task SendProgressAsync(HttpClient client, PullGateClaim claim,
        ChannelReader<string> lines, CancellationToken ct)
    {
        await foreach (var line in lines.ReadAllAsync(ct))
        {
            using var request = LeaseRequest(HttpMethod.Post,
                $"api/pull-gates/runs/{claim.RunId}/progress", claim.Lease);
            request.Content = JsonContent.Create(new PullGateHeartbeat(line), options: Json);
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task HeartbeatAsync(HttpClient client, PullGateClaim claim,
        Func<string> progress, CancellationTokenSource lifetime)
    {
        var expiry = claim.ExpiresAt;
        while (!lifetime.IsCancellationRequested)
        {
            using var request = LeaseRequest(HttpMethod.Post,
                $"api/pull-gates/runs/{claim.RunId}/heartbeat", claim.Lease);
            request.Content = JsonContent.Create(new PullGateHeartbeat(progress()), options: Json);
            try
            {
                using var response = await client.SendAsync(request, lifetime.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    lifetime.Cancel();
                    return;
                }
                response.EnsureSuccessStatusCode();
                expiry = DateTimeOffset.UtcNow.AddSeconds(90);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Gate heartbeat failed for {claim.RunId}: {exception.Message}");
                if (DateTimeOffset.UtcNow >= expiry) { lifetime.Cancel(); return; }
            }
            await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token);
        }
    }

    private static HttpRequestMessage LeaseRequest(HttpMethod method, string path, string lease)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Gate-Lease", lease);
        return request;
    }
}
