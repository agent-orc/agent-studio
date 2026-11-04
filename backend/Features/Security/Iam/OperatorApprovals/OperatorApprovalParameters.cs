using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentStudio.Security;

/// <summary>
/// The request body of a T2 request as the Owner reviews it (security model
/// SM-5). A JSON body is shown indented with the values of secret-like fields
/// masked, any other body as text through <see cref="CredentialRedactor"/>.
/// The view never stores a secret value in the identity store; the parameter
/// digest binds the exact bytes, so masking cannot widen what the approval
/// covers.
/// </summary>
public static partial class OperatorApprovalParameters
{
    public const string Masked = "[masked]";
    private const int MaxShownBytes = 64 * 1024;
    private const int MaxShownChars = 8 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [GeneratedRegex("(secret|password|passphrase|token|credential|private|value|key)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretLikeName();

    /// <summary>The body to show, or <c>null</c> for a request without one. Leaves the body readable for the handler.</summary>
    public static async Task<string?> DisplayAsync(HttpRequest request)
    {
        if (request.ContentLength is 0 || request.ContentLength is null && request.Headers.TransferEncoding.Count == 0) return null;
        if (request.ContentLength is > MaxShownBytes) return $"({request.ContentLength} bytes, not shown)";
        request.EnableBuffering();
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxShownBytes) return "(more than 64 KB, not shown)";
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length == 0 ? null : Display(buffer.ToArray());
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    public static string Display(byte[] body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not null)
            {
                Mask(node, secretContext: false);
                return Truncate(node.ToJsonString(Indented));
            }
        }
        catch (JsonException exception)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "OperatorApprovalParameters: body is not JSON, shown as redacted text");
        }
        return Truncate(CredentialRedactor.Redact(Encoding.UTF8.GetString(body)));
    }

    private static void Mask(JsonNode node, bool secretContext)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    var secret = secretContext || SecretLikeName().IsMatch(name);
                    if (child is JsonValue && secret) obj[name] = Masked;
                    else if (child is JsonValue value && value.TryGetValue<string>(out var text)) obj[name] = CredentialRedactor.Redact(text);
                    else if (child is not null) Mask(child, secret);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue && secretContext) array[index] = Masked;
                    else if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = CredentialRedactor.Redact(text);
                    else if (array[index] is { } child) Mask(child, secretContext);
                }
                break;
        }
    }

    private static string Truncate(string text) => text.Length <= MaxShownChars ? text : text[..MaxShownChars] + "\n(truncated)";
}
