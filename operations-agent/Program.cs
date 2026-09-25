using System.Net.Http.Headers;
using AgentStudio.Operations.Agent;
using AgentStudio.Operations.Contracts;

string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} is required.");
var endpoint = new Uri(Required("OPERATIONS_URL"));
if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && (endpoint.IsLoopback
    || Environment.GetEnvironmentVariable("OPERATIONS_ALLOW_PRIVATE_HTTP") == "true")))
    throw new InvalidOperationException("The Operations Agent requires HTTPS outside an explicitly private network.");
if (!string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query))
    throw new InvalidOperationException("OPERATIONS_URL must be a service origin.");
var tokenFile = Required("OPERATIONS_TOKEN_FILE");
var agentId = Required("OPERATIONS_AGENT_ID");
var bootId = Guid.NewGuid().ToString("N");
using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
{
    BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 32768,
};
client.DefaultRequestHeaders.Add(OperationsProtocol.Header, OperationsProtocol.Version.ToString());
var agent = new OperationsAgentClient(client, agentId, bootId, Required("OPERATIONS_SPOOL_DIRECTORY"), TimeProvider.System);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var failures = 0;
while (!stop.IsCancellationRequested)
{
    try
    {
        // Rotation is picked up without putting credentials in arguments or logs.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile, stop.Token)).Trim());
        await agent.RegisterAsync(stop.Token);
        await agent.PollOnceAsync(stop.Token);
        failures = 0;
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
    {
        if (stop.IsCancellationRequested) break;
        failures = Math.Min(failures + 1, 5);
        Console.Error.WriteLine($"operations-channel-unavailable retry={failures}");
    }
    try { await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 2 : Math.Pow(2, failures)), stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
}
