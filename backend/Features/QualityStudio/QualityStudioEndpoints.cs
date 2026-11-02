using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Secrets;
using AgentStudio.Security;

namespace AgentStudio.QualityStudio;

public static class QualityStudioEndpoints
{
    public const string TokenReference = "secret://workspace/quality-studio/api-token";
    public const string KeyReference = "secret://workspace/quality-studio/signing-key";
    private const string Contract = "agent-studio-integration/1";

    public static void MapQualityStudioEndpoints(this WebApplication app)
    {
        app.MapGet("/.well-known/agent-studio/jwks.json", async (ISecretProvider secrets, CancellationToken ct) =>
        {
            try
            {
                var secret = await secrets.GetForPlatformAsync(SecretReference.Parse(KeyReference), "quality-studio-jwks", ct);
                var seed = Convert.FromBase64String(secret.Value);
                try
                {
                    return Results.Ok(new { keys = new[] { new { kty = "OKP", crv = "Ed25519", kid = Ed25519Assertion.KeyId,
                        alg = "EdDSA", use = "sig", x = Ed25519Assertion.Base64Url(Ed25519Assertion.PublicKey(seed)) } } });
                }
                finally { CryptographicOperations.ZeroMemory(seed); }
            }
            catch (SecretNotFoundException) { return Results.Ok(new { keys = Array.Empty<object>() }); }
        });

        app.MapGet("/api/integrations/quality-studio/connection", (HttpContext context,
            AccessSecurityStore access, QualityStudioConnectionStore connections) =>
        {
            RequireOwner(context, access);
            return Results.Ok(connections.Get());
        });

        app.MapPut("/api/integrations/quality-studio/connection", async (QualityStudioConnection request,
            HttpContext context, AccessSecurityStore access, QualityStudioConnectionStore connections,
            ISecretProvider secrets, CancellationToken ct) =>
        {
            var owner = RequireOwner(context, access);
            if (!ValidBaseUrl(request.InternalBaseUrl, allowHttp: true)
                || !ValidBaseUrl(request.PublicUrl, allowHttp: false)
                || string.IsNullOrWhiteSpace(request.ServiceClientId)
                || request.ServiceClientId.Length > 100)
                return Results.BadRequest(new { error = "invalid-quality-studio-connection" });
            var key = SecretReference.Parse(KeyReference);
            var listing = await secrets.ListAsync(SecretScope.Workspace, ct);
            if (!listing.Records.Any(record => record.Name == key.Name && record.Versions.Any(version => version.Active)))
            {
                var seed = RandomNumberGenerator.GetBytes(32);
                try { await secrets.PutAsync(SecretScope.Workspace, key.Name, Convert.ToBase64String(seed), SecretActor.Human(owner.User.Id), ct); }
                finally { CryptographicOperations.ZeroMemory(seed); }
            }
            connections.Set(request with { InternalBaseUrl = request.InternalBaseUrl.TrimEnd('/'), PublicUrl = request.PublicUrl.TrimEnd('/') });
            return Results.Ok(connections.Get());
        });

        app.MapPost("/api/integrations/quality-studio/token", async (QualityStudioTokenRequest request,
            HttpContext context, AccessSecurityStore access, ISecretProvider secrets, CancellationToken ct) =>
        {
            var owner = RequireOwner(context, access);
            if (string.IsNullOrWhiteSpace(request.Token)) return Results.BadRequest(new { error = "token-required" });
            await secrets.PutAsync(SecretScope.Workspace, SecretReference.Parse(TokenReference).Name,
                request.Token, SecretActor.Human(owner.User.Id), ct);
            return Results.NoContent();
        });

        app.MapGet("/api/integrations/quality-studio/status", async (string project, HttpContext context,
            AccessSecurityStore access, ProjectRegistry registry, ProjectSettingsService settings,
            QualityStudioConnectionStore connections, ISecretProvider secrets, IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var projectRecord = registry.FindByIdOrDisplayName(project);
            if (projectRecord is null || !CanReadProject(context, access, projectRecord, registry)) return Results.NotFound();
            var owner = AccessSecurityEndpoints.FindHuman(context, access)?.User.Role == StudioRoles.Owner;
            var connection = connections.Get();
            if (connection is null) return Results.Ok(new QualityStudioStatus("not-connected", false, owner, null));
            var repositoryId = settings.Get(projectRecord.DisplayName).QualityStudioRepositoryId;
            try
            {
                using var client = clients.CreateClient("quality-studio-integration");
                var token = await secrets.GetForPlatformAsync(SecretReference.Parse(TokenReference), "quality-studio-status", ct);
                using var capabilityRequest = new HttpRequestMessage(HttpMethod.Get, connection.InternalBaseUrl + "/api/v1/capabilities");
                capabilityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
                capabilityRequest.Headers.Add("X-Client-Id", connection.ServiceClientId);
                using var capabilities = await client.SendAsync(capabilityRequest, ct);
                if (!capabilities.IsSuccessStatusCode) return Results.Ok(new QualityStudioStatus("unreachable", true, owner, repositoryId));
                using var document = JsonDocument.Parse(await capabilities.Content.ReadAsStreamAsync(ct));
                if (!document.RootElement.TryGetProperty("contracts", out var contracts)
                    || contracts.ValueKind != JsonValueKind.Array
                    || !contracts.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == Contract))
                    return Results.Ok(new QualityStudioStatus("version-too-old", true, owner, repositoryId));
                if (string.IsNullOrWhiteSpace(repositoryId))
                    return Results.Ok(new QualityStudioStatus("not-registered", true, owner, null));
                using var probe = new HttpRequestMessage(HttpMethod.Get, connection.InternalBaseUrl + "/api/v1/repos");
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
                probe.Headers.Add("X-Client-Id", connection.ServiceClientId);
                using var response = await client.SendAsync(probe, ct);
                if (!response.IsSuccessStatusCode)
                    return Results.Ok(new QualityStudioStatus("unreachable", true, owner, repositoryId));
                using var registrations = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
                var registered = registrations.RootElement.TryGetProperty("repositories", out var repositories)
                    && repositories.ValueKind == JsonValueKind.Array
                    && repositories.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("id", out var id)
                        && id.GetString() == repositoryId);
                return Results.Ok(new QualityStudioStatus(registered ? "ready" : "not-registered", true, owner, repositoryId));
            }
            catch (SecretNotFoundException)
            {
                return Results.Ok(new QualityStudioStatus("not-connected", false, owner, repositoryId));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return Results.Ok(new QualityStudioStatus("unreachable", true, owner, repositoryId));
            }
        });

        app.MapPut("/api/integrations/quality-studio/projects/{project}/repository", (string project,
            QualityStudioRepositoryRequest request, HttpContext context, AccessSecurityStore access,
            ProjectRegistry registry, ProjectSettingsService settings) =>
        {
            RequireOwner(context, access);
            var record = registry.FindByIdOrDisplayName(project);
            if (record is null) return Results.NotFound();
            var id = request.RepositoryId?.Trim();
            if (id is { Length: > 100 } || id?.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) == true)
                return Results.BadRequest(new { error = "invalid-repository-id" });
            settings.SetQualityStudioRepositoryId(record.DisplayName, string.IsNullOrWhiteSpace(id) ? null : id);
            return Results.NoContent();
        });

        app.MapGet("/api/integrations/quality-studio/open", async (string project, string? @return,
            HttpContext context, AccessSecurityStore access, ProjectRegistry registry, ProjectSettingsService settings,
            QualityStudioConnectionStore connections, ISecretProvider secrets, TimeProvider clock, CancellationToken ct) =>
        {
            var human = AccessSecurityEndpoints.FindHuman(context, access);
            if (human is null) return Results.Unauthorized();
            var record = registry.FindByIdOrDisplayName(project);
            if (record is null || !CanReadProject(context, access, record, registry)) return Results.NotFound();
            var repositoryId = settings.Get(record.DisplayName).QualityStudioRepositoryId;
            var connection = connections.Get();
            if (connection is null || string.IsNullOrWhiteSpace(repositoryId)) return Results.NotFound();
            var target = @return ?? "/?repo=" + Uri.EscapeDataString(repositoryId);
            if (!ValidReturnPath(target)) return Results.BadRequest(new { error = "invalid-return-path" });
            var origin = new Uri(connection.PublicUrl).GetLeftPart(UriPartial.Authority);
            var seed = Convert.FromBase64String((await secrets.GetForPlatformAsync(
                SecretReference.Parse(KeyReference), "quality-studio-handoff", ct)).Value);
            string assertion;
            try
            {
                var role = human.User.Role == StudioRoles.Owner ? "owner"
                    : human.User.Role == StudioRoles.Operator ? "operator" : "viewer";
                assertion = Ed25519Assertion.Sign(seed, QualityStudioHandoffClaims.Issue(
                    new UriBuilder(context.Request.Scheme, context.Request.Host.Host, context.Request.Host.Port ?? -1).Uri.GetLeftPart(UriPartial.Authority),
                    human.User.Id, human.Session.Id, role, repositoryId, target,
                    clock.GetUtcNow(), Guid.NewGuid().ToString("N")));
            }
            finally { CryptographicOperations.ZeroMemory(seed); }
            var nonce = Ed25519Assertion.Base64Url(RandomNumberGenerator.GetBytes(18));
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; form-action {origin}; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers.XFrameOptions = "DENY";
            var action = HtmlEncoder.Default.Encode(origin + "/auth/agent-studio");
            var html = $"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Opening Quality Studio</title><body><form id=\"handoff\" method=\"post\" action=\"{action}\"><input type=\"hidden\" name=\"handoff\" value=\"{assertion}\"><noscript><button type=\"submit\">Continue to Quality Studio</button></noscript></form><script nonce=\"{nonce}\">document.getElementById('handoff').submit()</script></body></html>";
            return Results.Content(html, "text/html", Encoding.UTF8);
        });
    }

    public static bool ValidReturnPath(string? path)
        => path is { Length: > 0 and <= 2048 } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal)
           && !path.Contains('\\') && !path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
           && !path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
           && !path.Any(char.IsControl);

    private static bool ValidBaseUrl(string? value, bool allowHttp)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0
           && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
           && (uri.Scheme == Uri.UriSchemeHttps || allowHttp && uri.Scheme == Uri.UriSchemeHttp);

    private static HumanPrincipal RequireOwner(HttpContext context, AccessSecurityStore access)
    {
        var human = AccessSecurityEndpoints.RequireHuman(context, access);
        if (human.User.Role != StudioRoles.Owner)
            throw new SecurityOperationException(403, "owner-required", "Owner role is required.");
        return human;
    }

    private static bool CanReadProject(HttpContext context, AccessSecurityStore access, ProjectRecord project, ProjectRegistry registry)
    {
        var human = AccessSecurityEndpoints.FindHuman(context, access);
        return human is not null
            && access.AuthorizeUser(human.User.Id, IamPermissions.ProjectRead,
                PermissionTarget.ForProject(project.DisplayName),
                (allowed, requested) => ProjectAccessAuthorization.SameProject(allowed, requested, registry)).Allowed;
    }
}

public sealed record QualityStudioTokenRequest(string Token);
public sealed record QualityStudioRepositoryRequest(string? RepositoryId);
public sealed record QualityStudioStatus(string State, bool Configured, bool Owner, string? RepositoryId);
