using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Cached installation routing authority. Claim transactions never await network I/O.</summary>
public sealed class HttpModelRouteAdmission(IHttpClientFactory clients, IOptions<TaskServerOptions> options,
    TimeProvider? timeProvider = null) : IModelRouteAdmission
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly Dictionary<string, (DateTimeOffset At, SelectionResult Result)> _cache = [];
    private readonly Dictionary<string, Task> _pending = [];
    private readonly HashSet<ConfiguredModelRouteDto> _knownModels = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    public IReadOnlyList<ConfiguredModelRouteDto> ConfiguredRoutes
    {
        get { lock (_sync) return _knownModels.ToArray(); }
    }
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    public Task<SelectionResult> SelectAsync(ModelRouteAdmissionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached) && _time.GetUtcNow() - cached.At < Freshness)
                return Task.FromResult(cached.Result);
            if (!_pending.ContainsKey(key) && _pending.Count < 32)
            {
                var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[key] = complete.Task;
                _ = Task.Run(async () =>
                {
                    await _refreshGate.WaitAsync();
                    try
                    {
                        var result = await ReadAuthorityAsync(request);
                        lock (_sync)
                        {
                            if (_cache.Count >= 256)
                                _cache.Remove(_cache.MinBy(entry => entry.Value.At).Key);
                            _cache[key] = (_time.GetUtcNow(), result);
                            foreach (var route in result.EquivalentRoutes)
                                if (_knownModels.Count < 256 && !string.IsNullOrWhiteSpace(route.Model))
                                    _knownModels.Add(new(route.CliType, route.Model));
                        }
                    }
                    finally
                    {
                        _refreshGate.Release();
                        lock (_sync) _pending.Remove(key);
                        complete.TrySetResult();
                    }
                });
            }
        }
        return Task.FromResult(Wait(request, "Model routing read pending; claim waits for fresh installation routing."));
    }

    public Task RefreshesIdle()
    {
        lock (_sync) return Task.WhenAll(_pending.Values.ToArray());
    }

    private async Task<SelectionResult> ReadAuthorityAsync(ModelRouteAdmissionRequest request)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                options.Value.QuotaAdmissionBaseUrl!.TrimEnd('/') + "/api/model-selection/plan")
            {
                Content = JsonContent.Create(request),
            };
            var token = options.Value.ModelSelectionBearerToken;
            if (!string.IsNullOrWhiteSpace(token)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient(nameof(HttpModelRouteAdmission)).SendAsync(message);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SelectionResult>(Json)
                ?? throw new InvalidDataException("Model routing authority returned an empty result.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or InvalidDataException or JsonException or ObjectDisposedException or ArgumentException)
        {
            return Wait(request, "Model routing authority unavailable; claim waits for a successful routing read.");
        }
    }

    private static SelectionResult Wait(ModelRouteAdmissionRequest request, string reason)
    {
        var pins = request.Request.Pins;
        var route = new SelectionRoute(pins.CliType ?? "claude", pins.Model, pins.ThinkingLevel);
        return new() { Outcome = SelectionOutcome.Wait, Primary = route, Route = route,
            Rule = SelectionRules.Capacity, Reason = reason };
    }
}
