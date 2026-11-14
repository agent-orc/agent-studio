using System.Net.Http.Json;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace TaskServer.Tests;

internal static class StudioFactorTestHelper
{
    public static async Task<string> EnrollTotpAsync(HttpClient client, DateTime? now = null)
    {
        var start = await client.PostAsync("/api/v1/studio/auth/totp/start", null);
        start.EnsureSuccessStatusCode();
        var secret = (await start.Content.ReadFromJsonAsync<SetupResponse>())!.Secret;
        var serverNow = now ?? start.Headers.Date?.UtcDateTime
            ?? throw new InvalidOperationException("The test server did not provide a clock for factor setup.");
        var code = SecondFactorCodes.CurrentTotp(secret, serverNow);
        (await client.PostAsJsonAsync("/api/v1/studio/auth/totp/confirm", new StudioConfirmTotpRequest(code)))
            .EnsureSuccessStatusCode();
        return SecondFactorCodes.CurrentTotp(secret, serverNow.AddSeconds(30));
    }

    private sealed record SetupResponse(string Secret);
}
