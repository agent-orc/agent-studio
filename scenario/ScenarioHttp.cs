using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Scenario;

/// <summary>Addresses and credentials of one running deployment.</summary>
public sealed record ScenarioEndpoints(
    string TaskServerUrl,
    string? StudioUrl,
    string? ManagementToken,
    string RunnerId,
    string? RunnerToken,
    string? BackupDirectory);

/// <summary>Raised when the deployment answered in a way the scenario rejects.</summary>
public sealed class ScenarioStepException(string message) : Exception(message);

/// <summary>
/// Thin protocol-correct HTTP client. Every request carries the protocol
/// version header, so a scenario run also proves the deployment accepts the
/// protocol the build ships with.
/// </summary>
public sealed class ScenarioHttpClient : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;

    public ScenarioHttpClient(string baseUrl, string? bearerToken, string clientVersion)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        _client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, clientVersion);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken ct)
    {
        using var response = await _client.GetAsync(Relative(path), ct);
        return await ReadAsync<TResponse>(response, "GET", path, ct);
    }

    public async Task<TResponse> PostAsync<TResponse>(string path, object body, CancellationToken ct)
    {
        using var response = await _client.PostAsJsonAsync(Relative(path), body, Json, ct);
        return await ReadAsync<TResponse>(response, "POST", path, ct);
    }

    public async Task<TResponse> PutAsync<TResponse>(string path, object body, CancellationToken ct)
    {
        using var response = await _client.PutAsJsonAsync(Relative(path), body, Json, ct);
        return await ReadAsync<TResponse>(response, "PUT", path, ct);
    }

    /// <summary>Sends a request and returns the status code without parsing.</summary>
    public async Task<int> ProbeAsync(string path, CancellationToken ct)
    {
        using var response = await _client.GetAsync(Relative(path), ct);
        return (int)response.StatusCode;
    }

    private static async Task<TResponse> ReadAsync<TResponse>(
        HttpResponseMessage response,
        string method,
        string path,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ScenarioStepException(
                $"{method} {path} returned {(int)response.StatusCode}: {Trim(body)}");
        if (typeof(TResponse) == typeof(string)) return (TResponse)(object)body;
        var value = JsonSerializer.Deserialize<TResponse>(body, Json);
        return value ?? throw new ScenarioStepException(
            $"{method} {path} returned a body that is not a {typeof(TResponse).Name}: {Trim(body)}");
    }

    private static string Relative(string path) => path.TrimStart('/');

    private static string Trim(string body)
        => body.Length <= 400 ? body : body[..400] + "...";

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Bounded polling. Every wait states its own deadline and its own failure
/// message; there is no sleep longer than the polling contract and no
/// unbounded retry, so a hung deployment fails fast with its own reason.
/// </summary>
public static class ScenarioWait
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public static async Task<T> UntilAsync<T>(
        Func<CancellationToken, Task<T?>> probe,
        TimeSpan timeout,
        string description,
        Action? healthCheck,
        CancellationToken ct)
        where T : class
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            healthCheck?.Invoke();
            try
            {
                if (await probe(ct) is { } value) return value;
            }
            catch (ScenarioStepException exception)
            {
                last = exception;
            }
            catch (HttpRequestException exception)
            {
                last = exception;
            }
            catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
            {
                last = exception;
            }
            await Task.Delay(PollInterval, ct);
        }
        throw new ScenarioStepException(
            $"Timed out after {timeout.TotalSeconds:0} s waiting for {description}."
            + (last is null ? string.Empty : $" Last error: {last.Message}"));
    }
}
