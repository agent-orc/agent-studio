using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Primitives;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure HTTP-validator policy for the two board reads (<c>GET /api/tasks/</c>
/// and <c>GET /api/tasks/grouped</c>). It knows nothing about tasks: it turns a
/// list of opaque input parts into a strong entity tag, decides whether a
/// request already holds that tag, and shapes the conditional response.
///
/// <para>Kept free of services so the decision is directly testable as a
/// matrix - the same parts produce the same tag, any differing part produces a
/// different one, a matching <c>If-None-Match</c> answers 304 and never builds
/// the body.</para>
///
/// <para>Mirrors the in-repo conditional-GET pattern established by the wiki
/// reads (<c>ProjectDocsService.FormatETag</c> /
/// <c>ProjectDocsEndpoints.ConditionalOk</c>), down to the
/// <c>Cache-Control: no-cache</c> that makes the browser store the response but
/// always revalidate. The one difference is the shape: a wiki read has its
/// payload in hand before it formats the tag, so one method can do both, while a
/// board read keys its body cache by a signature composed before it starts
/// building anything. A signature that is already cached answers
/// <see cref="NotModified"/> without enrichment, lane sorting or
/// serialisation.</para>
///
/// <para>The body tag itself is <see cref="ContentETag"/>: a hash of the
/// built bytes. The signature moves on inputs that leave the board unchanged
/// (an index rescan that found nothing, a runtime fact no card shows), and a
/// signature-derived tag turned each of those into a full 200 for a client
/// that already held the same bytes.</para>
/// </summary>
public static class BoardReadValidator
{
    /// <summary>
    /// Separator between signature parts. A unit separator cannot occur in any
    /// part we compose (generation numbers, timestamps, route names, hex
    /// digests), so two distinct part lists can never collide into one token.
    /// </summary>
    private const char PartSeparator = '';

    /// <summary>Joins signature parts into one opaque cache token.</summary>
    public static string Compose(params string[] parts)
        => string.Join(PartSeparator, parts);

    /// <summary>
    /// Formats a cache token as a quoted strong HTTP entity tag. The token can
    /// carry arbitrary bytes, and a raw token in the ETag header throws in
    /// Kestrel; hashing keeps strong-validator semantics and is always
    /// header-safe.
    /// </summary>
    public static string FormatETag(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return "\"" + Convert.ToHexString(bytes, 0, 16).ToLowerInvariant() + "\"";
    }

    /// <summary>
    /// Strong tag of one built body. <paramref name="scope"/> is the route and
    /// request variant, so two variants that happen to serialize to the same
    /// bytes still never validate each other.
    /// </summary>
    public static string ContentETag(string scope, byte[] body)
        => FormatETag(Compose(scope, Convert.ToHexString(SHA256.HashData(body))));

    /// <summary>
    /// True when the request already holds <paramref name="etag"/>. Follows
    /// RFC 9110 for the cases a board client can produce: the wildcard, a
    /// comma-separated list, and the weak prefix a proxy may have added to an
    /// otherwise identical tag.
    /// </summary>
    public static bool Matches(StringValues ifNoneMatch, string etag)
    {
        foreach (var header in ifNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            foreach (var candidate in header.Split(','))
            {
                var trimmed = candidate.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed == "*") return true;
                if (trimmed.StartsWith("W/", StringComparison.Ordinal)) trimmed = trimmed[2..];
                if (string.Equals(trimmed, etag, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Answers a request that already holds <paramref name="etag"/> with
    /// <c>304 Not Modified</c> and an empty body. Returns null when the client
    /// needs the body, so the caller can start the expensive work only then -
    /// the split exists precisely so a validated board never pays for
    /// enrichment, lane sorting or serialisation.
    /// </summary>
    public static IResult? NotModified(HttpContext http, string etag)
    {
        if (!Matches(http.Request.Headers.IfNoneMatch, etag)) return null;
        ApplyHeaders(http, etag);
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    /// <summary>
    /// Emits a full response under <paramref name="etag"/> so the next request
    /// can be conditional.
    /// </summary>
    public static IResult Ok(HttpContext http, string etag, object payload)
    {
        ApplyHeaders(http, etag);
        return Results.Ok(payload);
    }

    public static IResult Bytes(HttpContext http, string etag, byte[] payload)
    {
        ApplyHeaders(http, etag);
        return Results.Bytes(payload, "application/json");
    }

    /// <summary>
    /// <c>no-cache</c> tells the browser to store the response but always
    /// revalidate, which is what turns the next board poll into a conditional
    /// GET instead of either a blind refetch or a blind cache hit.
    /// </summary>
    private static void ApplyHeaders(HttpContext http, string etag)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "no-cache";
    }
}
