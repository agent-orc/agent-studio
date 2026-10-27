using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AgentStudio.Git;

/// <summary>
/// Responses of the card reads served from the Git fact store (Git facts S3).
/// Each carries the generation it was joined from, its age and its state in
/// headers, and a strong ETag over the response bytes, so a client holding a
/// validator gets 304 without a body. A miss answers <c>202</c> with
/// <c>state: "warming"</c> in the body and <c>Retry-After</c>; the producer has
/// already been asked for the missing fact.
/// </summary>
internal static class GitFactsResults
{
    internal const string GenerationHeader = "X-Git-Facts-Generation";
    internal const string AgeHeader = "X-Git-Facts-Age";
    internal const string StateHeader = "X-Git-Facts-State";
    internal const string WarmingState = "warming";
    internal const int RetryAfterMs = 1_000;

    internal static IResult Ok(object body, GitPublishedFacts? facts) => new JsonFactsResult(body, facts, warming: false);

    /// <summary>The route's normal shape with <c>state: "warming"</c> added, as 202.</summary>
    internal static IResult Warming(object body, GitPublishedFacts? facts) => new JsonFactsResult(body, facts, warming: true);

    /// <summary>A text diff response: 204 when empty, like the route served before.</summary>
    internal static IResult Text(string text, GitPublishedFacts facts) => new TextFactsResult(text, facts, warming: false);

    internal static IResult TextWarming(GitPublishedFacts? facts) => new TextFactsResult("", facts, warming: true);

    private static void Describe(HttpContext context, GitPublishedFacts? facts, bool warming)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-cache";
        headers[StateHeader] = warming ? WarmingState : facts?.State ?? WarmingState;
        if (facts is null) return;
        headers[GenerationHeader] = facts.Generation.ToString(CultureInfo.InvariantCulture);
        if (facts.PublishedAt > DateTimeOffset.MinValue)
        {
            var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            var age = Math.Max(0, (time.GetUtcNow() - facts.PublishedAt).TotalSeconds);
            headers[AgeHeader] = ((long)age).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static async Task Write(HttpContext context, byte[] bytes, string contentType, bool warming,
        bool emptyIsNoContent)
    {
        if (warming)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            context.Response.Headers.RetryAfter = "1";
        }
        else if (emptyIsNoContent && bytes.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        else
        {
            var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..32] + "\"";
            context.Response.Headers.ETag = etag;
            var match = context.Request.Headers.IfNoneMatch.ToString();
            if (match.Length > 0 && match.Split(',').Any(candidate => candidate.Trim() == etag || candidate.Trim() == "*"))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private sealed class JsonFactsResult(object body, GitPublishedFacts? facts, bool warming) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            var options = context.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                ?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
            byte[] bytes;
            if (warming && JsonSerializer.SerializeToNode(body, body.GetType(), options) is JsonObject node)
            {
                node["state"] = WarmingState;
                node["retryAfterMs"] = RetryAfterMs;
                bytes = JsonSerializer.SerializeToUtf8Bytes(node, options);
            }
            else bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), options);
            Describe(context, facts, warming);
            return Write(context, bytes, "application/json; charset=utf-8", warming, emptyIsNoContent: false);
        }
    }

    private sealed class TextFactsResult(string text, GitPublishedFacts? facts, bool warming) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            Describe(context, facts, warming);
            return Write(context, string.IsNullOrWhiteSpace(text) ? [] : Encoding.UTF8.GetBytes(text),
                "text/plain; charset=utf-8", warming, emptyIsNoContent: true);
        }
    }
}
