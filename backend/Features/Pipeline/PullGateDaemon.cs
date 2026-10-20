using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        try
        {
            foreach (var name in new[] { "request.json", "source.bundle" })
            {
                using var request = LeaseRequest(HttpMethod.Get,
                    $"api/pull-gates/runs/{claim.RunId}/{name}", claim.Lease);
                using var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token);
                await using var output = File.Create(Path.Combine(directory, name));
                await input.CopyToAsync(output, lifetime.Token);
            }
            Volatile.Write(ref progress, "materialized");
            lines.Writer.TryWrite("materialized");
            await RemoteGateWorker.RunAsync(Path.Combine(directory, "request.json"),
                line =>
                {
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
            using var complete = LeaseRequest(HttpMethod.Post,
                $"api/pull-gates/runs/{claim.RunId}/result", claim.Lease);
            complete.Content = JsonContent.Create(result, options: Json);
            using var receipt = await client.SendAsync(complete, lifetime.Token);
            receipt.EnsureSuccessStatusCode();
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
