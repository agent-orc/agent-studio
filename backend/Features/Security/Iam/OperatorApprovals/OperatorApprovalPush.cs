using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AgentStudio.Security;

/// <summary>
/// What a push notification about a new approval request carries: a title, a
/// short message, and the link to the request in Studio. It never carries a
/// token, the parameter digest, or a decision route, so receiving it grants
/// nothing; the Owner still approves in Studio after a step-up (SM-D8 option A).
/// </summary>
public sealed record OperatorApprovalPushMessage(string ApprovalId, string Title, string Message, string Url);

/// <summary>The optional push channel for approval requests (security model SM-5).</summary>
public interface IOperatorApprovalPush
{
    bool Configured { get; }

    Task SendAsync(OperatorApprovalPushMessage message, CancellationToken ct);
}

/// <summary>Configuration under <c>OperatorApprovals</c>: the Studio address links use, and the optional push endpoint.</summary>
public sealed record OperatorApprovalOptions(Uri? StudioUrl, Uri? PushUrl, string? PushTokenFile)
{
    public static OperatorApprovalOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("OperatorApprovals");
        Uri? studio = null;
        if (!string.IsNullOrWhiteSpace(section["StudioUrl"])
            && (!Uri.TryCreate(section["StudioUrl"], UriKind.Absolute, out studio) || !IsSecure(studio)))
            throw new InvalidOperationException("OperatorApprovals:StudioUrl must be an absolute HTTPS URL.");
        Uri? push = null;
        if (!string.IsNullOrWhiteSpace(section["Push:Url"]))
        {
            if (!Uri.TryCreate(section["Push:Url"], UriKind.Absolute, out push) || !IsSecure(push))
                throw new InvalidOperationException("OperatorApprovals:Push:Url must be an absolute HTTPS URL.");
            if (studio is null)
                throw new InvalidOperationException("OperatorApprovals:Push:Url needs OperatorApprovals:StudioUrl for the link.");
        }
        return new OperatorApprovalOptions(studio, push, section["Push:TokenFile"]);
    }

    /// <summary>The Studio link to one approval request; relative when no Studio address is configured.</summary>
    public string Link(string approvalId)
        => $"{StudioUrl?.ToString().TrimEnd('/')}/#/workspace/settings/access?approval={Uri.EscapeDataString(approvalId)}";

    // Plain HTTP is accepted only on loopback, for a local relay or a test endpoint.
    private static bool IsSecure(Uri url) => url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
}

/// <summary>
/// Posts the message as JSON (<c>approvalId</c>, <c>title</c>, <c>message</c>,
/// <c>url</c>) to the configured endpoint, for a relay that forwards it to the
/// Owner's phone. The bearer is read from a file on each call.
/// </summary>
public sealed class HttpsOperatorApprovalPush(HttpClient http, OperatorApprovalOptions options) : IOperatorApprovalPush
{
    public const string HttpClientName = "operator-approval-push";

    public bool Configured => options.PushUrl is not null;

    public async Task SendAsync(OperatorApprovalPushMessage message, CancellationToken ct)
    {
        if (options.PushUrl is null) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, options.PushUrl) { Content = JsonContent.Create(message) };
        if (!string.IsNullOrWhiteSpace(options.PushTokenFile))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(options.PushTokenFile, ct)).Trim());
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The push endpoint answered {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
